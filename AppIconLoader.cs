using System;
using System.Diagnostics;
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
        private const string AppIconPackUri = "pack://application:,,,/Resources/desktopplus_icon.ico";
        private static readonly string AppIconPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources",
            "desktopplus_icon.ico");

        private static string GetCurrentExecutablePath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath;
            }

            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                processPath = currentProcess.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    return processPath;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static Icon? TryLoadIconFromPackResource()
        {
            try
            {
                var resourceInfo = System.Windows.Application.GetResourceStream(new Uri(AppIconPackUri, UriKind.Absolute));
                if (resourceInfo?.Stream == null)
                {
                    return null;
                }

                using var stream = resourceInfo.Stream;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                using var icon = new Icon(memory);
                return (Icon)icon.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static Icon? TryLoadIconFromFile()
        {
            try
            {
                if (File.Exists(AppIconPath))
                {
                    return new Icon(AppIconPath);
                }
            }
            catch
            {
            }

            return null;
        }

        private static Icon? TryLoadIconFromExecutable()
        {
            try
            {
                string exePath = GetCurrentExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var associatedIcon = Icon.ExtractAssociatedIcon(exePath);
                    if (associatedIcon != null)
                    {
                        return associatedIcon;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static Icon ResolveAppIconForRuntime()
        {
            var fromResource = TryLoadIconFromPackResource();
            if (fromResource != null)
            {
                return fromResource;
            }

            var fromFile = TryLoadIconFromFile();
            if (fromFile != null)
            {
                return fromFile;
            }

            var fromExe = TryLoadIconFromExecutable();
            if (fromExe != null)
            {
                return fromExe;
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        public static Icon LoadNotifyIcon()
        {
            return ResolveAppIconForRuntime();
        }

        private static ImageSource? ConvertIconToImageSource(Icon icon, int size)
        {
            using var sizedIcon = new Icon(icon, new System.Drawing.Size(size, size));
            using var bitmap = sizedIcon.ToBitmap();
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

        public static ImageSource? LoadAppIcon(int size)
        {
            if (size <= 0)
            {
                size = 16;
            }

            try
            {
                using var icon = ResolveAppIconForRuntime();
                return ConvertIconToImageSource(icon, size);
            }
            catch
            {
                try
                {
                    using var fallback = (Icon)SystemIcons.Application.Clone();
                    return ConvertIconToImageSource(fallback, size);
                }
                catch
                {
                    return null;
                }
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
