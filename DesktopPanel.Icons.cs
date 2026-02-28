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
            SetContentScrollbarsFrozen(true);
            try
            {
            UpdateWrapPanelWidth();
            UpdateLayout();

            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            if (wrapPanel == null)
            {
                MainWindow.SaveSettings();
                return;
            }

            Rect workArea = GetWorkAreaForPanel();
            double startWidth = Width;
            int baselineFirstRowCount = GetFirstRowItemCount(FileList, wrapPanel);
            bool widthChanged = false;
            bool heightChanged = false;
            double minWindowWidth = Math.Max(220, MinWidth > 0 ? MinWidth : 0);
            double maxWindowWidth = Math.Max(minWindowWidth, workArea.Right - Left);

            // --- Width fitting ---
            // Measure how much horizontal space items actually need.
            // We use item count × item width for precision, avoiding sub-pixel drift.
            double currentLayoutWidth = GetContentLayoutWidth();
            if (currentLayoutWidth > 1)
            {
                double neededWrapWidth = GetVisibleItemRightEdge(FileList, wrapPanel);
                double firstRowRightEdge = GetFirstRowRightEdge(FileList, wrapPanel);
                double stableNeededWrapWidth = Math.Max(neededWrapWidth, firstRowRightEdge);

                if (stableNeededWrapWidth > 0)
                {
                    // Use the actual right-most rendered item edge for width fitting.
                    // This avoids overestimating with count * max-width and keeps
                    // scrollbar overlay changes out of the fit calculation.
                    double wrapHorizontalMargin = GetHorizontalMargin(wrapPanel);
                    double layoutSafetyPixels = Math.Ceiling(2 * VisualTreeHelper.GetDpi(this).DpiScaleX);
                    double neededContentWidth = Math.Ceiling(stableNeededWrapWidth + wrapHorizontalMargin + layoutSafetyPixels);
                    double chrome = Width - currentLayoutWidth;
                    double targetWidth = chrome + neededContentWidth;

                    targetWidth = Math.Max(minWindowWidth, Math.Min(targetWidth, maxWindowWidth));

                    if (Math.Abs(targetWidth - Width) > 0.5)
                    {
                        Width = targetWidth;
                        widthChanged = true;
                    }
                }
            }

            if (widthChanged)
            {
                UpdateLayout();
                UpdateWrapPanelWidth();
                UpdateLayout();

                // Guard against wrap-threshold drift (rounding/DPI):
                // never reduce items in the first row when fitting width.
                if (baselineFirstRowCount > 0 && Width < startWidth - 0.5)
                {
                    int fittedFirstRowCount = GetFirstRowItemCount(FileList, wrapPanel);
                    if (fittedFirstRowCount < baselineFirstRowCount)
                    {
                        double fallbackWidth = Math.Max(minWindowWidth, Math.Min(startWidth, maxWindowWidth));
                        if (Math.Abs(fallbackWidth - Width) > 0.5)
                        {
                            Width = fallbackWidth;
                            UpdateLayout();
                            UpdateWrapPanelWidth();
                            UpdateLayout();
                        }
                    }
                }
            }

            // --- Height fitting ---
            // After width is settled, measure content height at the final width.
            double finalWrapWidth = wrapPanel.Width > 0 ? wrapPanel.Width : GetContentLayoutWidth();
            if (finalWrapWidth <= 1)
            {
                if (widthChanged)
                {
                    SyncAnchoringFromCurrentBounds();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateWrapPanelWidth();
                        MainWindow.SaveSettings();
                        MainWindow.NotifyPanelsChanged();
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    MainWindow.SaveSettings();
                }
                return;
            }

            wrapPanel.Measure(new System.Windows.Size(finalWrapWidth, double.PositiveInfinity));
            double desiredWrapHeight = wrapPanel.DesiredSize.Height;

            // Calculate chrome between window height and viewport height
            double viewportHeight = ContentContainer.ViewportHeight > 0
                ? ContentContainer.ViewportHeight
                : ContentContainer.ActualHeight;

            if (viewportHeight <= 1)
            {
                if (widthChanged)
                {
                    SyncAnchoringFromCurrentBounds();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateWrapPanelWidth();
                        MainWindow.SaveSettings();
                        MainWindow.NotifyPanelsChanged();
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    MainWindow.SaveSettings();
                }
                return;
            }

            double verticalChrome = Height - viewportHeight;
            double targetHeight2 = verticalChrome + desiredWrapHeight + 6;
            double collapsedHeight = GetCollapsedHeight();
            double minWindowHeight = Math.Max(collapsedHeight, MinHeight > 0 ? MinHeight : 0);
            double maxWindowHeightInWorkArea = Math.Max(minWindowHeight, workArea.Height);
            targetHeight2 = Math.Max(minWindowHeight, Math.Min(targetHeight2, maxWindowHeightInWorkArea));

            double targetTop = Top;
            double maxHeightAtCurrentTop = Math.Max(minWindowHeight, workArea.Bottom - targetTop);

            // Special case: panel is at (or near) the bottom edge and cannot grow downward.
            // Shift upward so fit-to-content can still reach the target height.
            if (targetHeight2 > maxHeightAtCurrentTop + 0.5)
            {
                double shiftedTop = workArea.Bottom - targetHeight2;
                targetTop = ClampTopToWorkArea(workArea, targetHeight2, shiftedTop);

                double maxHeightAtShiftedTop = Math.Max(minWindowHeight, workArea.Bottom - targetTop);
                targetHeight2 = Math.Max(minWindowHeight, Math.Min(targetHeight2, maxHeightAtShiftedTop));
            }
            else
            {
                targetTop = ClampTopToWorkArea(workArea, targetHeight2, targetTop);
            }

            SnapWindowVerticalBounds(ref targetTop, ref targetHeight2);

            if (Math.Abs(targetTop - Top) > 0.5)
            {
                Top = targetTop;
                heightChanged = true;
            }

            if (Math.Abs(targetHeight2 - Height) > 0.5)
            {
                Height = targetHeight2;
                expandedHeight = Math.Max(collapsedHeight, targetHeight2);
                heightChanged = true;
            }

            if (!widthChanged && !heightChanged)
            {
                MainWindow.SaveSettings();
                return;
            }

            SyncAnchoringFromCurrentBounds();
            UpdateWrapPanelWidth();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateWrapPanelWidth();
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }), System.Windows.Threading.DispatcherPriority.Render);
            }
            finally
            {
                SetContentScrollbarsFrozen(false);
            }
        }

        private void UpdateWrapPanelWidth()
        {
            if (ContentContainer == null || FileList == null) return;

            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            if (wrapPanel != null)
            {
                double baseWidth = GetContentLayoutWidth();
                double availableWidth = Math.Max(0, baseWidth - GetHorizontalMargin(wrapPanel));
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

        private double GetContentLayoutWidth()
        {
            if (ContentContainer == null)
            {
                return 0;
            }

            // `ActualWidth` remains stable even when the vertical scrollbar toggles.
            // This keeps wrapping and fit-to-content independent from scrollbar width.
            if (ContentContainer.ActualWidth > 0)
            {
                return ContentContainer.ActualWidth;
            }

            if (ContentContainer.ViewportWidth > 0)
            {
                return ContentContainer.ViewportWidth;
            }

            return 0;
        }

        private static double GetHorizontalMargin(FrameworkElement element)
        {
            return element.Margin.Left + element.Margin.Right;
        }

        private static int GetFirstRowItemCount(System.Windows.Controls.ListBox fileList, WrapPanel wrapPanel)
        {
            const double rowTolerance = 2.0;
            double firstRowY = double.NaN;
            int count = 0;

            foreach (var item in fileList.Items)
            {
                if (fileList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    continue;
                }

                if (container.Visibility != Visibility.Visible)
                {
                    continue;
                }

                double itemWidth = container.ActualWidth > 0
                    ? container.ActualWidth
                    : container.RenderSize.Width;
                if (itemWidth <= 0)
                {
                    continue;
                }

                var topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), wrapPanel);
                if (double.IsNaN(firstRowY))
                {
                    firstRowY = topLeft.Y;
                }
                else if (Math.Abs(topLeft.Y - firstRowY) > rowTolerance)
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private static double GetFirstRowRightEdge(System.Windows.Controls.ListBox fileList, WrapPanel wrapPanel)
        {
            const double rowTolerance = 2.0;
            double firstRowY = double.NaN;
            double maxRight = 0;
            bool hasVisibleItems = false;

            foreach (var item in fileList.Items)
            {
                if (fileList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    continue;
                }

                if (container.Visibility != Visibility.Visible)
                {
                    continue;
                }

                double itemWidth = container.ActualWidth > 0
                    ? container.ActualWidth
                    : container.RenderSize.Width;
                if (itemWidth <= 0)
                {
                    continue;
                }

                var topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), wrapPanel);
                if (double.IsNaN(firstRowY))
                {
                    firstRowY = topLeft.Y;
                }
                else if (Math.Abs(topLeft.Y - firstRowY) > rowTolerance)
                {
                    break;
                }

                maxRight = Math.Max(maxRight, topLeft.X + itemWidth);
                hasVisibleItems = true;
            }

            return hasVisibleItems ? maxRight : 0;
        }

        private static double GetVisibleItemRightEdge(System.Windows.Controls.ListBox fileList, WrapPanel wrapPanel)
        {
            double maxRight = 0;
            bool hasVisibleItems = false;

            foreach (var item in fileList.Items)
            {
                if (fileList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    continue;
                }

                if (container.Visibility != Visibility.Visible)
                {
                    continue;
                }

                double itemWidth = container.ActualWidth > 0
                    ? container.ActualWidth
                    : container.RenderSize.Width;
                if (itemWidth <= 0)
                {
                    continue;
                }

                var topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), wrapPanel);
                maxRight = Math.Max(maxRight, topLeft.X + itemWidth);
                hasVisibleItems = true;
            }

            return hasVisibleItems ? maxRight : 0;
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
