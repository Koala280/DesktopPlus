using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MediaColor = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

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
        private const string ItemNameTextTag = "DesktopPlus.ItemName";
        private const string ParentNavigationTextPrefix = "↩ ";
        private static readonly HashSet<string> PhotoFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif"
        };
        private static readonly Dictionary<string, (int Width, int Height)> PhotoDimensionsCache =
            new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> PhotoPreviewCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private enum DetailsSortColumn
        {
            Name,
            Type,
            Size,
            Created,
            Modified,
            Dimensions
        }

        private DetailsSortColumn _detailsSortColumn = DetailsSortColumn.Name;
        private bool _detailsSortAscending = true;

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

        private static ImageSource? LoadPhotoPreviewSource(string path, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsPhotoPath(path))
            {
                return null;
            }

            lock (PhotoPreviewCache)
            {
                if (PhotoPreviewCache.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = Math.Max(96, decodePixelWidth);
                bitmap.EndInit();
                bitmap.Freeze();

                lock (PhotoPreviewCache)
                {
                    PhotoPreviewCache[path] = bitmap;
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private FrameworkElement CreateListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            string normalizedViewMode = NormalizeViewMode(viewMode);
            if (string.Equals(normalizedViewMode, ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return CreateDetailsListBoxItem(displayName, path, isBackButton, appearance);
            }

            if (string.Equals(normalizedViewMode, ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return CreatePhotoListBoxItem(displayName, path, isBackButton, appearance);
            }

            return CreateIconListBoxItem(displayName, path, isBackButton, appearance);
        }

        private FrameworkElement CreateIconListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
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

            TextBlock text = CreateItemNameTextBlock(
                displayName,
                textSize,
                textBrush,
                TextAlignment.Center,
                new Thickness(0, 0, 0, 0));
            text.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Width = panelWidth - 10;
            text.Opacity = 0.92;

            panel.Children.Add(icon);
            panel.Children.Add(text);
            AttachPressedOpacity(panel, baseOpacity);
            return panel;
        }

        private bool IsMetadataColumnVisible(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataType;
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataSize;
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataCreated;
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataModified;
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataDimensions;
            }

            return false;
        }

        private List<string> GetOrderedVisibleMetadataColumns()
        {
            var ordered = NormalizeMetadataOrder(metadataOrder);
            return ordered.Where(IsMetadataColumnVisible).ToList();
        }

        private static GridLength GetMetadataColumnWidth(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return new GridLength(110);
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return new GridLength(95);
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return new GridLength(130);
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return new GridLength(130);
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return new GridLength(110);
            }

            return new GridLength(100);
        }

        private string GetMetadataColumnLabelText(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaType");
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaSize");
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaCreated");
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaModified");
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaDimensions");
            }

            return metadataKey;
        }

        private string GetMetadataColumnHeaderText(string metadataKey)
        {
            string label = GetMetadataColumnLabelText(metadataKey);

            DetailsSortColumn headerColumn = MapMetadataToSortColumn(metadataKey);
            if (_detailsSortColumn == headerColumn)
            {
                label += _detailsSortAscending ? " [^]" : " [v]";
            }

            return label;
        }

        private string GetMetadataValueText(string metadataKey, string path, bool isFolder)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return GetItemTypeText(path, isBackButton: false, isFolder);
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return GetSizeText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return GetCreatedText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return GetModifiedText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return GetDimensionsText(path, isFolder);
            }

            return "-";
        }

        private static DetailsSortColumn MapMetadataToSortColumn(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Type;
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Size;
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Created;
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Modified;
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Dimensions;
            }

            return DetailsSortColumn.Name;
        }

        private void MetadataHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void MetadataHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBlock header || header.Tag is not string metadataKey)
            {
                return;
            }

            DetailsSortColumn clickedColumn = MapMetadataToSortColumn(metadataKey);
            if (_detailsSortColumn == clickedColumn)
            {
                _detailsSortAscending = !_detailsSortAscending;
            }
            else
            {
                _detailsSortColumn = clickedColumn;
                _detailsSortAscending = true;
            }

            bool hasActiveSearchRequest = _searchCts != null &&
                PanelType == PanelKind.Folder &&
                !string.IsNullOrWhiteSpace(SearchBox?.Text);
            if (hasActiveSearchRequest)
            {
                // Avoid expensive full re-sorts while search is still streaming results.
                _deferSortUntilSearchComplete = true;
                RefreshParentNavigationItemVisual();
                e.Handled = true;
                return;
            }

            SortCurrentFolderItemsInPlace();
            RebuildListItemVisuals();
            e.Handled = true;
        }

        private void RefreshParentNavigationItemVisual()
        {
            if (FileList == null)
            {
                return;
            }

            var backItem = FileList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(IsParentNavigationItem);
            if (backItem?.Tag is not string parentPath || string.IsNullOrWhiteSpace(parentPath))
            {
                return;
            }

            string displayName = BuildParentNavigationDisplayName(parentPath);
            backItem.Content = CreateListBoxItem(displayName, parentPath, isBackButton: true, _currentAppearance);
        }

        private void SortCurrentFolderItemsInPlace()
        {
            if (FileList == null || PanelType != PanelKind.Folder)
            {
                return;
            }

            var allItems = FileList.Items.OfType<ListBoxItem>().ToList();
            if (allItems.Count <= 1)
            {
                return;
            }

            var selectedPaths = new HashSet<string>(
                FileList.SelectedItems
                    .OfType<ListBoxItem>()
                    .Select(i => i.Tag as string)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            var backItem = allItems.FirstOrDefault(IsParentNavigationItem);
            var sortable = allItems
                .Where(i => !ReferenceEquals(i, backItem))
                .ToList();

            sortable.Sort(CompareItemsForCurrentSort);

            FileList.Items.Clear();
            if (backItem != null)
            {
                FileList.Items.Add(backItem);
            }

            foreach (var item in sortable)
            {
                FileList.Items.Add(item);
            }

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is string path &&
                    selectedPaths.Contains(path))
                {
                    item.IsSelected = true;
                }
            }
        }

        private int CompareItemsForCurrentSort(ListBoxItem left, ListBoxItem right)
        {
            string leftPath = left.Tag as string ?? "";
            string rightPath = right.Tag as string ?? "";
            bool leftIsFolder = Directory.Exists(leftPath);
            bool rightIsFolder = Directory.Exists(rightPath);

            if (leftIsFolder != rightIsFolder)
            {
                return leftIsFolder ? -1 : 1;
            }

            int comparison = 0;
            switch (_detailsSortColumn)
            {
                case DetailsSortColumn.Type:
                    comparison = string.Compare(
                        GetItemTypeText(leftPath, isBackButton: false, leftIsFolder),
                        GetItemTypeText(rightPath, isBackButton: false, rightIsFolder),
                        StringComparison.CurrentCultureIgnoreCase);
                    break;
                case DetailsSortColumn.Size:
                    comparison = GetComparableSize(leftPath, leftIsFolder)
                        .CompareTo(GetComparableSize(rightPath, rightIsFolder));
                    break;
                case DetailsSortColumn.Created:
                    comparison = GetComparableTimestamp(leftPath, leftIsFolder, created: true)
                        .CompareTo(GetComparableTimestamp(rightPath, rightIsFolder, created: true));
                    break;
                case DetailsSortColumn.Modified:
                    comparison = GetComparableTimestamp(leftPath, leftIsFolder, created: false)
                        .CompareTo(GetComparableTimestamp(rightPath, rightIsFolder, created: false));
                    break;
                case DetailsSortColumn.Dimensions:
                    comparison = GetComparableDimensionValue(leftPath, leftIsFolder)
                        .CompareTo(GetComparableDimensionValue(rightPath, rightIsFolder));
                    break;
                case DetailsSortColumn.Name:
                default:
                    comparison = string.Compare(
                        GetDisplayNameForPath(leftPath),
                        GetDisplayNameForPath(rightPath),
                        StringComparison.CurrentCultureIgnoreCase);
                    break;
            }

            if (comparison == 0)
            {
                comparison = string.Compare(
                    GetDisplayNameForPath(leftPath),
                    GetDisplayNameForPath(rightPath),
                    StringComparison.CurrentCultureIgnoreCase);
            }

            return _detailsSortAscending ? comparison : -comparison;
        }

        private static long GetComparableSize(string path, bool isFolder)
        {
            if (isFolder)
            {
                return -1;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                return fileInfo.Exists ? fileInfo.Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static DateTime GetComparableTimestamp(string path, bool isFolder, bool created)
        {
            var value = TryGetPathTimestamp(path, isFolder, created);
            return value ?? DateTime.MinValue;
        }

        private static long GetComparableDimensionValue(string path, bool isFolder)
        {
            if (isFolder || !File.Exists(path) || !IsPhotoPath(path))
            {
                return -1;
            }

            var dimensions = TryGetPhotoDimensions(path);
            if (string.IsNullOrWhiteSpace(dimensions))
            {
                return -1;
            }

            string[] parts = dimensions.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long width) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long height))
            {
                return -1;
            }

            return (width * height * 100000L) + width;
        }

        private FrameworkElement CreateDetailsListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            var activeAppearance = appearance ?? _currentAppearance ?? MainWindow.Appearance;
            bool isFolder = isBackButton || Directory.Exists(path);
            bool isHiddenItem = !isBackButton && showHiddenItems && IsHiddenPath(path);
            double baseOpacity = isHiddenItem ? 0.58 : 1.0;
            double rowWidth = GetDetailsItemWidth();
            double iconSize = Math.Max(20, 24 * zoomFactor);
            double textSize = Math.Max(8, activeAppearance.ItemFontSize) * zoomFactor;
            double metaSize = Math.Max(8, textSize - 1);
            var nameBrush = CreateBrush(
                isFolder ? ResolveFolderColor(activeAppearance) : activeAppearance.TextColor,
                1.0,
                MediaColor.FromRgb(242, 245, 250));
            var metadataBrush = CreateBrush(
                activeAppearance.MutedTextColor,
                1.0,
                MediaColor.FromRgb(167, 176, 192));

            var panel = new Grid
            {
                Width = rowWidth,
                Margin = new Thickness(4, 2, 4, 2),
                Opacity = baseOpacity
            };

            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(28, 32 * zoomFactor)) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var visibleMetadata = GetOrderedVisibleMetadataColumns();
            int columnIndex = 2;
            foreach (string metadataKey in visibleMetadata)
            {
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GetMetadataColumnWidth(metadataKey) });

                var metadataText = new TextBlock
                {
                    Foreground = metadataBrush,
                    FontSize = metaSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                if (isBackButton)
                {
                    metadataText.Text = GetMetadataColumnHeaderText(metadataKey);
                    metadataText.FontWeight = FontWeights.SemiBold;
                    metadataText.Cursor = System.Windows.Input.Cursors.Hand;
                    metadataText.Tag = metadataKey;
                    metadataText.PreviewMouseLeftButtonDown += MetadataHeader_PreviewMouseLeftButtonDown;
                    metadataText.MouseLeftButtonUp += MetadataHeader_MouseLeftButtonUp;
                }
                else
                {
                    metadataText.Text = GetMetadataValueText(metadataKey, path, isFolder);
                }

                Grid.SetColumn(metadataText, columnIndex++);
                panel.Children.Add(metadataText);
            }

            System.Windows.Controls.Image icon = new System.Windows.Controls.Image
            {
                Source = LoadExplorerStyleIcon(path, (int)Math.Max(48, iconSize * 2)),
                Width = iconSize,
                Height = iconSize,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            TextBlock nameText = CreateItemNameTextBlock(
                displayName,
                textSize,
                nameBrush,
                TextAlignment.Left,
                new Thickness(2, 0, 0, 0));
            nameText.VerticalAlignment = VerticalAlignment.Center;
            nameText.TextWrapping = TextWrapping.NoWrap;
            nameText.TextTrimming = TextTrimming.CharacterEllipsis;

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameText, 1);
            panel.Children.Add(icon);
            panel.Children.Add(nameText);
            AttachPressedOpacity(panel, baseOpacity);
            return panel;
        }

        private FrameworkElement CreatePhotoListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            var activeAppearance = appearance ?? _currentAppearance ?? MainWindow.Appearance;
            bool isFolder = isBackButton || Directory.Exists(path);
            bool isHiddenItem = !isBackButton && showHiddenItems && IsHiddenPath(path);
            double baseOpacity = isHiddenItem ? 0.58 : 1.0;
            double thumbWidth = Math.Max(86, 122 * zoomFactor);
            const double defaultPreviewAspect = 1.39;
            double previewAspect = defaultPreviewAspect;
            if (!isBackButton &&
                !isFolder &&
                IsPhotoPath(path) &&
                TryGetPhotoPixelDimensions(path, out int pixelWidth, out int pixelHeight))
            {
                previewAspect = Math.Clamp((double)pixelWidth / Math.Max(1, pixelHeight), 0.18, 6.2);
            }

            double previewHeight = Math.Clamp(thumbWidth / previewAspect, thumbWidth * 0.22, thumbWidth * 1.28);
            double previewWidth = Math.Clamp(previewHeight * previewAspect, thumbWidth * 0.62, thumbWidth * 1.7);
            double cardWidth = previewWidth + 24;
            double textSize = Math.Max(8, activeAppearance.ItemFontSize) * zoomFactor;
            double metaSize = Math.Max(8, textSize - 1);
            var nameBrush = CreateBrush(
                isFolder ? ResolveFolderColor(activeAppearance) : activeAppearance.TextColor,
                1.0,
                MediaColor.FromRgb(242, 245, 250));
            var metadataBrush = CreateBrush(
                activeAppearance.MutedTextColor,
                1.0,
                MediaColor.FromRgb(167, 176, 192));

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Width = cardWidth,
                Margin = new Thickness(6),
                Opacity = baseOpacity
            };

            var thumbnailFrame = new Border
            {
                Width = previewWidth,
                Height = previewHeight,
                CornerRadius = new CornerRadius(8),
                BorderBrush = TryFindResource("PanelBorder") as Brush ?? Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(3)
            };

            System.Windows.Controls.Image icon = new System.Windows.Controls.Image
            {
                Source = (!isBackButton && !isFolder && IsPhotoPath(path))
                    ? (LoadPhotoPreviewSource(
                        path,
                        (int)Math.Ceiling(Math.Max(previewWidth, previewHeight) * 2.0))
                        ?? LoadExplorerStyleIcon(path, 256))
                    : LoadExplorerStyleIcon(path, IsPhotoPath(path) ? 256 : 128),
                Width = Math.Max(18, previewWidth - 8),
                Height = previewHeight - 8,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            thumbnailFrame.Child = icon;

            TextBlock nameText = CreateItemNameTextBlock(
                displayName,
                textSize,
                nameBrush,
                TextAlignment.Center,
                new Thickness(0, 6, 0, 0));
            nameText.HorizontalAlignment = HorizontalAlignment.Stretch;
            nameText.TextWrapping = TextWrapping.NoWrap;
            nameText.TextTrimming = TextTrimming.CharacterEllipsis;

            TextBlock metaText = new TextBlock
            {
                Text = GetPhotoMetaText(path, isFolder, isBackButton),
                FontSize = metaSize,
                Foreground = metadataBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
                Opacity = 0.9
            };

            panel.Children.Add(thumbnailFrame);
            panel.Children.Add(nameText);
            if (!string.IsNullOrWhiteSpace(metaText.Text))
            {
                panel.Children.Add(metaText);
            }

            if (!isBackButton)
            {
                var tooltipContent = new TextBlock
                {
                    Text = string.Empty,
                    TextWrapping = TextWrapping.NoWrap,
                    FontSize = Math.Max(8, metaSize),
                    Margin = new Thickness(2)
                };
                var metadataToolTip = new System.Windows.Controls.ToolTip { Content = tooltipContent };
                metadataToolTip.Opened += (_, _) =>
                {
                    if (tooltipContent.Tag is string cachedText &&
                        !string.IsNullOrWhiteSpace(cachedText))
                    {
                        tooltipContent.Text = cachedText;
                        return;
                    }

                    string tooltipText = BuildPhotoMetadataTooltipText(path, isFolder);
                    tooltipContent.Tag = tooltipText;
                    tooltipContent.Text = tooltipText;
                };

                ToolTipService.SetToolTip(panel, metadataToolTip);
                ToolTipService.SetInitialShowDelay(panel, 700);
                ToolTipService.SetBetweenShowDelay(panel, 120);
                ToolTipService.SetShowDuration(panel, 16000);
            }

            AttachPressedOpacity(panel, baseOpacity);
            return panel;
        }

        private static void AttachPressedOpacity(UIElement element, double baseOpacity)
        {
            element.PreviewMouseLeftButtonDown += (_, _) =>
            {
                element.Opacity = Math.Max(0.45, baseOpacity - 0.2);
            };

            element.MouseLeftButtonUp += (_, _) =>
            {
                element.Opacity = baseOpacity;
            };

            element.MouseLeave += (_, _) =>
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    element.Opacity = baseOpacity;
                }
            };
        }

        private TextBlock CreateItemNameTextBlock(
            string text,
            double fontSize,
            Brush brush,
            TextAlignment textAlignment,
            Thickness margin)
        {
            return new TextBlock
            {
                Text = text,
                Tag = ItemNameTextTag,
                FontSize = fontSize,
                TextAlignment = textAlignment,
                Foreground = brush,
                FontFamily = FontFamily,
                Margin = margin
            };
        }

        private string BuildParentNavigationDisplayName(string path)
        {
            return ParentNavigationTextPrefix + GetDisplayNameForPath(path);
        }

        private bool IsParentNavigationPath(string path)
        {
            if (PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(currentFolderPath) ||
                string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string? parentPath = Path.GetDirectoryName(currentFolderPath);
            return !string.IsNullOrWhiteSpace(parentPath) &&
                   string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase);
        }

        private string GetItemTypeText(string path, bool isBackButton, bool isFolder)
        {
            if (isBackButton)
            {
                return MainWindow.GetString("Loc.PanelTypeFolder");
            }

            if (isFolder)
            {
                return MainWindow.GetString("Loc.PanelTypeFolder");
            }

            string extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return MainWindow.GetString("Loc.PanelTypeFile");
            }

            return extension.TrimStart('.').ToUpperInvariant();
        }

        private static string GetSizeText(string path, bool isFolder)
        {
            if (isFolder)
            {
                return "-";
            }

            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    return "-";
                }

                return FormatFileSize(fileInfo.Length);
            }
            catch
            {
                return "-";
            }
        }

        private static string GetCreatedText(string path, bool isFolder)
        {
            var created = TryGetPathTimestamp(path, isFolder, created: true);
            return created.HasValue ? FormatTimestamp(created.Value) : "-";
        }

        private static string GetModifiedText(string path, bool isFolder)
        {
            var modified = TryGetPathTimestamp(path, isFolder, created: false);
            return modified.HasValue ? FormatTimestamp(modified.Value) : "-";
        }

        private static DateTime? TryGetPathTimestamp(string path, bool isFolder, bool created)
        {
            try
            {
                if (isFolder)
                {
                    var info = new DirectoryInfo(path);
                    if (!info.Exists)
                    {
                        return null;
                    }

                    return created ? info.CreationTime : info.LastWriteTime;
                }

                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    return null;
                }

                return created ? fileInfo.CreationTime : fileInfo.LastWriteTime;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            string format = unitIndex == 0 ? "0" : "0.#";
            return $"{value.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
        }

        private string GetPhotoMetaText(string path, bool isFolder, bool isBackButton)
        {
            if (isBackButton)
            {
                return string.Empty;
            }

            var parts = new List<string>(3);

            if (showMetadataDimensions &&
                !isFolder &&
                File.Exists(path) &&
                IsPhotoPath(path) &&
                TryGetPhotoDimensions(path) is string dimensions &&
                !string.IsNullOrWhiteSpace(dimensions))
            {
                parts.Add(dimensions);
            }

            if (showMetadataSize)
            {
                string size = GetSizeText(path, isFolder);
                if (!string.IsNullOrWhiteSpace(size) && !string.Equals(size, "-", StringComparison.Ordinal))
                {
                    parts.Add(size);
                }
            }

            if (showMetadataModified)
            {
                var modified = TryGetPathTimestamp(path, isFolder, created: false);
                if (modified.HasValue)
                {
                    parts.Add(modified.Value.ToString("d", CultureInfo.CurrentCulture));
                }
            }

            return string.Join("  ", parts);
        }

        private string BuildPhotoMetadataTooltipText(string path, bool isFolder)
        {
            var lines = new List<string>(5)
            {
                $"{GetMetadataColumnLabelText(MetadataType)}: {GetItemTypeText(path, isBackButton: false, isFolder)}",
                $"{GetMetadataColumnLabelText(MetadataSize)}: {GetSizeText(path, isFolder)}",
                $"{GetMetadataColumnLabelText(MetadataDimensions)}: {GetDimensionsText(path, isFolder)}",
                $"{GetMetadataColumnLabelText(MetadataCreated)}: {GetCreatedText(path, isFolder)}",
                $"{GetMetadataColumnLabelText(MetadataModified)}: {GetModifiedText(path, isFolder)}"
            };

            return string.Join(Environment.NewLine, lines);
        }

        private static bool IsPhotoPath(string path)
        {
            string extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) && PhotoFileExtensions.Contains(extension);
        }

        private static string GetDimensionsText(string path, bool isFolder)
        {
            if (isFolder || !File.Exists(path) || !IsPhotoPath(path))
            {
                return "-";
            }

            string? dimensions = TryGetPhotoDimensions(path);
            return string.IsNullOrWhiteSpace(dimensions) ? "-" : dimensions;
        }

        private static bool TryGetPhotoPixelDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            lock (PhotoDimensionsCache)
            {
                if (PhotoDimensionsCache.TryGetValue(path, out var cached) &&
                    cached.Width > 0 &&
                    cached.Height > 0)
                {
                    width = cached.Width;
                    height = cached.Height;
                    return true;
                }
            }

            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);

                var frame = decoder.Frames.FirstOrDefault();
                if (frame == null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                {
                    return false;
                }

                width = frame.PixelWidth;
                height = frame.PixelHeight;
                lock (PhotoDimensionsCache)
                {
                    PhotoDimensionsCache[path] = (width, height);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetPhotoDimensions(string path)
        {
            return TryGetPhotoPixelDimensions(path, out int width, out int height)
                ? $"{width}x{height}"
                : null;
        }

        private static string FormatTimestamp(DateTime value)
        {
            return value.ToString("g", CultureInfo.CurrentCulture);
        }

        public bool TryGetItemNameLabel(ListBoxItem item, out TextBlock label)
        {
            label = null!;
            if (item.Content is not DependencyObject content)
            {
                return false;
            }

            var tagged = FindTaggedNameLabel(content);
            if (tagged != null)
            {
                label = tagged;
                return true;
            }

            var fallback = FindVisualChild<TextBlock>(content);
            if (fallback != null)
            {
                label = fallback;
                return true;
            }

            return false;
        }

        public bool TryGetEditableNameHost(ListBoxItem item, out System.Windows.Controls.Panel hostPanel, out TextBlock label, out int labelIndex)
        {
            hostPanel = null!;
            label = null!;
            labelIndex = -1;

            if (!TryGetItemNameLabel(item, out var resolvedLabel))
            {
                return false;
            }

            var parentPanel = FindAncestor<Panel>(resolvedLabel);
            if (parentPanel == null)
            {
                return false;
            }

            int index = parentPanel.Children.IndexOf(resolvedLabel);
            if (index < 0)
            {
                return false;
            }

            hostPanel = parentPanel;
            label = resolvedLabel;
            labelIndex = index;
            return true;
        }

        private static TextBlock? FindTaggedNameLabel(DependencyObject root)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock text &&
                    string.Equals(text.Tag as string, ItemNameTextTag, StringComparison.Ordinal))
                {
                    return text;
                }

                var nested = FindTaggedNameLabel(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        public void ApplyViewSettings(
            string? requestedViewMode,
            bool metadataType,
            bool metadataSize,
            bool metadataCreated,
            bool metadataModified,
            bool metadataDimensions,
            IEnumerable<string>? metadataOrderOverride = null,
            bool persistSettings = true)
        {
            string normalized = NormalizeViewMode(requestedViewMode);
            var normalizedOrder = NormalizeMetadataOrder(metadataOrderOverride ?? metadataOrder);
            bool changed =
                !string.Equals(viewMode, normalized, StringComparison.OrdinalIgnoreCase) ||
                showMetadataType != metadataType ||
                showMetadataSize != metadataSize ||
                showMetadataCreated != metadataCreated ||
                showMetadataModified != metadataModified ||
                showMetadataDimensions != metadataDimensions ||
                !metadataOrder.SequenceEqual(normalizedOrder, StringComparer.OrdinalIgnoreCase);

            viewMode = normalized;
            showMetadataType = metadataType;
            showMetadataSize = metadataSize;
            showMetadataCreated = metadataCreated;
            showMetadataModified = metadataModified;
            showMetadataDimensions = metadataDimensions;
            metadataOrder = normalizedOrder;

            if (!changed)
            {
                return;
            }

            RebuildListItemVisuals();

            if (persistSettings)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
        }

        private void RebuildListItemVisuals()
        {
            if (FileList == null)
            {
                return;
            }

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is not string path || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                bool isBackButton = IsParentNavigationPath(path);
                string displayName = isBackButton
                    ? BuildParentNavigationDisplayName(path)
                    : GetDisplayNameForPath(path);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = GetPathLeafName(path);
                }

                item.Content = CreateListBoxItem(displayName, path, isBackButton, _currentAppearance);
            }

            SortCurrentFolderItemsInPlace();
            UpdateWrapPanelWidth();
            UpdateDropZoneVisibility();
        }

        public void SetZoom(double newZoom)
        {
            zoomFactor = Math.Max(0.7, Math.Min(1.5, newZoom));
            ApplyZoom();
            MainWindow.SaveSettings();
        }

        private void ApplyZoom()
        {
            RebuildListItemVisuals();
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

            UpdateDetailItemWidths();
        }

        private double GetDetailsItemWidth()
        {
            double contentWidth = GetContentLayoutWidth();
            if (contentWidth <= 1)
            {
                contentWidth = ActualWidth > 0 ? Math.Max(260, ActualWidth - 34) : 560;
            }

            return Math.Max(260, contentWidth - 12);
        }

        private void UpdateDetailItemWidths()
        {
            if (FileList == null ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            double targetWidth = GetDetailsItemWidth();
            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is FrameworkElement element &&
                    Math.Abs(element.Width - targetWidth) > 0.5)
                {
                    element.Width = targetWidth;
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
