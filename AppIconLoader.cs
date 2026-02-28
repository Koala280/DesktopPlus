using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPlus
{
    internal static class AppIconLoader
    {
        private static readonly string AppIconPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources",
            "desktopplus_icon.ico");

        public static ImageSource? LoadAppIcon(int size)
        {
            try
            {
                if (!File.Exists(AppIconPath)) return null;

                using var icon = new Icon(AppIconPath, new System.Drawing.Size(size, size));
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        public static void TryApplyWindowIcon(Window window, int size = 32)
        {
            var icon = LoadAppIcon(size);
            if (icon != null)
            {
                window.Icon = icon;
            }
        }
    }
}
