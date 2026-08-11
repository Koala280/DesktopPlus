using System.Text;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class ShellFileNameMapTests
{
    [Fact]
    public void BuildUnicodePayload_PreservesOrderAndAddsDoubleNullTerminator()
    {
        byte[] payload = ShellFileNameMap.BuildUnicodePayload(
            new[] { "Original Folder", "Archive with spaces.zip" });

        Assert.Equal(
            "Original Folder\0Archive with spaces.zip\0\0",
            Encoding.Unicode.GetString(payload));
    }

    [Fact]
    public void BuildUnicodePayload_RejectsInvalidNames()
    {
        Assert.Throws<ArgumentException>(() =>
            ShellFileNameMap.BuildUnicodePayload(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() =>
            ShellFileNameMap.BuildUnicodePayload(new[] { "valid", "bad\0name" }));
    }
}
