using System.Net;
using System.Text.RegularExpressions;

namespace G350ConnectionManager.Services;

/// <summary>
/// An IPv4 configuration as Windows reports it for one adapter.
/// </summary>
public sealed record Ipv4Config(
    string Mode,          // "DHCP" or "Static"
    string? Address,
    int PrefixLength,
    string? Gateway)
{
    public string Mask => PrefixLength is >= 0 and <= 32 ? PrefixToMask(PrefixLength) : "-";

    public bool IsEmpty => Address is null && Mode == "DHCP";

    public override string ToString() =>
        Mode == "DHCP"
            ? "Automatic (DHCP)"
            : $"{Address}/{PrefixLength}{(Gateway is null ? "" : $" via {Gateway}")}";

    // ---------------------------------------------------------------- helpers

    public static string PrefixToMask(int prefix)
    {
        if (prefix <= 0) return "0.0.0.0";
        if (prefix >= 32) return "255.255.255.255";
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{(mask >> 24) & 255}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }

    public static int? MaskToPrefix(string mask)
    {
        if (!IPAddress.TryParse(mask, out var addr)) return null;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return null;

        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var prefix = 0;
        for (var i = 31; i >= 0; i--)
        {
            if ((value & (1u << i)) != 0) prefix++;
            else break;
        }

        // Reject non-contiguous masks like 255.0.255.0
        return PrefixToMask(prefix) == mask ? prefix : null;
    }

    public static bool IsValidIPv4(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && IPAddress.TryParse(text.Trim(), out var a)
        && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && Regex.IsMatch(text.Trim(), @"^\d{1,3}(\.\d{1,3}){3}$");
}
