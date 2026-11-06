using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;
using Microsoft.Win32;

namespace G350ConnectionManager.Views;

public partial class FilesView : UserControl
{
    private readonly SftpService _sftp = new();

    private string _localPath = AppSettings.DefaultCaptureFolder();
    private string _remotePath = SftpService.RomsRoot;

    private bool _busy;
    private bool _subscribed;

    public FilesView()
    {
        InitializeComponent();

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();

        Render(App.Link.Current);
    }

    // ------------------------------------------------------------- lifecycle

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Link.Updated += OnLinkUpdated;
        _subscribed = true;
        Render(App.Link.Current);

        // Populate both panes on arrival: an empty console pane with no
        // explanation looks broken.
        _ = LoadLocalAsync();
        _ = LoadRemoteAsync();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        App.Link.Updated -= OnLinkUpdated;
        _subscribed = false;
    }

    private void OnLinkUpdated(object? sender, LinkSnapshot snapshot) => Render(snapshot);

    private void Render(LinkSnapshot s)
    {
        var connected = s.Adapter is not null && !string.IsNullOrEmpty(s.ConsoleIp);

        StatusDot.Fill = (Brush)FindResource(connected ? "StatusOkBrush" : "StatusIdleBrush");
        StatusText.Text = connected
            ? $"Console reachable at {s.ConsoleIp}"
            : "Console not connected";

        RefreshButton.IsEnabled = connected && !_busy;
        UploadButton.IsEnabled = connected && !_busy;
        DownloadButton.IsEnabled = connected && !_busy;
    }

    private string? Host => App.Link.Current.ConsoleIp;

    // ----------------------------------------------------------------- local

    private async Task LoadLocalAsync()
    {
        try
        {
            var dir = new DirectoryInfo(_localPath);
            if (!dir.Exists) dir.Create();

            var entries = dir.EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                .Select(f => new FileEntry(
                    f.Name,
                    f.FullName,
                    f is DirectoryInfo,
                    f is FileInfo fi ? fi.Length : -1,
                    f.LastWriteTimeUtc))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LocalList.ItemsSource = entries;
            LocalPathText.Text = _localPath;
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Could not read {_localPath}: {ex.Message}";
        }
    }

    private void LocalList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not FileEntry entry) return;
        if (!entry.IsDirectory) return;

        _localPath = entry.FullPath;
        _ = LoadLocalAsync();
    }

    private void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_localPath);
        if (parent is null) return;

        _localPath = parent.FullName;
        _ = LoadLocalAsync();
    }

    private void LocalNewFolder_Click(object sender, RoutedEventArgs e)
        => CreateFolder(_localPath, isRemote: false);

    private void LocalDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItem is not FileEntry entry)
        {
            ActivityText.Text = "Select a file or folder on this PC first.";
            return;
        }

        if (!ConfirmDelete(entry)) return;

        try
        {
            if (entry.IsDirectory) Directory.Delete(entry.FullPath, recursive: true);
            else File.Delete(entry.FullPath);

            ActivityText.Text = $"Deleted {entry.Name}.";
            _ = LoadLocalAsync();
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Could not delete {entry.Name}: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- remote

    private async Task LoadRemoteAsync()
    {
        var host = Host;
        if (string.IsNullOrEmpty(host))
        {
            ActivityText.Text = "Connect the console first.";
            return;
        }

        _busy = true;
        Render(App.Link.Current);

        try
        {
            var entries = await _sftp.ListAsync(host, _remotePath);
            RemoteList.ItemsSource = entries;
            RemotePathText.Text = _remotePath;
            ActivityText.Text = $"{entries.Count} entries in {_remotePath}.";
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Could not list {_remotePath}: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Render(App.Link.Current);
        }
    }

    private void RemoteList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileEntry entry) return;
        if (!entry.IsDirectory) return;

        _remotePath = entry.FullPath;
        _ = LoadRemoteAsync();
    }

    private void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_remotePath == "/") return;

        var idx = _remotePath.TrimEnd('/').LastIndexOf('/');
        _remotePath = idx <= 0 ? "/" : _remotePath[..idx];
        _ = LoadRemoteAsync();
    }

    private void RemoteNewFolder_Click(object sender, RoutedEventArgs e)
        => CreateFolder(_remotePath, isRemote: true);

    private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileEntry entry)
        {
            ActivityText.Text = "Select a file or folder on the console first.";
            return;
        }

        var host = Host;
        if (string.IsNullOrEmpty(host)) return;

        if (!ConfirmDelete(entry)) return;

        _busy = true;
        Render(App.Link.Current);
        ActivityText.Text = $"Deleting {entry.Name}…";

        try
        {
            if (entry.IsDirectory) await _sftp.DeleteDirectoryAsync(host, entry.FullPath);
            else await _sftp.DeleteFileAsync(host, entry.FullPath);

            ActivityText.Text = $"Deleted {entry.Name} from the console.";
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Could not delete {entry.Name}: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Render(App.Link.Current);
            await LoadRemoteAsync();
        }
    }

    // -------------------------------------------------------------- transfer

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (string.IsNullOrEmpty(host)) return;

        var dialog = new OpenFileDialog
        {
            Title = "Upload to the console",
            InitialDirectory = _localPath
        };
        if (dialog.ShowDialog() != true) return;

        _busy = true;
        Render(App.Link.Current);
        ActivityText.Text = $"Uploading {Path.GetFileName(dialog.FileName)}…";

        try
        {
            await _sftp.UploadAsync(host, dialog.FileName, _remotePath);
            ActivityText.Text = $"Uploaded {Path.GetFileName(dialog.FileName)} to {_remotePath}.";
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Upload failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Render(App.Link.Current);
            await LoadRemoteAsync();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (string.IsNullOrEmpty(host)) return;
        if (RemoteList.SelectedItem is not FileEntry entry || entry.IsDirectory)
        {
            ActivityText.Text = "Select a file on the console to download.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save file from the console",
            FileName = entry.Name,
            InitialDirectory = _localPath
        };
        if (dialog.ShowDialog() != true) return;

        _busy = true;
        Render(App.Link.Current);
        ActivityText.Text = $"Downloading {entry.Name}…";

        try
        {
            await _sftp.DownloadAsync(host, entry.FullPath, dialog.FileName);
            ActivityText.Text = $"Saved {entry.Name} to {Path.GetDirectoryName(dialog.FileName)}.";
            _localPath = Path.GetDirectoryName(dialog.FileName) ?? _localPath;
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Render(App.Link.Current);
            await LoadLocalAsync();
        }
    }

    // --------------------------------------------------------------- shared

    private void CreateFolder(string parent, bool isRemote)
    {
        var name = PromptForName(isRemote ? "New folder on the console" : "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            if (isRemote)
            {
                var host = Host;
                if (string.IsNullOrEmpty(host)) return;

                _busy = true;
                Render(App.Link.Current);
                _sftp.CreateDirectoryAsync(host, parent, name.Trim()).GetAwaiter().GetResult();
                _busy = false;
                Render(App.Link.Current);
                _ = LoadRemoteAsync();
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(parent, name.Trim()));
                _ = LoadLocalAsync();
            }
        }
        catch (Exception ex)
        {
            _busy = false;
            Render(App.Link.Current);
            ActivityText.Text = $"Could not create folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletion is the only irreversible action in the app, so it always asks
    /// first and names exactly what will go. A plain yes/no is enough; making
    /// the user retype the name adds friction without adding safety for a
    /// single-file operation.
    /// </summary>
    private bool ConfirmDelete(FileEntry entry)
    {
        var kind = entry.IsDirectory ? "folder and everything inside it" : "file";

        var message = $"Are you sure you want to delete this {kind}?\n\n"
                    + $"{entry.Name}\n\n"
                    + "You can't recover it.";

        return MessageBox.Show(
            Window.GetWindow(this),
            message,
            "Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private string? PromptForName(string title)
    {
        var window = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        var box = new TextBox { Margin = new Thickness(0, 0, 0, 14) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Create", MinWidth = 84, IsDefault = true };

        cancel.Click += (_, _) => window.DialogResult = false;
        ok.Click += (_, _) => window.DialogResult = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Loaded += (_, _) => box.Focus();

        return window.ShowDialog() == true ? box.Text : null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadLocalAsync();
        await LoadRemoteAsync();
    }
}
