using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;

namespace G350ConnectionManager.Views;

public partial class HomeView : UserControl
{
    private bool _subscribed;

    /// <summary>
    /// Suppresses the ToggleButton's Checked/Unchecked handlers while the code
    /// sets <see cref="ShareWifiToggle"/>.IsChecked to mirror the real state -
    /// otherwise every status refresh would look like a user click and start a
    /// sharing operation.
    /// </summary>
    private bool _syncingToggle;

    /// <summary>Last state the toggle was synced from.</summary>
    private bool _sharingOn;

    /// <summary>
    /// Set once the user has operated the toggle.
    ///
    /// Before that, the knob is seeded from the link probe's address-based guess,
    /// which is the only information available without an elevated read. After
    /// it, the knob reflects the result of a real ICS operation and must not be
    /// overwritten by that guess - the guess looks at the adapter's *first*
    /// address, so an adapter holding both 192.168.7.1 and 192.168.137.1 can
    /// report "not sharing" while sharing is in fact on.
    /// </summary>
    private bool _userToggled;

    private readonly SharingCoordinator _sharing = new();

    public HomeView()
    {
        InitializeComponent();

        // Views are cached by MainWindow and re-attached when re-selected, so
        // subscribing in the constructor is not enough: Unloaded fires when the
        // view is navigated away from, and the cached instance is never
        // reconstructed on return. Bind to Loaded/Unloaded instead.
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();

        Render(App.Link.Current);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Link.Updated += OnLinkUpdated;
        _subscribed = true;
        Render(App.Link.Current);
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        App.Link.Updated -= OnLinkUpdated;
        _subscribed = false;
    }

    private void OnLinkUpdated(object? sender, LinkSnapshot snapshot) => Render(snapshot);

    // ------------------------------------------------------------- actions

    /// <summary>
    /// One press does whatever the current state needs, which is the point of the
    /// button: connect the console if it is not answering, otherwise stop
    /// pretending the banner is the only feedback and run a fresh probe.
    ///
    /// When the console is already reachable this re-probes rather than doing
    /// nothing, because the useful action at that moment is "is it still fine?".
    /// </summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ActionHeadline.Text = "Connecting…";
        ActionDetail.Text = App.Link.Current.Adapter is null
            ? "Looking for the console's USB adapter."
            : "Checking that the console answers and SSH is running.";

        try
        {
            var snapshot = await App.Link.RefreshAsync();
            Render(snapshot);

            // The console's own services are not started by anything on the PC
            // side when the link is up but port 22 is shut. Say so instead of
            // leaving "Console detected" on screen with no explanation.
            if (snapshot.AdapterFound && !snapshot.SshOk)
            {
                ActionDetail.Text =
                    "The console is reachable but its services are stopped. "
                    + "Press [A] on the console, or turn on Share Wi-Fi.";
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Turns the PC's internet sharing to the console on or off.
    ///
    /// The toggle reflects real state rather than an assumption: the operation is
    /// verified through the same coordinator the Network page uses, and the knob
    /// is snapped back if Windows refused.
    /// </summary>
    private async void ShareWifi_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;

        var enable = ShareWifiToggle.IsChecked == true;
        _userToggled = true;

        ShareWifiToggle.IsEnabled = false;
        ActionHeadline.Text = enable ? "Sharing Wi-Fi…" : "Stopping sharing…";
        ActionDetail.Text = "Approve the Windows prompt if one appears.";

        try
        {
            var result = await _sharing.SetSharingAsync(enable);

            if (!result.Ok)
            {
                // Put the knob back where it was: the toggle must never claim a
                // state Windows did not accept.
                SyncToggle(previous: !enable);
                ActionHeadline.Text = enable ? "Could not share Wi-Fi" : "Could not stop sharing";
                ActionDetail.Text = result.Message;
                return;
            }

            _sharingOn = enable;

            if (enable)
            {
                ActionHeadline.Text = result.ConsoleHasInternet
                    ? "The console is online"
                    : "Sharing is on";

                ActionDetail.Text = result.ConsoleHasInternet
                    ? "The G350 can reach the internet through this PC."
                    : "Windows is sharing, but the console has not reached the internet yet. "
                      + "Give it a moment, or press Connect again.";
            }
            else
            {
                ActionHeadline.Text = "Sharing stopped";
                ActionDetail.Text = "The console keeps its direct link to this PC.";
            }

            await App.Link.RefreshAsync();
            Render(App.Link.Current);
        }
        finally
        {
            ShareWifiToggle.IsEnabled = true;
        }
    }

    /// <summary>Sets the toggle without raising the click handlers.</summary>
    private void SyncToggle(bool previous)
    {
        _syncingToggle = true;
        try
        {
            ShareWifiToggle.IsChecked = previous;
            _sharingOn = previous;
        }
        finally
        {
            _syncingToggle = false;
        }
    }

    private void Test_Click(object sender, RoutedEventArgs e) => _ = App.Link.RefreshAsync();

    /// <summary>
    /// The icon at the top of each card navigates to that tab, which is quicker
    /// than hunting the sidebar for the same destination.
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        var tag = sender switch
        {
            Button b => b.Tag as string,
            FrameworkElement fe => fe.Tag as string,
            _ => null
        };

        if (tag is not null && Window.GetWindow(this) is MainWindow main)
            main.GoTo(tag);
    }

