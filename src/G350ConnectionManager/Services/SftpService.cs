using System.IO;
using Renci.SshNet;

using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>One entry in a remote (or local) folder listing.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc)
{
    /// <summary>Size rendered for the list, e.g. "1.4 MB" or "—" for folders.</summary>
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    public string ModifiedText =>
        ModifiedUtc == DateTime.MinValue
            ? "—"
            : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}

/// <summary>
/// SFTP access to the console.
///
/// The console runs OpenSSH with the internal-sftp subsystem, and '/roms' is
/// world-writable on a stock dArkOS image, so ordinary file operations need no
/// elevation. Anything outside the user's writable paths will fail with a
/// permission error, which is reported rather than retried.
///
/// Every operation creates and disposes its own session. A long-lived connection
/// would be faster, but it also breaks the moment the console's address changes
/// — which happens on every internet-sharing toggle (192.168.7.2 vs .137.2).
/// Transfers are the only long-lived activity, and those hold their own session
/// for their duration.
/// </summary>
public sealed class SftpService
{
    private static string User => ConsoleCredentials.User;
    private static string Password => ConsoleCredentials.Password;

    /// <summary>Where the console's user data lives.</summary>
    public const string RomsRoot = "/roms";

    public Task<List<FileEntry>> ListAsync(string host, string path)
        => Task.Run(() => List(host, path));

    private static List<FileEntry> List(string host, string path)
    {
        using var sftp = Connect(host);

        var entries = new List<FileEntry>();
        foreach (var item in sftp.ListDirectory(path))
        {
            // internal-sftp reports the conventional . and .. entries.
            if (item.Name is "." or "..") continue;

            entries.Add(new FileEntry(
                item.Name,
                item.FullName,
                item.IsDirectory,
                item.IsRegularFile ? (long)item.Length : -1,
                item.LastWriteTimeUtc));
        }

        // Folders first, then names, matching how a file manager is expected to read.
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task DownloadAsync(string host, string remotePath, string localPath)
        => Task.Run(() =>
        {
            using var sftp = Connect(host);
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // The stream must be disposed explicitly: DownloadFile does not
            // close it, which leaves the destination locked afterwards.
            using (var output = File.Create(localPath))
                sftp.DownloadFile(remotePath, output);
        });

    public Task UploadAsync(string host, string localPath, string remoteDir)
        => Task.Run(() =>
        {
            using var sftp = Connect(host);
            var target = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(localPath);
            using var source = File.OpenRead(localPath);
            sftp.UploadFile(source, target, true);
        });

    public Task DeleteFileAsync(string host, string remotePath)
        => Task.Run(() =>
        {
            using var sftp = Connect(host);
            sftp.DeleteFile(remotePath);
        });

    /// <summary>Removes a folder and everything inside it.</summary>
    public Task DeleteDirectoryAsync(string host, string remotePath)
        => Task.Run(() =>
        {
            using var sftp = Connect(host);
            DeleteRecursive(sftp, remotePath);
        });

    private static void DeleteRecursive(SftpClient sftp, string path)
    {
        // Children first, depth-first: rmdir only succeeds on an empty folder.
        foreach (var item in sftp.ListDirectory(path))
        {
            if (item.Name is "." or "..") continue;

            if (item.IsDirectory) DeleteRecursive(sftp, item.FullName);
            else sftp.DeleteFile(item.FullName);
        }

        sftp.DeleteDirectory(path);
    }

    public Task CreateDirectoryAsync(string host, string parent, string name)
        => Task.Run(() =>
        {
            using var sftp = Connect(host);
            sftp.CreateDirectory(parent.TrimEnd('/') + "/" + name);
        });

    private static SftpClient Connect(string host)
    {
        var client = new SftpClient(host, User, Password)
        {
            ConnectionInfo = { Timeout = TimeSpan.FromSeconds(8) },
            OperationTimeout = TimeSpan.FromSeconds(30)
        };
        client.Connect();
        return client;
    }
}
