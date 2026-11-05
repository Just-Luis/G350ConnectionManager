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
    public const string ToggleArgument = "--ics-toggle";

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

    // ------------------------------------------------------------ toggle side

    /// <summary>
    /// Enables or disables ICS by running the elevated helper.
    /// Returns (ok, message); never throws.
    /// </summary>
    public async Task<(bool Ok, string Message)> SetSharingAsync(
        bool enable, string sourceName, string targetName)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return (false, "The app could not locate its own executable.");

        var outPath = Path.Combine(Path.GetTempPath(), $"g350-ics-toggle-{Guid.NewGuid():N}.json");
        var op = enable ? "enable" : "disable";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{ToggleArgument} {op} \"{sourceName}\" \"{targetName}\" \"{outPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return (false, "Could not start the elevated helper.");

            await proc.WaitForExitAsync();

            if (!File.Exists(outPath))
                return (false, "The elevated helper produced no result.");

            var payload = JsonSerializer.Deserialize<TogglePayload>(
                await File.ReadAllTextAsync(outPath));

            if (payload is null)
                return (false, "The elevated helper returned unreadable data.");

            if (!payload.Ok) return (false, payload.Message);

            // Applied is false when Windows had not settled within the helper's
            // own timeout. Report that honestly rather than showing a stale panel.
            return payload.Applied
                ? (true, payload.Message)
                : (false, payload.Message + " Windows has not confirmed the change yet — press Refresh in a moment.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.NativeErrorCode == 1223
                ? (false, "The Windows elevation prompt was declined. Nothing was changed.")
                : (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    /// <summary>
    /// Elevated helper for enabling/disabling sharing.
    ///
    /// Order matters and was determined empirically: the *private* (target) side
    /// must be enabled before the public (source) side, and disabled in the
    /// reverse order. Doing it the other way round leaves ICS half-configured.
    /// </summary>
    public static int RunToggle(string[] args, string outPath)
    {
        var payload = new TogglePayload();

        try
        {
            var enable = args.Length > 1 && args[1].Equals("enable", StringComparison.OrdinalIgnoreCase);
            var sourceName = args.Length > 2 ? args[2] : string.Empty;
            var targetName = args.Length > 3 ? args[3] : string.Empty;

            if (sourceName.Length == 0 || targetName.Length == 0)
            {
                payload.Message = "No source or target adapter was given.";
                WriteToggle(outPath, payload);
                return 3;
            }

            if (sourceName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                payload.Message = "The source and target adapters are the same.";
                WriteToggle(outPath, payload);
                return 3;
            }

            var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (type is null)
            {
                payload.Message = "Internet Connection Sharing is not available on this system.";
                WriteToggle(outPath, payload);
                return 3;
            }

            dynamic mgr = Activator.CreateInstance(type)!;
            dynamic? src = FindConnection(mgr, sourceName);
            dynamic? dst = FindConnection(mgr, targetName);

            if (src is null || dst is null)
            {
                payload.Message = src is null
                    ? $"Adapter '{sourceName}' was not found. Reconnect the console and try again."
                    : $"Adapter '{targetName}' was not found. Reconnect the console and try again.";
                WriteToggle(outPath, payload);
                return 3;
            }

            dynamic cfgSrc = mgr.INetSharingConfigurationForINetConnection(src);
            dynamic cfgDst = mgr.INetSharingConfigurationForINetConnection(dst);

            if (enable)
            {
                // Any previous sharing must be cleared first, or EnableSharing
                // throws when another connection is already public.
                DisableAllSharing(mgr);

                cfgDst.EnableSharing(1);   // 1 = private (shared side)
                cfgSrc.EnableSharing(0);   // 0 = public  (internet source)
                payload.Ok = true;
                payload.Message = $"Sharing {sourceName} → {targetName}.";

                // Wait here, inside the elevated process, for Windows to finish.
                // Polling from the UI would raise a fresh UAC prompt on every
                // read — one approval has to cover the whole operation.
                payload.Applied = WaitForState(targetName, true);
            }
            else
            {
                try { cfgSrc.DisableSharing(); } catch { }
                try { cfgDst.DisableSharing(); } catch { }
                payload.Ok = true;
                payload.Message = "Internet sharing turned off.";

                payload.Applied = WaitForState(targetName, false);
            }
        }
        catch (Exception ex)
        {
            payload.Ok = false;
            payload.Message = $"{ex.GetType().Name}: {ex.Message}";
        }

        WriteToggle(outPath, payload);
        return payload.Ok ? 0 : 3;
    }

    /// <summary>
    /// Blocks until the target connection's sharing state matches <paramref name="expected"/>,
    /// or the timeout expires. ICS needs several seconds to renumber the adapter on
    /// enable and to tear the configuration down on disable.
    /// </summary>
    private static bool WaitForState(string targetName, bool expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(900);

            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
                if (type is null) return false;

                dynamic mgr = Activator.CreateInstance(type)!;
                dynamic? conn = FindConnection(mgr, targetName);
                if (conn is null) continue;

                dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);
                if ((bool)cfg.SharingEnabled == expected) return true;
            }
            catch
            {
                // Adapter briefly disappears while being renumbered.
            }
        }

        return false;
    }

    private static void WriteToggle(string outPath, TogglePayload payload)
    {
        try { File.WriteAllText(outPath, JsonSerializer.Serialize(payload, JsonOpts)); }
        catch { }
    }

    private static dynamic? FindConnection(dynamic mgr, string name)
    {
        foreach (dynamic conn in mgr.EnumEveryConnection)
        {
            dynamic props = mgr.NetConnectionProps(conn);
            if (string.Equals((string)props.Name, name, StringComparison.OrdinalIgnoreCase))
                return conn;
        }
        return null;
    }

    private static void DisableAllSharing(dynamic mgr)
    {
        foreach (dynamic conn in mgr.EnumEveryConnection)
        {
            try
            {
                dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);
                if ((bool)cfg.SharingEnabled) cfg.DisableSharing();
            }
            catch { }
        }
    }

    private sealed class TogglePayload
    {
        public bool Ok { get; set; }
        public bool Applied { get; set; }
        public string Message { get; set; } = string.Empty;
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
