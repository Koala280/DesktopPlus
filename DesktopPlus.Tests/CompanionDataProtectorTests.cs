using System;
using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionDataProtectorTests
{
    [Fact]
    public void ProtectThenUnprotect_RoundTripsString()
    {
        string secret = "tok_" + Guid.NewGuid().ToString("N");

        string protectedValue = CompanionDataProtector.ProtectString(secret);
        Assert.False(string.IsNullOrEmpty(protectedValue));
        Assert.NotEqual(secret, protectedValue);                       // actually encrypted
        Assert.Equal(secret, CompanionDataProtector.UnprotectString(protectedValue));
    }

    [Fact]
    public void ProtectString_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", CompanionDataProtector.ProtectString(""));
        Assert.Equal("", CompanionDataProtector.ProtectString(null));
    }

    [Fact]
    public void UnprotectString_NotDpapiPayload_ReturnsEmpty()
    {
        // Valid base64 but not a DPAPI blob, and outright garbage: both degrade to "no secret".
        Assert.Equal("", CompanionDataProtector.UnprotectString("YWJjZGVmZ2g="));
        Assert.Equal("", CompanionDataProtector.UnprotectString("not-base64-$$$"));
        Assert.Equal("", CompanionDataProtector.UnprotectString(null));
    }

    [Fact]
    public void ProtectBytes_RoundTrips()
    {
        byte[] data = { 0, 1, 2, 3, 4, 250, 251, 252, 253, 254, 255 };

        byte[] protectedBytes = CompanionDataProtector.ProtectBytes(data);
        Assert.NotEqual(data, protectedBytes);
        Assert.Equal(data, CompanionDataProtector.UnprotectBytes(protectedBytes));
    }
}
