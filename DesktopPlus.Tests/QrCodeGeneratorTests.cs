using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class QrCodeGeneratorTests
{
    [Fact]
    public void CreatePng_ReturnsValidPngBytes()
    {
        byte[] png = QrCodeGenerator.CreatePng("https://192.168.1.100:8443/#t=abc123");

        Assert.NotNull(png);
        Assert.True(png.Length > 0);

        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void CreatePng_LargerModuleSize_ProducesMoreBytes()
    {
        byte[] small = QrCodeGenerator.CreatePng("same-content", pixelsPerModule: 4);
        byte[] large = QrCodeGenerator.CreatePng("same-content", pixelsPerModule: 16);

        Assert.True(large.Length > small.Length);
    }
}
