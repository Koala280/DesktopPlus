using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionAuthTests
{
    [Fact]
    public void GenerateToken_IsUrlSafeAndLongEnough()
    {
        string t = CompanionAuth.GenerateToken();

        Assert.False(string.IsNullOrEmpty(t));
        Assert.True(t.Length >= 40, $"token too short: {t.Length}");   // 32 bytes base64url ≈ 43 chars
        Assert.DoesNotContain('+', t);
        Assert.DoesNotContain('/', t);
        Assert.DoesNotContain('=', t);
        Assert.Matches("^[A-Za-z0-9_-]+$", t);
    }

    [Fact]
    public void GenerateToken_IsUnique()
    {
        Assert.NotEqual(CompanionAuth.GenerateToken(), CompanionAuth.GenerateToken());
    }

    [Fact]
    public void ConstantTimeEquals_TrueForIdentical()
    {
        Assert.True(CompanionAuth.ConstantTimeEquals("abc123XYZ", "abc123XYZ"));
    }

    [Theory]
    [InlineData("abc", "abd")]      // same length, differ
    [InlineData("abc", "abcd")]     // different length
    [InlineData("", "abc")]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void ConstantTimeEquals_FalseOtherwise(string? a, string? b)
    {
        Assert.False(CompanionAuth.ConstantTimeEquals(a, b));
    }
}
