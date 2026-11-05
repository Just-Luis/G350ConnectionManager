using System.Windows;
using System.Windows.Controls;

namespace G350ConnectionManager.Views;

public partial class NetworkView : UserControl
{
    /// <summary>
    /// True while InitializeComponent() is still running. The AutoMode radio has
    /// IsChecked="True" in XAML, so its Checked event fires before the named
    /// fields below it exist — touching them there would throw.
    /// </summary>
    private bool _loading = true;

    public NetworkView()
    {
        InitializeComponent();
        _loading = false;
        UpdateFieldState();
    }

    private void IpMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateFieldState();
    }

    /// <summary>
    /// Automatic mode pins the values the app will apply; Manual lets the user
    /// type them. Actual apply/restore logic lands in Stage 3.
    /// </summary>
    private void UpdateFieldState()
    {
        if (IpAddressBox is null || SubnetBox is null || GatewayBox is null) return;

        var manual = ManualMode?.IsChecked == true;

        IpAddressBox.IsEnabled = manual;
        SubnetBox.IsEnabled = manual;
        GatewayBox.IsEnabled = manual;

        if (!manual)
        {
            IpAddressBox.Text = "192.168.7.1";
            SubnetBox.Text = "255.255.255.0";
            GatewayBox.Text = string.Empty;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        NetworkHint.Text = "Applying IP configuration arrives in Stage 3.";
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        NetworkHint.Text = "Connection testing arrives in Stage 2.";
    }
}