    private void Render(LinkSnapshot s)
    {
        BannerDot.Fill = (Brush)FindResource(
            s.IsReady ? "StatusOkBrush"
            : s.AdapterFound ? "StatusWarnBrush"
            : "StatusIdleBrush");

        BannerHeadline.Text = s.Headline;
        BannerDetail.Text = s.IsReady ? $"{s.Detail} · {s.PingMs} ms" : s.Detail;

        // Keep the quick-action line honest, but never overwrite the result of an
        // operation the user just triggered - that message is more specific than
        // anything derived from the periodic probe.
        if (ConnectButton.IsEnabled && ShareWifiToggle.IsEnabled)
        {
            ActionHeadline.Text = s.IsReady
                ? "Connected"
                : s.AdapterFound
                    ? "Console detected"
                    : "Not connected";

            ActionDetail.Text = s.IsReady
                ? $"{s.ConsoleIp} · SSH ready. Share Wi-Fi to give the console internet."
                : s.AdapterFound
                    ? "The adapter is up but the console is not answering."
                    : "Plug in the USB cable and switch the console to PC mode.";
        }

        // The card is labelled "Network" and follows step 1 of the setup flow,
        // which is about the console's address — so this reports the console IP,
        // not the PC adapter's.
        Step1Text.Text = s.IsReady
            ? $"IP set to {s.ConsoleIp}"
            : s.AdapterFound
                ? "Adapter up, console silent"
                : "Waiting for console";

        // Sharing can be on while the PC itself is offline, so both facts matter.
        Step2Text.Text = s.InternetSharingActive
            ? (s.InternetReachable ? "Sharing on — PC online" : "Sharing on — PC offline")
            : "Off";

        // These are always available, but saying so is friendlier than saying
        // nothing, and it hints that they depend on the link being up.
        Step3Text.Text = s.IsReady ? "Preview and record" : "Connect first";
        Step4Text.Text = s.IsReady ? "Browse and transfer" : "Connect first";
        Step5Text.Text = s.IsReady ? "Run commands" : "Connect first";

        // The probe cannot read ICS (that needs elevation), so before the user
        // has touched the toggle the knob is seeded from an address-based guess.
        // Once they have operated it, the knob is the record of a real operation
        // and is left alone - see _userToggled.
        if (!_userToggled && !_syncingToggle && ShareWifiToggle.IsEnabled)
        {
            var guess = s.InternetSharingActive;
            if (guess != _sharingOn)
            {
                _syncingToggle = true;
                try
                {
                    ShareWifiToggle.IsChecked = guess;
                    _sharingOn = guess;
                }
                finally
                {
                    _syncingToggle = false;
                }
            }
        }
    }
}
