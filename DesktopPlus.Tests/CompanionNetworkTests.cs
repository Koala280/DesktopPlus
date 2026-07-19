using System.Net;
using System.Net.Sockets;
using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionNetworkTests
{
    [Fact]
    public void GetPrimaryLanAddress_IsNullOrReachableIPv4()
    {
        string? addr = CompanionNetwork.GetPrimaryLanAddress();

        // Environment-dependent: may be null on a host with no network. When present it must be a
        // parseable, non-loopback, non-link-local IPv4 — never something the phone can't reach.
        if (addr != null)
        {
            Assert.True(IPAddress.TryParse(addr, out IPAddress? ip));
            Assert.Equal(AddressFamily.InterNetwork, ip!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(ip));
            byte[] b = ip.GetAddressBytes();
            Assert.False(b[0] == 169 && b[1] == 254, "must not be link-local");
        }
    }

    [Fact]
    public void GetLocalIPv4Addresses_ExcludesLoopbackAndLinkLocal()
    {
        foreach (IPAddress ip in CompanionNetwork.GetLocalIPv4Addresses())
        {
            Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.False(IPAddress.IsLoopback(ip));
            byte[] b = ip.GetAddressBytes();
            Assert.False(b[0] == 169 && b[1] == 254, "must not be link-local");
        }
    }
}
