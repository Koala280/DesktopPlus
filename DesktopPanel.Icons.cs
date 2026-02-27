using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int width, int height)
            {
                cx = width;
                cy = height;
            }
        }

        enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10,
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IBindCtx pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private ImageSource? LoadExplorerStyleIcon(string filePath, int size = 256)
        {
            try
            {
                Guid iidImageFactory = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(filePath, null!, iidImageFactory, out IShellItemImageFactory imageFactory);

                SIZE iconSize = new SIZE(size, size);

                imageFactory.GetImage(iconSize, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);

                if (hBitmap != IntPtr.Zero)
                {
                    var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(size, size));

                    DeleteObject(hBitmap);
                    return bitmapSource;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon konnte nicht geladen werden: {ex.Message}");
            }

            return null;
        }

        private StackPanel CreateListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            var activeAppearance = appearance ?? _currentAppearance ?? MainWindow.Appearance;
            double iconSize = 48 * zoomFactor;
            double textSize = Math.Max(8, activeAppearance.ItemFontSize) * zoomFactor;
            double panelWidth = 100 * zoomFactor;
            bool isFolder = isBackButton || Directory.Exists(path);
            bool isHiddenItem = !isBackButton && showHiddenItems && IsHiddenPath(path);
            double baseOpacity = isHiddenItem ? 0.58 : 1.0;
            var textBrush = CreateBrush(
                isFolder ? ResolveFolderColor(activeAppearance) : activeAppearance.TextColor,
                1.0,
                MediaColor.FromRgb(242, 245, 250));

            StackPanel panel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Width = panelWidth,
                Margin = new Thickness(5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Opacity = baseOpacity
            };

            System.Windows.Controls.Image icon = new System.Windows.Controls.Image
            {
                Source = LoadExplorerStyleIcon(path, (int)(48 * zoomFactor)),
                Width = iconSize,
                Height = iconSize,
                Margin = new Thickness(0, 0, 0, 5),
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            TextBlock text = new TextBlock
            {
                Text = displayName,
                FontSize = textSize,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = panelWidth - 10,
                Foreground = textBrush,
                Opacity = 0.92
            };

            panel.Children.Add(icon);
            panel.Children.Add(text);
            panel.PreviewMouseLeftButtonDown += (s, e) =>
            {
                panel.Opacity = Math.Max(0.45, baseOpacity - 0.2);
            };

            panel.MouseLeftButtonUp += (s, e) =>
            {
                panel.Opacity = baseOpacity;
            };

            panel.MouseLeave += (s, e) =>
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    panel.Opacity = baseOpacity;
                }
            };
            return panel;
        }

        public void SetZoom(double newZoom)
        {
            zoomFactor = Math.Max(0.7, Math.Min(1.5, newZoom));
            ApplyZoom();
            MainWindow.SaveSettings();
        }

        private void ApplyZoom()
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance;
            double baseTextSize = appearance != null ? Math.Max(8, appearance.ItemFontSize) : 12;
            foreach (var item in FileList.Items)
            {
                if (item is ListBoxItem listBoxItem && listBoxItem.Content is StackPanel panel)
                {
                    panel.Width = 90 * zoomFactor;

                    foreach (var child in panel.Children)
                    {
                        if (child is System.Windows.Controls.Image img)
                        {
                            img.Width = 48 * zoomFactor;
                            img.Height = 48 * zoomFactor;
                        }
                        else if (child is TextBlock txt)
                        {
                            txt.FontSize = baseTextSize * zoomFactor;
                            txt.Width = 85 * zoomFactor;
                            txt.MaxHeight = double.PositiveInfinity;
                        }
                    }
                }
            }
        }

        private void ContentContainer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;
                zoomFactor += (e.Delta > 0) ? 0.1 : -0.1;
                zoomFactor = Math.Max(0.7, Math.Min(2.0, zoomFactor));
                ApplyZoom();
            }
            else if (sender is ScrollViewer scrollViewer)
            {
                double newOffset = scrollViewer.VerticalOffset - (e.Delta * 0.5);
                newOffset = Math.Max(0, Math.Min(newOffset, scrollViewer.ScrollableHeight));
                scrollViewer.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void ContentContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWrapPanelWidth();
        }

        public void FitToContent()
        {
            if (ContentContainer == null || FileList == null) return;

            if (!isContentVisible)
            {
                ForceCollapseState(false);
            }

            SetContentLayerVisibility(true);
            UpdateWrapPanelWidth();
            UpdateLayout();

            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            if (wrapPanel == null)
            {
                MainWindow.SaveSettings();
                return;
            }

            double measureWidth = wrapPanel.Width > 0 ? wrapPanel.Width : ContentContainer.ActualWidth;
            if (measureWidth <= 1)
            {
                MainWindow.SaveSettings();
                return;
            }

            wrapPanel.Measure(new System.Windows.Size(measureWidth, double.PositiveInfinity));
            double desiredWrapHeight = wrapPanel.DesiredSize.Height;
            double viewportHeight = ContentContainer.ViewportHeight > 0
                ? ContentContainer.ViewportHeight
                : ContentContainer.ActualHeight;

            if (viewportHeight <= 1)
            {
                MainWindow.SaveSettings();
                return;
            }

            const double viewportPadding = 6;
            double targetViewportHeight = Math.Max(0, desiredWrapHeight + viewportPadding);
            double delta = targetViewportHeight - viewportHeight;
            if (Math.Abs(delta) <= 0.5)
            {
                MainWindow.SaveSettings();
                return;
            }

            double collapsedHeight = GetCollapsedHeight();
            double minWindowHeight = Math.Max(collapsedHeight, MinHeight > 0 ? MinHeight : 0);
            Rect workArea = GetWorkAreaForPanel();
            double maxWindowHeight = Math.Max(minWindowHeight, workArea.Bottom - Top);

            double targetHeight = Height + delta;
            targetHeight = Math.Max(minWindowHeight, Math.Min(targetHeight, maxWindowHeight));

            Height = targetHeight;
            expandedHeight = Math.Max(collapsedHeight, targetHeight);
            SyncAnchoringFromCurrentBounds();
            UpdateWrapPanelWidth();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateWrapPanelWidth();
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void UpdateWrapPanelWidth()
        {
            if (ContentContainer == null || FileList == null) return;

            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            if (wrapPanel != null)
            {
                // Keep reserve independent from current scrollbar visibility so
                // Fit-to-content remains stable and doesn't oscillate on wrap changes.
                const double horizontalInset = 10;
                const double scrollbarGutterReserve = 6;
                double baseWidth = ContentContainer.ActualWidth > 0
                    ? ContentContainer.ActualWidth
                    : ContentContainer.ViewportWidth;
                double availableWidth = baseWidth - horizontalInset - scrollbarGutterReserve;
                if (availableWidth > 0)
                {
                    if (Math.Abs(wrapPanel.Width - availableWidth) > 0.5)
                    {
                        wrapPanel.Width = availableWidth;
                        wrapPanel.InvalidateMeasure();
                        wrapPanel.InvalidateArrange();
                        FileList.InvalidateMeasure();
                    }
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                    return found;
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
