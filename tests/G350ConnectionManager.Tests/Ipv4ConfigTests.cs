using G350ConnectionManager.Services;
using Xunit;

namespace G350ConnectionManager.Tests;

/// <summary>
/// Prefix and mask conversion. The apply path shows the user a "before -> after"
/// address, and a wrong conversion there silently reports success for a change
/// that was never made.
/// </summary>
public class Ipv4ConfigTests
{
    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    [InlineData(30, "255.255.255.252")]
    [InlineData(12, "255.240.0.0")]
    public void PrefixToMask_MapsPrefixToDottedQuad(int prefix, string expected)
        => Assert.Equal(expected, Ipv4Config.PrefixToMask(prefix));

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    public void MaskToPrefix_RoundTripsThroughPrefixToMask(int prefix, string mask)
        => Assert.Equal(prefix, Ipv4Config.MaskToPrefix(mask));

    [Theory]
    [InlineData("255.0.255.0")]
    [InlineData("255.255.255.1")]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void MaskToPrefix_RejectsNonContiguousOrMalformedMasks(string mask)
        => Assert.Null(Ipv4Config.MaskToPrefix(mask));

    [Theory]
    [InlineData("192.168.7.1", true)]
    [InlineData("192.168.137.1", true)]
    [InlineData(" 192.168.7.2 ", true)]
    [InlineData("192.168.7", false)]
    [InlineData("192.168.7.1.5", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidIPv4_AcceptsOnlyDottedQuad(string? text, bool expected)
        => Assert.Equal(expected, Ipv4Config.IsValidIPv4(text));

    [Fact]
    public void ToString_DescribesDhcpWithoutAnAddress()
        => Assert.Equal("Automatic (DHCP)", new Ipv4Config("DHCP", null, 0, null).ToString());

    [Fact]
    public void ToString_IncludesGatewayWhenPresent()
        => Assert.Equal("192.168.7.1/24 via 192.168.7.1",
            new Ipv4Config("Static", "192.168.7.1", 24, "192.168.7.1").ToString());

    [Fact]
    public void ToString_OmitsGatewayWhenAbsent()
        => Assert.Equal("192.168.7.1/24",
            new Ipv4Config("Static", "192.168.7.1", 24, null).ToString());
}
