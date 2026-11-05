using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using G350ConnectionManager.Models;

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

    /// <summary>The values the app considers "automatic" for the console link.</summary>
    public const string AutoAddress = "192.168.7.1";
    public const string AutoMask = "255.255.255.0";

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
        var setIp = await RunAsync(
            $"netsh interface ipv4 set address name=\"{adapterName}\" static {address} {mask}" +
            (string.IsNullOrWhiteSpace(gateway) ? " none" : $" {gateway}"),
            requireElevation: true);

        if (setIp is null)
        {
            Log($"[{adapterName}] apply FAILED (elevation denied or netsh error)");
            return (false, "Could not apply — the elevation prompt may have been declined.");
        }

        // 3. Verify by reading back.
        var after = await ReadAsync(adapterName);
        var expectedPrefix = Ipv4Config.MaskToPrefix(mask) ?? 24;
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

        string? result;
        if (saved.Mode == "DHCP")
        {
            result = await RunAsync(
                $"netsh interface ipv4 set address name=\"{adapterName}\" source=dhcp",
                requireElevation: true);
        }
        else
        {
            var mask = Ipv4Config.PrefixToMask(saved.PrefixLength);
            result = await RunAsync(
                $"netsh interface ipv4 set address name=\"{adapterName}\" static {saved.Address} {mask}" +
                (saved.Gateway is null ? " none" : $" {saved.Gateway}"),
                requireElevation: true);
        }

        if (result is null)
        {
            Log($"[{adapterName}] restore FAILED");
            return (false, "Could not restore — the elevation prompt may have been declined.");
        }

        DeleteSnapshot(adapterName);
        var after = await ReadAsync(adapterName);
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
    /// Runs a netsh mutation, elevating only this operation (UAC prompt).
    /// Returns null when it could not run or exited non-zero.
    /// </summary>
    private static async Task<string?> RunAsync(string arguments, bool requireElevation = false)
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

            if (requireElevation)
            {
                psi.UseShellExecute = true;
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
                psi.CreateNoWindow = false;
                psi.Verb = "runas";
            }

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            if (requireElevation)
            {
                // Elevation path cannot capture stdout; success is exit code 0.
                await proc.WaitForExitAsync();
                return proc.ExitCode == 0 ? string.Empty : null;
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstMatch(string text, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
