using QRCoder;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Renders the pairing URL as a QR-code PNG (pure managed, no System.Drawing dependency)
    /// for display in the settings UI so the phone can scan and connect.
    /// </summary>
    internal static class QrCodeGenerator
    {
        public static byte[] CreatePng(string text, int pixelsPerModule = 8)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            return png.GetGraphic(pixelsPerModule);
        }
    }
}
