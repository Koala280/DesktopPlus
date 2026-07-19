using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Produces real Explorer thumbnails (with icon fallback) as PNG bytes via the Windows
    /// Shell image factory. Uses WPF imaging for the HBITMAP→PNG conversion (alpha-correct)
    /// and is safe to call off the UI thread (the BitmapSource is frozen before encoding).
    /// </summary>
    internal static class CompanionThumbnails
    {
        public static byte[]? GetPng(string? rawPath, int size)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            string path;
            try
            {
                path = Path.GetFullPath(rawPath);
            }
            catch
            {
                return null;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return null;
            }

            size = Math.Clamp(size, 32, 512);
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, null, iid, out IShellItemImageFactory factory);
                factory.GetImage(
                    new SIZE(size, size),
                    SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK,
                    out hBitmap);
                if (hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var memory = new MemoryStream();
                encoder.Save(memory);
                return memory.ToArray();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int width, int height)
            {
                cx = width;
                cy = height;
            }
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            System.Runtime.InteropServices.ComTypes.IBindCtx? pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
