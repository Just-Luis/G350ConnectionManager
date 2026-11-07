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
    public const string DiagArgument = "--ics-diag";
    public const string RepairArgument = "--ics-repair";

    private const string ServiceName = "SharedAccess";

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
                return IcsSnapshot.Failed(payload.Error);

            return new IcsSnapshot(
                Ok: true,
                Message: $"Read {payload.Connections.Count} connections.",
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
            return IcsSnapshot.Failed(msg);
        }
        catch (Exception ex)
        {
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
    /// Returns (ok, message, After); never throws.
    ///
    /// <paramref name="targetGuid"/> identifies the console adapter independently of
    /// its name. An adapter name belongs to the physical USB port, so it changes
    /// when the cable moves; the GUID does not. It is optional so a name-only
    /// call still works, but passing it is what lets a port move be recovered
    /// without the user first pressing Check.
    /// </summary>
    public async Task<(bool Ok, string Message, IcsSnapshot? After)> SetSharingAsync(
        bool enable, string sourceName, string targetName, string? targetGuid = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return (false, "The app could not locate its own executable.", null);

        var outPath = Path.Combine(Path.GetTempPath(), $"g350-ics-toggle-{Guid.NewGuid():N}.json");
        var op = enable ? "enable" : "disable";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{ToggleArgument} {op} \"{sourceName}\" \"{targetName}\" \"{outPath}\" "
                          + $"\"{targetGuid ?? string.Empty}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return (false, "Could not start the elevated helper.", null);

            await proc.WaitForExitAsync();

            if (!File.Exists(outPath))
                return (false, "The elevated helper produced no result.", null);

            var payload = JsonSerializer.Deserialize<TogglePayload>(
                await File.ReadAllTextAsync(outPath));

            if (payload is null)
                return (false, "The elevated helper returned unreadable data.", null);

            // The helper ran elevated and already observed the settled state, so
            // it hands back the post-change snapshot. Re-reading from here would
            // launch a second elevated process — i.e. a second UAC prompt — for
            // information that was just collected inside the first one.
            var after = payload.Snapshot is null
                ? null
                : new IcsSnapshot(
                    Ok: true,
                    Message: payload.Message,
                    ServiceRunning: payload.Snapshot.ServiceRunning,
                    ScopeAddress: payload.Snapshot.ScopeAddress,
                    Connections: payload.Snapshot.Connections);

            if (!payload.Ok) return (false, payload.Message, after);

            // Applied is false when Windows had not settled within the helper's
            // own timeout. Report that honestly rather than showing a stale panel.
            return payload.Applied
                ? (true, payload.Message, after)
                : (false, payload.Message + " Windows has not confirmed the change yet.", after);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.NativeErrorCode == 1223
                ? (false, "The Windows elevation prompt was declined. Nothing was changed.", null)
                : (false, ex.Message, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
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

            // The UI sends the console adapter's GUID alongside its name, so the
            // target can still be identified after Windows has renamed it. An
            // adapter name is bound to a physical USB port, not to the device:
            // moving the cable turns "Ethernet 2" into "Ethernet 4" or
            // "Ethernet 5".
            var targetGuid = args.Length > 5 ? args[5] : string.Empty;

            if (sourceName.Length == 0)
            {
                payload.Message = "No internet source adapter was given.";
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

            if (!enable)
            {
                // Turning sharing off must never depend on the console still being
                // plugged in.
                //
                // Disabling used to look both adapters up by name and abandon the
                // whole operation when either was missing. Unplugging the console
                // therefore left the WiFi side shared, with ICS still pinned to an
                // adapter that no longer existed - the state that makes every later
                // EnableSharing fail with 0x80040201 until Network Connections is
                // fixed by hand.
                //
                // Clearing *all* sharing instead needs no lookup at all, so it works
                // whether the console is present, absent, or has come back under a
                // different name.
                payload.Applied = ClearAllSharing(mgr);
                payload.Ok = payload.Applied;
                payload.Message = payload.Applied
                    ? "Internet sharing turned off."
                    : "Windows did not confirm that sharing was turned off ("
                      + (LastClearFailures ?? "unknown adapter")
                      + "). If it still shows as shared, clear it in Network "
                      + "Connections (ncpa.cpl).";
            }
            else
            {
                if (targetName.Length == 0 && targetGuid.Length == 0)
                {
                    payload.Message = "No console adapter was given.";
                    WriteToggle(outPath, payload);
                    return 3;
                }

                // Refuse early rather than let EnableSharing fail with a bare COM
                // code. On this hardware the private side fails with 0x80040201
                // whenever ICS itself is not healthy, and that is indistinguishable
                // from an adapter problem unless it is checked first.
                var health = IcsHealth();
                if (health is not null)
                {
                    payload.Message = health;
                    WriteToggle(outPath, payload);
                    return 3;
                }

                // The source is the internet-carrying adapter and is always a real,
                // stable piece of hardware, so resolving it by name is fine.
                dynamic? src = FindConnection(mgr, sourceName);
                if (src is null)
                {
                    payload.Message =
                        $"Internet source '{sourceName}' was not found. Reconnect Wi-Fi or "
                        + "Ethernet, then press Check.";
                    WriteToggle(outPath, payload);
                    return 3;
                }

                // The target is resolved by GUID first: the console can be reached
                // after a port move, when its name no longer matches anything the
                // UI knew about, but its GUID still identifies it.
                dynamic? dst = FindConnection(mgr, targetName, targetGuid);
                if (dst is null)
                {
                    payload.Message = targetGuid.Length > 0
                        ? "The console adapter could not be found. Reconnect the console, "
                          + "wait for it to appear, then press Check."
                        : $"Adapter '{targetName}' was not found. Reconnect the console and try again.";
                    WriteToggle(outPath, payload);
                    return 3;
                }

                var resolvedTargetName = ConnectionName(mgr, dst) ?? targetName;

                if (sourceName.Equals(resolvedTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    payload.Message = "The source and target adapters are the same.";
                    WriteToggle(outPath, payload);
                    return 3;
                }

                // Any previous sharing must be cleared first, or EnableSharing
                // throws when another connection is already public.
                DisableAllSharing(mgr);

                dynamic cfgDst = mgr.INetSharingConfigurationForINetConnection(dst);
                dynamic cfgSrc = mgr.INetSharingConfigurationForINetConnection(src);

                cfgDst.EnableSharing(1);   // 1 = private (shared side)
                cfgSrc.EnableSharing(0);   // 0 = public  (internet source)
                payload.Message = $"Sharing {sourceName} → {resolvedTargetName}.";

                // ICS is supposed to renumber the shared adapter into its scope
                // on its own, and usually does. On some machines it does not and
                // the adapter is left on a stale address instead - observed here
                // as 192.168.0.1, which is in no way related to this project.
                // The console carries 192.168.137.2 and reaches a gateway only at
                // 192.168.137.1, so sharing appears to succeed while the console
                // gets nothing.
                //
                // This runs inside the helper that is already elevated, so the
                // correction costs no extra elevation prompt.
                var renumbed = EnsureScopeAddress(resolvedTargetName);

                // Wait here, inside the elevated process, for Windows to finish.
                // Polling from the UI would raise a fresh UAC prompt on every
                // read — one approval has to cover the whole operation.
                payload.Applied = WaitForState(resolvedTargetName, true);
                payload.Ok = payload.Applied;
                if (!payload.Applied)
                    payload.Message += " Windows has not confirmed the change yet.";
                else if (renumbed is not null)
                    payload.Message += " " + renumbed;
            }
        }
        catch (Exception ex)
        {
            payload.Ok = false;
            payload.Message = ExplainIcs(ex);
        }

        // Collect the settled state while still elevated, so the caller does not
        // have to raise a second elevation prompt just to refresh the panel.
        payload.Snapshot = CaptureState();

        WriteToggle(outPath, payload);
        return payload.Ok ? 0 : 3;
    }

    /// <summary>Reads ICS state. Caller must already be elevated.</summary>
    private static ProbePayload CaptureState()
    {
        var payload = new ProbePayload();

        try
        {
            payload.ServiceRunning = IsServiceRunning();
            payload.ScopeAddress = ReadScopeAddress();

            var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (type is null) return payload;

            dynamic mgr = Activator.CreateInstance(type)!;

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

                    dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);
                    entry = entry with
                    {
                        SharingEnabled = (bool)cfg.SharingEnabled,
                        SharingType = (int)cfg.SharingConnectionType
                    };
                }
                catch
                {
                    continue;
                }

                payload.Connections.Add(entry);
            }
        }
        catch (Exception ex)
        {
            payload.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return payload;
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

    /// <summary>
    /// Finds a connection by adapter GUID first, then by name.
    ///
    /// The GUID is stable across the renames Windows performs when the console
    /// moves USB port, so it is the reliable identity. The name is kept as a
    /// fallback for callers that only have one.
    /// </summary>
    private static dynamic? FindConnection(dynamic mgr, string name, string? guid = null)
    {
        if (!string.IsNullOrEmpty(guid))
        {
            foreach (dynamic conn in mgr.EnumEveryConnection)
            {
                try
                {
                    if (string.Equals((string)mgr.NetConnectionProps(conn).Guid, guid,
                                      StringComparison.OrdinalIgnoreCase))
                        return conn;
                }
                catch { }
            }
        }

        if (string.IsNullOrEmpty(name)) return null;

        foreach (dynamic conn in mgr.EnumEveryConnection)
        {
            try
            {
                if (string.Equals((string)mgr.NetConnectionProps(conn).Name, name,
                                  StringComparison.OrdinalIgnoreCase))
                    return conn;
            }
            catch { }
        }

        return null;
    }

    /// <summary>Current adapter name of a connection object, or null.</summary>
    private static string? ConnectionName(dynamic mgr, dynamic conn)
    {
        try { return (string)mgr.NetConnectionProps(conn).Name; }
        catch { return null; }
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

    /// <summary>
    /// Turns sharing off on every connection and reports whether it actually
    /// ended, so the caller never claims success it did not observe.
    ///
    /// Unlike <see cref="DisableAllSharing"/> this is not best-effort: each
    /// connection is re-read afterwards, and one that still reports sharing as
    /// enabled keeps the result false. That distinction matters because the
    /// previous code set "turned off" unconditionally, which is how a failed
    /// disable could look like a success.
    /// </summary>
    private static bool ClearAllSharing(dynamic mgr)
    {
        var failures = new List<string>();

        foreach (dynamic conn in mgr.EnumEveryConnection)
        {
            try
            {
                dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);
                if (!(bool)cfg.SharingEnabled) continue;

                cfg.DisableSharing();

                // Re-read on a fresh interface object; the one above can cache.
                dynamic check = mgr.INetSharingConfigurationForINetConnection(conn);
                if ((bool)check.SharingEnabled)
                    failures.Add(ConnectionName(mgr, conn) ?? "an adapter");
            }
            catch (Exception ex)
            {
                failures.Add(ConnectionName(mgr, conn) ?? ex.Message);
            }
        }

        if (failures.Count == 0) return true;

        LastClearFailures = string.Join(", ", failures);
        return false;
    }

    /// <summary>Adapters that could not be un-shared by the last disable.</summary>
    private static string? LastClearFailures { get; set; }

    private sealed class TogglePayload
    {
        public bool Ok { get; set; }
        public bool Applied { get; set; }
        public string Message { get; set; } = string.Empty;
        public ProbePayload? Snapshot { get; set; }
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

    // ------------------------------------------------------------ diagnostics

    /// <summary>
    /// Elevated, read-only dump of every fact needed to reason about a sharing
    /// failure. Changes nothing.
    ///
    /// Written after an earlier investigation had to infer ICS's internal state
    /// from symptoms. It reports the four registry values ICS uses to remember
    /// what was being shared - including ones that may be absent, which is itself
    /// the evidence that a previous repair deleted them - plus the service
    /// states and the live connection list.
    /// </summary>
    public static int RunDiag(string outPath)
    {
        var lines = new List<string>
        {
            $"G350 Companion ICS diagnostics · {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            string.Empty
        };

        lines.Add("--- services ---");
        lines.Add($"SharedAccess        : {ServiceState("SharedAccess")}");
        lines.Add($"NetBT               : {ServiceState("NetBT")}");
        lines.Add($"SharedAccess start  : {ServiceStartType("SharedAccess")}");
        lines.Add(string.Empty);

        lines.Add("--- HKLM\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess\\Parameters ---");
        AddRegistryValue(lines, "ScopeAddress");
        AddRegistryValue(lines, "ScopeAddressBackup");
        AddRegistryValue(lines, "PublicConnection");
        AddRegistryValue(lines, "PrivateConnection");
        AddRegistryValue(lines, "ScopedAddress");
        lines.Add(string.Empty);
        lines.Add("ScopeAddress is the subnet ICS hands the shared adapter; the live");
        lines.Add("scope is visible above as the shared adapter's own address. They can");
        lines.Add("disagree: ICS keeps whatever address the adapter already had when");
        lines.Add("sharing was turned on, which is why a console left on the wrong");
        lines.Add("subnet has no gateway even though sharing reports as active.");
        lines.Add("ScopeAddressBackup is what Windows restores ScopeAddress from.");
        lines.Add(string.Empty);
        lines.Add("A MISSING ScopeAddress is normal before sharing has ever been enabled:");
        lines.Add("ICS writes it itself on first enable. It is reported here for");
        lines.Add("completeness, not because it blocks anything.");
        lines.Add(string.Empty);

        lines.Add("--- local candidates (read without elevation) ---");
        lines.Add($"Console addresses .137.2 / .7.2 : {Reach("192.168.137.2")} / {Reach("192.168.7.2")}");
        lines.Add(string.Empty);

        lines.Add("--- HNetCfg.HNetShare ---");
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (type is null)
            {
                lines.Add("NOT REGISTERED on this system.");
            }
            else
            {
                dynamic mgr = Activator.CreateInstance(type)!;
                lines.Add($"SharingInstalled    : {(bool)mgr.SharingInstalled}");
                lines.Add(string.Empty);
                lines.Add($"{"name",-20} {"sharing",-8} {"type",-10} {"status",-5} {"address",-16} guid");

                foreach (dynamic conn in mgr.EnumEveryConnection)
                {
                    try
                    {
                        dynamic props = mgr.NetConnectionProps(conn);
                        dynamic cfg = mgr.INetSharingConfigurationForINetConnection(conn);

                        var sharing = (bool)cfg.SharingEnabled;
                        var stype = sharing
                            ? ((int)cfg.SharingConnectionType == 0 ? "public" : "private")
                            : "-";
                        var status = (int)props.Status == 2 ? "up" : "down";

                        // The address matters as much as the flags: the console can
                        // only reach a gateway when the shared adapter sits on
                        // 192.168.137.1, and the console carries a matching
                        // 192.168.137.2 on usb0.
                        lines.Add($"{Trunc((string)props.Name, 20),-20} "
                                + $"{sharing,-8} {stype,-10} {status,-5} "
                                + $"{AdapterAddress((string)props.Name),-16} {(string)props.Guid}");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"<unreadable connection: {ex.GetType().Name}: {ex.Message}>");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            File.WriteAllText(outPath, string.Join(Environment.NewLine, lines));
            return 0;
        }
        catch
        {
            return 2;
        }
    }

    private static void AddRegistryValue(List<string> lines, string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters");

            var value = key?.GetValue(name);
            lines.Add($"{name,-20}: {(value is null ? "<MISSING>" : value.ToString())}");
        }
        catch (Exception ex)
        {
            lines.Add($"{name,-20}: <unreadable: {ex.Message}>");
        }
    }

    /// <summary>
    /// True when the SharedAccess service is installed but its start type is
    /// Disabled, which means it cannot be started at all.
    ///
    /// This matters because <see cref="IcsHealth"/> tells the user to "start it
    /// and try again" - advice that is impossible to follow while the service is
    /// disabled. Detecting it separately lets the app say something actionable.
    /// </summary>
    private static bool IsServiceDisabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key?.GetValue("Start") is int start && start == 4;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restores the SharedAccess service to Manual and starts it.
    ///
    /// Only ever called from an explicit <c>--ics-repair</c> invocation. Setting
    /// a service's start type is a machine-wide change, so it is never performed
    /// automatically as a side effect of a status probe or a sharing toggle.
    ///
    /// The start type is written through <c>sc.exe config</c> rather than the
    /// registry directly, so Windows performs its own bookkeeping. Returns a
    /// sentence describing the outcome.
    /// </summary>
    private static string RepairService()
    {
        // Start type is checked BEFORE the running state. A disabled service can
        // still be reported as running - Windows leaves the process up, it just
        // refuses to start it again - so testing "is it running" first made the
        // repair report "already running" and leave the service disabled.
        if (IsServiceDisabled())
        {
            var config = Run("sc.exe", $"config {ServiceName} start= demand", timeoutMs: 10000);
            if (config.ExitCode != 0)
                return "The service start type could not be changed. " + Collapse(config.Output);

            if (IsServiceRunning())
                return "The Internet Connection Sharing service was re-enabled "
                     + "(it was still running).";

            Run("sc.exe", "start " + ServiceName, timeoutMs: 20000);

            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsServiceRunning())
                    return "The Internet Connection Sharing service was re-enabled and started.";
                Thread.Sleep(500);
            }

            return "The service start type was restored to Manual, but it did not start. "
                 + "A reboot may be required.";
        }

        if (!ServiceStartTypeIsUsable())
        {
            // Not Disabled but also not startable: report rather than guess.
            return "The Internet Connection Sharing service is not running and its "
                 + "start type is not Manual. Set it to Manual in Services, then retry.";
        }

        if (IsServiceRunning())
            return "The Internet Connection Sharing service is already running.";

        var start = Run("sc.exe", "start " + ServiceName, timeoutMs: 20000);
        return IsServiceRunning()
            ? "The Internet Connection Sharing service is now running."
            : "The Internet Connection Sharing service could not be started. "
              + Collapse(start.Output);
    }

    /// <summary>True when the start type allows the service to be started.</summary>
    private static bool ServiceStartTypeIsUsable()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key?.GetValue("Start") is int start && start is 2 or 3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Entry point for <c>--ics-repair</c>: re-enable and start the ICS service
    /// if it has been disabled. Changes nothing else.
    ///
    /// Deliberately does not touch the SharedAccess\Parameters registry values.
    /// Sharing works with ScopeAddress absent, and deleting those values is what
    /// damaged this class of machine in the first place.
    /// </summary>
    public static int RunRepair(string outPath)
    {
        var lines = new List<string>
        {
            $"G350 Companion ICS repair · {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            string.Empty,
            $"SharedAccess before : {ServiceState(ServiceName)}",
            $"Start type before   : {ServiceStartType(ServiceName)}",
            string.Empty
        };

        try
        {
            lines.Add(RepairService());
        }
        catch (Exception ex)
        {
            lines.Add($"Repair failed: {ex.Message}");
        }

        lines.Add(string.Empty);
        lines.Add($"SharedAccess after  : {ServiceState(ServiceName)}");
        lines.Add($"Start type after    : {ServiceStartType(ServiceName)}");

        try
        {
            File.WriteAllText(outPath, string.Join(Environment.NewLine, lines));
            return IsServiceRunning() ? 0 : 3;
        }
        catch
        {
            return 2;
        }
    }

    private static string ServiceState(string name)
    {
        try
        {
            var output = Run("sc.exe", "query " + name).Output;
            var line = output.Replace("\r", "")
                             .Split('\n')
                             .FirstOrDefault(l => l.Contains("STATE"));
            return line is null ? "unknown" : Collapse(line);
        }
        catch { return "unknown"; }
    }

    private static string ServiceStartType(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);

            return key?.GetValue("Start") switch
            {
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                var other => other?.ToString() ?? "unknown"
            };
        }
        catch { return "unknown"; }
    }

    private static string AdapterAddress(string adapterName)
    {
        try
        {
            var output = Run("netsh", $"interface ipv4 show addresses name=\"{adapterName}\"")
                .Output.Replace("\r", "");

            var line = output.Split('\n')
                             .FirstOrDefault(l => l.TrimStart()
                                 .StartsWith("IP Address:", StringComparison.OrdinalIgnoreCase));

            return line is null ? "(none)" : line.Split(':')[1].Trim();
        }
        catch { return "(unknown)"; }
    }

    /// <summary>
    /// Puts the shared adapter on the ICS scope address if it is not already there.
    ///
    /// Internet Connection Sharing assigns its private interface an address in
    /// the scope recorded under SharedAccess\Parameters (192.168.137.1 by
    /// default). On this machine that renumbering does not always happen: after
    /// enabling, the console adapter has been observed holding 192.168.0.1, an
    /// address unrelated to anything the project uses. EnableSharing reports
    /// success and the panel says sharing is on, while the console - which only
    /// ever carries 192.168.7.2 and 192.168.137.2 - has no gateway and therefore
    /// no internet.
    ///
    /// The scope value is read rather than assumed, so a machine configured with
    /// a different scope still works. "ScopeAddressBackup" is used when
    /// "ScopeAddress" is absent, which is the normal state before sharing has
    /// ever been enabled. Falling back again to 192.168.137.1 matches the value
    /// the console's own script probes for, so the two agree even if the
    /// registry is empty.
    ///
    /// Returns a sentence to append to the operation result when the address had
    /// to be changed, or null when nothing needed doing.
    /// </summary>
    private static string? EnsureScopeAddress(string adapterName)
    {
        var scope = ReadScopeAddress();

        if (string.IsNullOrEmpty(scope))
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters");
                scope = key?.GetValue("ScopeAddressBackup") as string;
            }
            catch { }
        }

        if (string.IsNullOrEmpty(scope))
            scope = "192.168.137.1";

        // Give ICS a moment to renumber by itself before overriding it. It is
        // normal for the address to still be the old one a second after the
        // call returns, and rewriting it unnecessarily would fight Windows.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (string.Equals(AdapterAddress(adapterName), scope, StringComparison.Ordinal))
                return null;
            Thread.Sleep(700);
        }

        var current = AdapterAddress(adapterName);

        var set = Run("netsh",
            $"interface ipv4 set address name=\"{adapterName}\" static {scope} 255.255.255.0");

        if (set.ExitCode != 0)
        {
            return $"The console adapter could not be moved to {scope} (it is on "
                 + $"{current}); the console may have no internet until that is fixed by hand.";
        }

        // Read back rather than trust the exit code: netsh returns 0 for a few
        // conditions that leave the address unchanged.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(500);
            if (string.Equals(AdapterAddress(adapterName), scope, StringComparison.Ordinal))
                return $"The console adapter was moved to {scope} (it was on {current}).";
        }

        return $"The console adapter was set to {scope} but still reports "
             + $"{AdapterAddress(adapterName)}; the console may have no internet.";
    }

    private static string Reach(string host)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            return ping.Send(host, 700).Status
                == System.Net.NetworkInformation.IPStatus.Success ? "reachable" : "no reply";
        }
        catch { return "error"; }
    }

    private static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Trunc(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// Returns null when ICS looks able to accept a sharing change, or a sentence
    /// explaining why it cannot.
    ///
    /// This exists because 0x80040201 says nothing about which of several very
    /// different problems occurred, and a bare COM code is not actionable.
    ///
    /// <b>Only genuine blockers belong here.</b> An earlier version also refused
    /// to continue when the <c>ScopeAddress</c> registry value was missing, on the
    /// theory that it caused 0x80040201. That was measured and is wrong: with
    /// <c>ScopeAddress</c> absent and the console adapter correctly addressed,
    /// both <c>EnableSharing(1)</c> and <c>EnableSharing(0)</c> succeed, because ICS
    /// writes the scope itself on first enable. The original observation was taken
    /// while the adapter was on the wrong subnet, so the failure was being blamed
    /// on the registry value instead of on the addressing.
    ///
    /// Refusing there was actively harmful: on a machine in that state the guard
    /// rejected every share attempt before COM was ever called, so the user was
    /// shown a diagnosis for an error the app had never received.
    /// </summary>
    private static string? IcsHealth()
    {
        if (!IsServiceRunning())
        {
            // "Start it and try again" is impossible advice when the service is
            // administratively disabled, so that case says what to do instead.
            return IsServiceDisabled()
                ? "The Internet Connection Sharing service (SharedAccess) is "
                + "disabled, so sharing cannot be enabled. Restore it with "
                + "--ics-repair, or set its startup type to Manual in Services."
                : "The Internet Connection Sharing service (SharedAccess) is not "
                + "running, so sharing cannot be enabled. Start it and try again.";
        }

        // A missing ScopeAddress is deliberately NOT a blocker - see the note above.
        return null;
    }

    /// <summary>
    /// Turns an ICS COM failure into something a person can act on.
    ///
    /// 0x80040201 is reported for several unrelated causes, so the message lists
    /// the ones actually observed rather than guessing at a single one. The
    /// previously suggested fix (reset the adapter to Automatic) was wrong for this
    /// scenario and sent the investigation in the wrong direction.
    /// </summary>
    private static string ExplainIcs(Exception ex)
    {
        const int EventInvokeFailed = unchecked((int)0x80040201);

        if (ex.HResult == EventInvokeFailed)
        {
            return "Windows refused to apply the sharing change (0x80040201). "
                 + "Known causes, in the order worth checking: (1) the console "
                 + "adapter is on the wrong subnet or has no address, so ICS cannot "
                 + "use it as the shared side - check it holds 192.168.7.1 or is "
                 + "left for ICS to number; (2) ICS still references a console "
                 + "adapter that is no longer connected, which used to happen when "
                 + "the cable moved USB port; (3) the machine needs a reboot to "
                 + "recover from a wedged ICS state. A missing ScopeAddress registry "
                 + "value is NOT a cause: ICS writes it itself on first enable.";
        }

        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>
    /// Runs a console process and captures its combined output.
    /// Used only by the read-only diagnostic, which does not need elevation
    /// beyond the one it already has.
    /// </summary>
    private static (int ExitCode, string Output) Run(string file, string args, int timeoutMs = 8000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return (-1, string.Empty);

            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return (proc.HasExited ? proc.ExitCode : -1, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal sealed class ProbePayload
    {
        public bool ServiceRunning { get; set; }
        public string? ScopeAddress { get; set; }
        public string? Error { get; set; }
        public List<IcsConnection> Connections { get; set; } = new();
    }
}
