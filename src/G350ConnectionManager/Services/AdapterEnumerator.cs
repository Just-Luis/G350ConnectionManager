using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>
/// Finds the USB Ethernet / RNDIS adapter that the G350 presents to Windows.
/// Read-only: this type never changes adapter configuration.
/// </summary>
public static class AdapterEnumerator
{
    /// <summary>
    /// Descriptions Windows gives the G350's gadget, in rough order of likelihood.
    /// Matching is case-insensitive substring.
    /// </summary>
    private static readonly string[] RndisHints =
    {
        "remote ndis", "rndis", "usb ethernet", "usb-net", "gadget",
        "linux usb", "cdc-ecm", "cdc ethernet"
    };

    /// <summary>
    /// Returns true when the adapter looks like the console's USB network gadget.
    /// </summary>
    public static bool LooksLikeGadget(string name, string description)
    {
        var haystack = $"{name} {description}".ToLowerInvariant();
        foreach (var hint in RndisHints)
            if (haystack.Contains(hint, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Enumerates every operational or potentially-relevant IPv4 interface.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> All()
    {
        var result = new List<AdapterInfo>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            IPv4InterfaceProperties? ipProps;
            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
                ipProps = props.GetIPv4Properties();
            }
            catch
            {
                // Adapter disappeared mid-enumeration or the driver refused.
                continue;
            }

            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString();

            var gateway = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                     && !g.Address.Equals(IPAddress.Any))
                ?.Address.ToString();

            var isUp = ni.OperationalStatus == OperationalStatus.Up;

            result.Add(new AdapterInfo(
                Id: ni.Id,
                Name: ni.Name,
                Description: ni.Description,
                Ipv4: ipv4,
                Gateway: gateway,
                IsUp: isUp,
                IsRndis: LooksLikeGadget(ni.Name, ni.Description)));
        }

        return result;
    }

    /// <summary>
    /// The best candidate for the console link: prefer a gadget-matching adapter,
    /// then anything currently up.
    /// </summary>
    public static AdapterInfo? FindConsoleAdapter(IReadOnlyList<AdapterInfo> adapters)
    {
        return adapters.FirstOrDefault(a => a.IsRndis)
               ?? adapters.FirstOrDefault(a => a.IsRndis && a.IsUp)
               ?? null;
    }

    /// <summary>Adapters that could host an internet connection (Wi-Fi, Ethernet).</summary>
    public static IReadOnlyList<AdapterInfo> ShareableSources(IReadOnlyList<AdapterInfo> adapters)
        => adapters.Where(a => !a.IsRndis).ToList();
}
