using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace G350ConnectionManager.Services;

/// <summary>
/// Reads and writes Windows IPv4 adapter configuration via netsh.
///
/// SAFETY MODEL (docs/PLAN.md §9)
///  * <see cref="ReadAsync"/> only ever observes.
///  * <see cref="ApplyStaticAsync"/> first snapshots the current configuration
///    to disk, then writes. The snapshot is what <see cref="RestoreAsync"/> uses.
///  * Every mutation is logged to <see cref="Activity"/> with before/after.
///  * Nothing runs elevated until the caller explicitly asks to apply.
/// </summary>
public sealed class NetworkConfigService
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "G350ConnectionManager", "backups");

    /// <summary>Addresses the app treats as "automatic" for the console link.</summary>
    ///
    /// There are two valid automatic setups and Windows decides which one applies:
    ///
    ///   192.168.7.1    direct USB link, no internet sharing
    ///   192.168.137.1 Windows Internet Connection Sharing
    ///
    /// ICS reserves 192.168.137.1 for the shared adapter and will overwrite
    /// anything else, so Automatic mode has to follow the sharing state rather
    /// than always suggesting .7.1.
    public const string AutoAddress = "192.168.7.1";
    public const string AutoMask = "255.255.255.0";

    /// <summary>Address ICS forces on the shared adapter.</summary>
    public const string IcsAutoAddress = "192.168.137.1";

    /// <summary>Append-only in-memory activity log shown in the UI.</summary>
    public List<string> Activity { get; } = new();

    private void Log(string message)
    {
        Activity.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        // Keep the log bounded so a long session cannot grow without limit.
        if (Activity.Count > 200) Activity.RemoveRange(0, Activity.Count - 200);
    }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Current IPv4 configuration of an adapter. Returns null when the adapter
    /// is gone (e.g. the console was unplugged).
    /// </summary>
    public async Task<Ipv4Config?> ReadAsync(string adapterName)
    {
        // "IP Address:" block tells us whether DHCP or static is in effect.
        var (output, exitCode) = await RunCapturedAsync($"interface ipv4 show config name=\"{adapterName}\"");
        if (output is null || exitCode != 0) return null;

        // netsh pads its output and the captured stream can contain blank lines
        // between every entry; normalise before pattern matching.
        var text = Normalise(output);

        var mode = text.Contains("DHCP enabled:", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("DHCP enabled: Yes", StringComparison.OrdinalIgnoreCase)
            ? "DHCP"
            : "Static";

        var address = FirstMatch(text, @"IP Address:\s+(\d+\.\d+\.\d+\.\d+)");
        var mask = FirstMatch(text, @"Subnet Prefix:.*?\(mask\s+(\d+\.\d+\.\d+\.\d+)\)")
                   ?? FirstMatch(text, @"Subnet Mask\s*:\s*(\d+\.\d+\.\d+\.\d+)");
        var gateway = FirstMatch(text, @"Default Gateway:\s+(\d+\.\d+\.\d+\.\d+)");

        // No address at all means the interface has no IPv4 binding yet.
        if (address is null) return new Ipv4Config("DHCP", null, 24, null);

        var prefix = mask is null ? 24 : Ipv4Config.MaskToPrefix(mask) ?? 24;

        return new Ipv4Config(mode, address, prefix, gateway);
    }

    // ----------------------------------------------------------------- write

    /// <summary>
    /// Applies a static IPv4 address, snapshotting the previous config first.
    /// Returns (ok, message).
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyStaticAsync(
        string adapterName, string address, string mask, string? gateway)
    {
        if (!Ipv4Config.IsValidIPv4(address))
            return (false, "IP address is not a valid IPv4 address.");
        if (Ipv4Config.MaskToPrefix(mask) is null)
            return (false, "Subnet mask is not a valid contiguous mask.");
        if (!string.IsNullOrWhiteSpace(gateway) && !Ipv4Config.IsValidIPv4(gateway))
            return (false, "Gateway is not a valid IPv4 address.");

        // 1. Snapshot BEFORE any change. If this fails, do not touch anything.
        var before = await ReadAsync(adapterName);
        if (before is null)
            return (false, $"Adapter '{adapterName}' not found. Reconnect the console.");

        var snapshotOk = await SaveSnapshotAsync(adapterName, before);
        if (!snapshotOk)
            return (false, "Could not save a backup of the current settings — aborting to stay safe.");

        Log($"[{adapterName}] before: {before}");

        // 2. Apply.
        var applyArgs =
            $"interface ipv4 set address name=\"{adapterName}\" static {address} {mask}" +
            (string.IsNullOrWhiteSpace(gateway) ? " none" : $" {gateway}");

        var setResult = await RunElevatedAsync(applyArgs);
        if (!setResult.Ok)
        {
            Log($"[{adapterName}] apply FAILED: {setResult.Message}");
            return (false, $"Could not apply — {setResult.Message}");
        }

        // 3. Verify by reading back.
        //
        // Windows runs Duplicate Address Detection after a static address is set,
        // during which `netsh show config` has no "IP Address:" line at all and
        // ipconfig reports the address as (Tentative). Reading back immediately
        // therefore looks like a failure even when the write succeeded, so poll
        // for a few seconds before declaring it broken.
        var expectedPrefix = Ipv4Config.MaskToPrefix(mask) ?? 24;
        Ipv4Config? after = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            after = await ReadAsync(adapterName);
            if (after is not null
                && after.Address == address
                && after.PrefixLength == expectedPrefix)
            {
                break;
            }
            await Task.Delay(250);
        }

        var ok = after is not null
                 && after.Address == address
                 && after.PrefixLength == expectedPrefix;

        Log($"[{adapterName}] after : {after}{(!ok ? "  (verify MISMATCH)" : "")}");

        return ok
            ? (true, $"Applied {address}/{expectedPrefix}" +
                     (string.IsNullOrWhiteSpace(gateway) ? "" : $" via {gateway}") + ".")
            : (false, "Settings were written but read-back did not match. Try Apply again.");
    }

    /// <summary>
    /// Restores the configuration captured by the last <see cref="ApplyStaticAsync"/>.
    /// </summary>
    public async Task<(bool Ok, string Message)> RestoreAsync(string adapterName)
    {
        var saved = LoadSnapshot(adapterName);
        if (saved is null)
            return (false, "No backup on record for this adapter — nothing to restore.");

        // If the adapter vanished while a backup exists, say so precisely rather
        // than blaming the elevation prompt.
        if (await ReadAsync(adapterName) is null)
            return (false, $"Adapter '{adapterName}' is not connected — reconnect it, then restore.");

        Log($"[{adapterName}] restoring: {saved}");

        var restoreArgs = saved.Mode == "DHCP"
            ? $"interface ipv4 set address name=\"{adapterName}\" source=dhcp"
            : $"interface ipv4 set address name=\"{adapterName}\" static {saved.Address} " +
              $"{Ipv4Config.PrefixToMask(saved.PrefixLength)}" +
              (saved.Gateway is null ? " none" : $" {saved.Gateway}");

        var result = await RunElevatedAsync(restoreArgs);
        if (!result.Ok)
        {
            Log($"[{adapterName}] restore FAILED: {result.Message}");
            return (false, $"Could not restore — {result.Message}");
        }

        DeleteSnapshot(adapterName);

        // Same DAD settling window as Apply: wait for the restored value to show up.
        Ipv4Config? after = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            after = await ReadAsync(adapterName);
            var matches = saved.Mode == "DHCP"
                ? after?.Mode == "DHCP"
                : after?.Address == saved.Address;
            if (matches) break;
            await Task.Delay(250);
        }

        Log($"[{adapterName}] restored to: {after}");
        return (true, $"Restored to {saved}.");
    }

    /// <summary>True when a backup exists for this adapter (i.e. Restore will work).</summary>
    public bool HasSnapshot(string adapterName) => LoadSnapshot(adapterName) is not null;

    // ------------------------------------------------------------- snapshots

    private static string SnapshotPath(string adapterName)
    {
        // Adapter names can contain characters that are awkward in filenames.
        var safe = string.Concat(adapterName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(StateDir, $"ipv4-{safe}.json");
    }

    private async Task<bool> SaveSnapshotAsync(string adapterName, Ipv4Config config)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SnapshotPath(adapterName), json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Ipv4Config? LoadSnapshot(string adapterName)
    {
        try
        {
            var path = SnapshotPath(adapterName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Ipv4Config>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void DeleteSnapshot(string adapterName)
    {
        try
        {
            var path = SnapshotPath(adapterName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Leaving a stale backup behind is harmless; it can still be restored.
        }
    }

    // -------------------------------------------------------------- process

    /// <summary>
    /// Runs a netsh arguments string at normal privilege and captures output.
    /// Returns (output, exitCode); output is null when the process could not run.
    /// </summary>
    private static async Task<(string? Output, int ExitCode)> RunCapturedAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (null, -1);

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return (string.IsNullOrEmpty(stdout) ? stderr : stdout, proc.ExitCode);
        }
        catch
        {
            return (null, -1);
        }
    }

    /// <summary>
    /// Collapses the doubled blank lines netsh output can contain and trims
    /// each line, so regexes can rely on single spaces between tokens.
    /// </summary>
    private static string Normalise(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            if (line.Length == 0) continue;
            // Collapse runs of 2+ spaces into one for predictable matching.
            var prevSpace = false;
            foreach (var ch in line)
            {
                var isSpace = ch == ' ' || ch == '\t';
                if (isSpace && prevSpace) continue;
                sb.Append(isSpace ? ' ' : ch);
                prevSpace = isSpace;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Runs a netsh mutation elevated (UAC prompt for this one operation only).
    ///
    /// Two problems had to be solved here:
    ///
    /// 1. Elevation requires UseShellExecute=true, which cannot redirect stdio.
    ///    Output is therefore captured by running a generated .bat that redirects
    ///    to a temp file.
    /// 2. ShellExecute re-parses the argument string, which mangles quoted adapter
    ///    names containing spaces ("Ethernet 2" becomes two tokens, and netsh then
    ///    reports a confusing parameter error). Putting the command inside a .bat
    ///    means cmd.exe does the parsing and the name survives verbatim.
    ///
    /// Capturing output also matters because netsh exits 1 for every failure —
    /// bad adapter, invalid address, and a declined UAC prompt are otherwise
    /// indistinguishable.
    /// </summary>
    private static async Task<(bool Ok, string Message)> RunElevatedAsync(string netshArgs)
    {
        var id = Guid.NewGuid().ToString("N");
        var batPath = Path.Combine(Path.GetTempPath(), $"g350-netsh-{id}.bat");
        var outPath = Path.Combine(Path.GetTempPath(), $"g350-netsh-{id}.txt");

        try
        {
            // > redirects both streams; exit code of netsh is propagated by cmd.
            var script =
                "@echo off\r\n" +
                $"netsh {netshArgs} > \"{outPath}\" 2>&1\r\n" +
                "exit /b %ERRORLEVEL%\r\n";
            await File.WriteAllTextAsync(batPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (false, "could not start the elevation helper");

            await proc.WaitForExitAsync();

            var output = File.Exists(outPath) ? (await File.ReadAllTextAsync(outPath)).Trim() : string.Empty;

            if (proc.ExitCode == 0) {
                return (true, "done");
            }

            // Treat "already in that state" as success rather than an error.
            if (IsBenignNetshMessage(output)) {
                return (true, "already configured");
            }

            return (false, ExplainNetsh(output, proc.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED, i.e. the UAC prompt was dismissed.
            return ex.NativeErrorCode == 1223
                ? (false, "the Windows elevation prompt was declined")
                : (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(batPath)) File.Delete(batPath); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    /// <summary>Turns netsh's own wording into something actionable.</summary>
    private static string ExplainNetsh(string output, int exitCode)
    {
        if (output.Length == 0) return $"netsh exited with code {exitCode}";

        if (output.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
            return "Windows did not grant administrator rights";
        if (output.Contains("filename, directory name", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not find", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no more data", StringComparison.OrdinalIgnoreCase))
            return "the adapter could not be found — reconnect the console and try again";
        if (output.Contains("Invalid address parameter", StringComparison.OrdinalIgnoreCase))
            return "the IP address was rejected by Windows";
        if (output.Contains("Invalid source parameter", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Invalid parameter", StringComparison.OrdinalIgnoreCase))
            return "Windows rejected the command parameters";

        return output.Replace("\r", "").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// netsh reports some harmless conditions as errors. "DHCP is already
    /// enabled" when restoring a DHCP snapshot is not a failure.
    /// </summary>
    private static bool IsBenignNetshMessage(string output) =>
        output.Contains("already enabled", StringComparison.OrdinalIgnoreCase)
        || output.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    private static string? FirstMatch(string text, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
