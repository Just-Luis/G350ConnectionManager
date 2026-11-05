using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>
/// Read-only view of Windows Internet Connection Sharing.
///
/// The COM API (HNetCfg.HNetShare) <b>refuses to answer at all</b> without
/// elevation — un-elevated, EnumEveryConnection yields a single null entry and
/// EnumPublicConnections throws E_ACCESSDENIED. There is no partial read
/// available to an asInvoker process.
///
/// So reading ICS means running an elevated helper. Rather than ship a second
/// executable or depend on PowerShell, this re-launches the app's own binary
/// with <c>--ics-probe</c>, which writes a JSON snapshot to a temp file and
/// exits. Only this operation is elevated; the UI stays asInvoker.
/// </summary>
public sealed class IcsService
{
    public const string ProbeArgument = "--ics-probe";

    private const string ServiceName = "SharedAccess";

    /// <summary>Last error / progress text for the caller's log.</summary>
    public string LastMessage { get; private set; } = string.Empty;

    /// <summary>
    /// Reads ICS state by running the elevated probe.
    /// Returns a failed snapshot (never throws) when elevation is declined.
    /// </summary>
    public async Task<IcsSnapshot> ReadAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return IcsSnapshot.Failed("The app could not locate its own executable.");

        var outPath = Path.Combine(Path.GetTempPath(), $"g350-ics-{Guid.NewGuid():N}.json");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{ProbeArgument} \"{outPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return IcsSnapshot.Failed("Could not start the elevated ICS reader.");

            await proc.WaitForExitAsync();

            if (!File.Exists(outPath))
                return IcsSnapshot.Failed("The elevated reader produced no result.");

            var json = await File.ReadAllTextAsync(outPath);
            var payload = JsonSerializer.Deserialize<ProbePayload>(json);
            if (payload is null)
                return IcsSnapshot.Failed("The elevated reader returned unreadable data.");

            if (payload.Error is { Length: > 0 })
            {
                LastMessage = payload.Error;
                return IcsSnapshot.Failed(payload.Error);
            }

            LastMessage = $"Read {payload.Connections.Count} connections.";
            return new IcsSnapshot(
                Ok: true,
                Message: LastMessage,
                SharingInstalled: payload.SharingInstalled,
                ServiceRunning: payload.ServiceRunning,
                ScopeAddress: payload.ScopeAddress,
                Connections: payload.Connections);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED: the UAC prompt was dismissed.
            var msg = ex.NativeErrorCode == 1223
                ? "Administrator approval is required to read internet sharing. "
                  + "Nothing was changed."
                : ex.Message;
            LastMessage = msg;
            return IcsSnapshot.Failed(msg);
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            return IcsSnapshot.Failed(ex.Message);
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    // ------------------------------------------------------------ helper side

    /// <summary>
    /// Entry point for the elevated helper. Writes a JSON result and returns an
    /// exit code. Must never throw: the parent only sees "no file" otherwise.
    /// </summary>
    public static int RunProbe(string outPath)
    {
        var payload = new ProbePayload();

        try
        {
            payload.ServiceRunning = IsServiceRunning();
            payload.ScopeAddress = ReadScopeAddress();

            var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (type is null)
            {
                payload.Error = "Internet Connection Sharing is not registered on this system.";
            }
            else
            {
                // `dynamic` resolves through IDispatch. Hand-declared [ComImport]
                // interfaces fail here with DISP_E_MEMBERNOTFOUND because these
                // objects are dual interfaces and the vtable slots do not line up.
                dynamic mgr = Activator.CreateInstance(type)!;
                payload.SharingInstalled = (bool)mgr.SharingInstalled;

                foreach (dynamic conn in mgr.EnumEveryConnection)
                {
                    var entry = new IcsConnection("", "", "", false, false, 0);

                    try
                    {
                        dynamic props = mgr.NetConnectionProps(conn);
                        entry = entry with
                        {
                            Name = (string)props.Name,
                            DeviceName = (string)props.DeviceName,
                            Guid = (string)props.Guid,
                            IsConnected = (int)props.Status == 2
                        };
                    }
                    catch (Exception ex)
                    {
                        payload.Error = $"Could not read a connection: {ex.Message}";
                        continue;
                    }

                    try
                    {
                        dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);
                        entry = entry with
                        {
                            SharingEnabled = (bool)cfg.SharingEnabled,
                            SharingType = (int)cfg.SharingConnectionType
                        };
                    }
                    catch
                    {
                        // Sharing config is unavailable for some virtual adapters;
                        // the connection itself is still worth reporting.
                    }

                    payload.Connections.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            payload.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, JsonOpts));
        }
        catch
        {
            return 2;
        }

        return payload.Error is null ? 0 : 3;
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ICS always assigns the shared adapter an address in this scope; reading it
    /// explains the 192.168.137.x addresses the user sees.
    /// </summary>
    private static string? ReadScopeAddress()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters");
            return key?.GetValue("ScopeAddress") as string;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private sealed class ProbePayload
    {
        public bool SharingInstalled { get; set; }
        public bool ServiceRunning { get; set; }
        public string? ScopeAddress { get; set; }
        public string? Error { get; set; }
        public List<IcsConnection> Connections { get; set; } = new();
    }
}
