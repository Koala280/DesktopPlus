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
        private const string ParentNavigationTextPrefix = "\u21A9 ";
        private static readonly HashSet<string> PhotoFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif"
        };
        private static readonly Dictionary<string, (int Width, int Height)> PhotoDimensionsCache =
            new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> PhotoPreviewCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> PhotoDimensionsCacheOrder = new Queue<string>();
        private static readonly Queue<string> PhotoPreviewCacheOrder = new Queue<string>();
        private const int PhotoDimensionsCacheLimit = 4096;
        private const int PhotoPreviewCacheLimit = 220;
        private const int MaxNativePhotoDecodePixels = 32768;
        private static readonly Dictionary<string, ImageSource> ExplorerIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> ExplorerIconCacheOrder = new Queue<string>();
        private const int ExplorerIconCacheLimit = 512;
        private const double PhotoCardHorizontalPadding = 0;
        private const double PhotoCardHorizontalMargin = 0;
        private const double PhotoWrapSafetyDip = 0.35;

        private sealed class PhotoTileLayoutInfo
        {
            public Border ThumbnailFrame { get; init; } = null!;
            public System.Windows.Controls.Image ThumbnailImage { get; init; } = null!;
            public TextBlock NameText { get; init; } = null!;
            public TextBlock MetaText { get; init; } = null!;
            public double AspectRatio { get; init; } = 1.39;
            public bool IsPhotoFile { get; init; }
            public string? PhotoPath { get; init; }
        }

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

        private static bool IsPathSpecificIconExtension(string extension)
        {
            return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".scr", StringComparison.OrdinalIgnoreCase) ||
                PhotoFileExtensions.Contains(extension);
        }

        private static int NormalizeIconRequestSize(int requestedSize)
        {
            if (requestedSize <= 24) return 24;
            if (requestedSize <= 32) return 32;
            if (requestedSize <= 48) return 48;
            if (requestedSize <= 64) return 64;
            if (requestedSize <= 96) return 96;
            if (requestedSize <= 128) return 128;
            return 256;
        }

        private string BuildExplorerIconCacheKey(string path, bool isFolder, int normalizedSize)
        {
            if (isFolder)
            {
                return _useLightweightItemVisuals
                    ? $"folder|{normalizedSize}"
                    : $"folderpath|{normalizedSize}|{path}";
            }

            string extension = Path.GetExtension(path);
            bool usePathSpecificKey = !_useLightweightItemVisuals && IsPathSpecificIconExtension(extension);
            if (usePathSpecificKey)
            {
                return $"path|{normalizedSize}|{path}";
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                return $"file|{normalizedSize}";
            }

            return $"ext|{normalizedSize}|{extension}";
        }

        private static void StoreExplorerIconCacheEntry(string cacheKey, ImageSource iconSource)
        {
            lock (ExplorerIconCache)
            {
                if (ExplorerIconCache.ContainsKey(cacheKey))
                {
                    ExplorerIconCache[cacheKey] = iconSource;
                    return;
                }

                ExplorerIconCache[cacheKey] = iconSource;
                ExplorerIconCacheOrder.Enqueue(cacheKey);

                while (ExplorerIconCacheOrder.Count > ExplorerIconCacheLimit)
                {
                    string evictedKey = ExplorerIconCacheOrder.Dequeue();
                    ExplorerIconCache.Remove(evictedKey);
                }
            }
        }

        private static void StorePhotoPreviewCacheEntry(string path, ImageSource previewSource)
        {
            lock (PhotoPreviewCache)
            {
                if (PhotoPreviewCache.ContainsKey(path))
                {
                    PhotoPreviewCache[path] = previewSource;
                    return;
                }

                PhotoPreviewCache[path] = previewSource;
                PhotoPreviewCacheOrder.Enqueue(path);

                while (PhotoPreviewCacheOrder.Count > PhotoPreviewCacheLimit)
                {
                    string evictedPath = PhotoPreviewCacheOrder.Dequeue();
                    PhotoPreviewCache.Remove(evictedPath);
                }
            }
        }

        private static void StorePhotoDimensionsCacheEntry(string path, int width, int height)
        {
            lock (PhotoDimensionsCache)
            {
                if (PhotoDimensionsCache.ContainsKey(path))
                {
                    PhotoDimensionsCache[path] = (width, height);
                    return;
                }

                PhotoDimensionsCache[path] = (width, height);
                PhotoDimensionsCacheOrder.Enqueue(path);

                while (PhotoDimensionsCacheOrder.Count > PhotoDimensionsCacheLimit)
                {
                    string evictedPath = PhotoDimensionsCacheOrder.Dequeue();
                    PhotoDimensionsCache.Remove(evictedPath);
                }
            }
        }

        private ImageSource? LoadExplorerStyleIcon(string filePath, bool isFolder, int size = 256)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            int normalizedSize = NormalizeIconRequestSize(size);
            string cacheKey = BuildExplorerIconCacheKey(filePath, isFolder, normalizedSize);
            lock (ExplorerIconCache)
            {
                if (ExplorerIconCache.TryGetValue(cacheKey, out var cachedIcon))
                {
                    return cachedIcon;
                }
            }

            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iidImageFactory = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(filePath, null!, iidImageFactory, out IShellItemImageFactory imageFactory);

                SIZE iconSize = new SIZE(normalizedSize, normalizedSize);

                imageFactory.GetImage(iconSize, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out hBitmap);

                if (hBitmap != IntPtr.Zero)
                {
                    var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(normalizedSize, normalizedSize));

                    if (bitmapSource.CanFreeze)
                    {
                        bitmapSource.Freeze();
                    }

                    StoreExplorerIconCacheEntry(cacheKey, bitmapSource);
                    return bitmapSource;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon konnte nicht geladen werden: {ex.Message}");
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }

            return null;
        }

        private ImageSource? LoadExplorerStyleIcon(string filePath, int size = 256)
        {
            bool isFolder = false;
            try
            {
                isFolder = Directory.Exists(filePath);
            }
            catch
            {
            }

            return LoadExplorerStyleIcon(filePath, isFolder, size);
        }

        private static int GetImageSourcePixelWidth(ImageSource? source)
        {
            if (source is BitmapImage bitmapImage)
            {
                if (bitmapImage.DecodePixelWidth > 0)
                {
                    return bitmapImage.DecodePixelWidth;
                }

                if (bitmapImage.PixelWidth > 0)
                {
                    return bitmapImage.PixelWidth;
                }
            }

            if (source is BitmapSource bitmapSource &&
                bitmapSource.PixelWidth > 0)
            {
                return bitmapSource.PixelWidth;
            }

            return 0;
        }

        private static int ResolvePhotoDecodePixels(string path, int decodePixelWidth)
        {
            int requestedPixels = Math.Max(128, decodePixelWidth);
            if (TryGetPhotoPixelDimensions(path, out int nativeWidth, out _))
            {
                int boundedNativeWidth = Math.Clamp(nativeWidth, 128, MaxNativePhotoDecodePixels);
                requestedPixels = Math.Max(requestedPixels, boundedNativeWidth);
            }

            return requestedPixels;
        }

        private static ImageSource? LoadPhotoPreviewSource(string path, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsPhotoPath(path))
            {
                return null;
            }

            int requestedPixels = ResolvePhotoDecodePixels(path, decodePixelWidth);
            lock (PhotoPreviewCache)
            {
                if (PhotoPreviewCache.TryGetValue(path, out var cached))
                {
                    int cachedPixels = GetImageSourcePixelWidth(cached);
                    if (cachedPixels >= requestedPixels)
                    {
                        return cached;
                    }
                }
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = requestedPixels;
                bitmap.EndInit();
                bitmap.Freeze();

                StorePhotoPreviewCacheEntry(path, bitmap);

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

        private ListBoxItem CreateFileListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            var item = new ListBoxItem
            {
                Content = CreateListBoxItem(displayName, path, isBackButton, appearance),
                Tag = path
            };

            ApplyListItemContainerSpacing(item);
            return item;
        }

        private void ApplyListItemContainerSpacing(ListBoxItem item)
        {
            bool isPhotoMode = string.Equals(
                NormalizeViewMode(viewMode),
                ViewModePhotos,
                StringComparison.OrdinalIgnoreCase);

            if (isPhotoMode)
            {
                item.BorderThickness = new Thickness(0);
                item.Padding = new Thickness(0);
                item.Margin = new Thickness(0);
                return;
            }

            item.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
            item.ClearValue(System.Windows.Controls.Control.PaddingProperty);
            item.ClearValue(FrameworkElement.MarginProperty);
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
                Source = LoadExplorerStyleIcon(path, isFolder, (int)(48 * zoomFactor)),
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
            if (!isBackButton)
            {
                AttachMetadataTooltip(panel, path, isFolder, Math.Max(8, textSize - 1));
            }

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
            RebuildListItemVisuals(sortItems: false);
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
            ApplyListItemContainerSpacing(backItem);
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
                Source = LoadExplorerStyleIcon(path, isFolder, (int)Math.Max(48, iconSize * 2)),
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
            if (!isBackButton)
            {
                AttachMetadataTooltip(panel, path, isFolder, metaSize);
            }

            AttachPressedOpacity(panel, baseOpacity);
            return panel;
        }

        private FrameworkElement CreatePhotoListBoxItem(string displayName, string path, bool isBackButton, AppearanceSettings? appearance = null)
        {
            var activeAppearance = appearance ?? _currentAppearance ?? MainWindow.Appearance;
            bool isFolder = isBackButton || Directory.Exists(path);
            bool isPhotoFile = !isBackButton && !isFolder && IsPhotoPath(path);
            bool isHiddenItem = !isBackButton && showHiddenItems && IsHiddenPath(path);
            double baseOpacity = isHiddenItem ? 0.58 : 1.0;
            double thumbWidth = Math.Max(86, 122 * zoomFactor);
            const double defaultPreviewAspect = 1.39;
            double previewAspect = defaultPreviewAspect;
            if (!isBackButton &&
                !isFolder &&
                isPhotoFile &&
                TryGetPhotoPixelDimensions(path, out int pixelWidth, out int pixelHeight))
            {
                previewAspect = Math.Clamp((double)pixelWidth / Math.Max(1, pixelHeight), 0.08, 20.0);
            }

            double previewHeight, previewWidth;
            if (isPhotoFile)
            {
                previewHeight = Math.Clamp(thumbWidth / previewAspect, thumbWidth * 0.15, thumbWidth * 2.0);
                previewWidth = Math.Clamp(previewHeight * previewAspect, thumbWidth * 0.4, thumbWidth * 4.0);
            }
            else
            {
                // Folders and non-image files: compact fixed size
                previewWidth = thumbWidth;
                previewHeight = thumbWidth * 0.85;
            }
            double cardWidth = previewWidth + (isPhotoFile ? PhotoCardHorizontalPadding : 24);
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
                Margin = new Thickness(isPhotoFile ? PhotoCardHorizontalMargin : 2),
                Opacity = baseOpacity
            };

            var thumbnailFrame = new Border
            {
                Width = previewWidth,
                Height = previewHeight,
                CornerRadius = new CornerRadius(isPhotoFile ? 0 : 8),
                BorderBrush = isPhotoFile ? Brushes.Transparent : (TryFindResource("PanelBorder") as Brush ?? Brushes.Transparent),
                BorderThickness = isPhotoFile ? new Thickness(0) : new Thickness(1),
                Background = isPhotoFile ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0),
                ClipToBounds = true
            };

            System.Windows.Controls.Image icon = new System.Windows.Controls.Image
            {
                Source = (!isBackButton && !isFolder && isPhotoFile)
                    ? (LoadPhotoPreviewSource(
                        path,
                        GetRequestedPhotoDecodePixels(previewWidth, previewHeight, 2.6))
                        ?? LoadExplorerStyleIcon(path, isFolder, 512))
                    : LoadExplorerStyleIcon(
                        path,
                        isFolder,
                        _useLightweightItemVisuals
                            ? 128
                            : (IsPhotoPath(path) ? 256 : 128)),
                Width = previewWidth,
                Height = previewHeight,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(
                icon,
                isPhotoFile ? BitmapScalingMode.Fant : BitmapScalingMode.HighQuality);
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
                Text = _useLightweightItemVisuals
                    ? string.Empty
                    : GetPhotoMetaText(path, isFolder, isBackButton),
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
            if (isPhotoFile)
            {
                // Collage mode: hide text for photo files
                nameText.Visibility = Visibility.Collapsed;
                metaText.Visibility = Visibility.Collapsed;
            }
            panel.Children.Add(nameText);
            if (!string.IsNullOrWhiteSpace(metaText.Text))
            {
                panel.Children.Add(metaText);
            }

            panel.Tag = new PhotoTileLayoutInfo
            {
                ThumbnailFrame = thumbnailFrame,
                ThumbnailImage = icon,
                NameText = nameText,
                MetaText = metaText,
                AspectRatio = previewAspect,
                IsPhotoFile = isPhotoFile,
                PhotoPath = isPhotoFile ? path : null
            };

            if (!isBackButton)
            {
                AttachMetadataTooltip(panel, path, isFolder, metaSize);
            }

            AttachPressedOpacity(panel, baseOpacity);
            return panel;
        }

        private void AttachMetadataTooltip(FrameworkElement target, string path, bool isFolder, double fontSize)
        {
            var tooltipContent = new TextBlock
            {
                Text = string.Empty,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = Math.Max(8, fontSize),
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

                string tooltipText = BuildMetadataTooltipText(path, isFolder);
                tooltipContent.Tag = tooltipText;
                tooltipContent.Text = tooltipText;
            };

            ToolTipService.SetToolTip(target, metadataToolTip);
            ToolTipService.SetInitialShowDelay(target, 700);
            ToolTipService.SetBetweenShowDelay(target, 120);
            ToolTipService.SetShowDuration(target, 16000);
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

        private string BuildMetadataTooltipText(string path, bool isFolder)
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
                StorePhotoDimensionsCacheEntry(path, width, height);

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

        private void RebuildListItemVisuals(bool sortItems = true)
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
                ApplyListItemContainerSpacing(item);
            }

            if (sortItems)
            {
                SortCurrentFolderItemsInPlace();
            }
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
            if (string.Equals(NormalizeViewMode(viewMode), ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                UpdateWrapPanelWidth();
                return;
            }

            RebuildListItemVisuals(sortItems: false);
        }

        private void ContentContainer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;
                ApplyMouseWheelZoom(e.Delta);
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
            // We use item count x item width for precision, avoiding sub-pixel drift.
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
            bool isPhotoMode = string.Equals(
                NormalizeViewMode(viewMode),
                ViewModePhotos,
                StringComparison.OrdinalIgnoreCase);
            ApplyPhotoModeItemCompaction(isPhotoMode);
            FileList.UseLayoutRounding = !isPhotoMode;
            ContentContainer.UseLayoutRounding = !isPhotoMode;

            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            if (wrapPanel != null)
            {
                var targetMargin = isPhotoMode ? new Thickness(0) : new Thickness(4);
                if (!wrapPanel.Margin.Equals(targetMargin))
                {
                    wrapPanel.Margin = targetMargin;
                    wrapPanel.InvalidateMeasure();
                    wrapPanel.InvalidateArrange();
                }
                wrapPanel.UseLayoutRounding = !isPhotoMode;
                wrapPanel.SnapsToDevicePixels = !isPhotoMode;

                double baseWidth = GetContentLayoutWidth();
                double availableWidth = Math.Max(0, baseWidth - GetHorizontalMargin(wrapPanel));
                if (isPhotoMode)
                {
                    availableWidth = Math.Max(0, availableWidth - PhotoWrapSafetyDip - GetPhotoScrollbarReserveWidth());
                }
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

            UpdatePhotoTileLayouts();
            UpdateDetailItemWidths();
        }

        private void ApplyPhotoModeItemCompaction(bool isPhotoMode)
        {
            if (FileList == null)
            {
                return;
            }

            foreach (ListBoxItem item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is not FrameworkElement root ||
                    root.Tag is not PhotoTileLayoutInfo layout)
                {
                    if (isPhotoMode &&
                        item.Tag is string pathTag &&
                        !IsPhotoPath(pathTag))
                    {
                        item.Visibility = Visibility.Collapsed;
                        item.IsHitTestVisible = false;
                    }
                    else
                    {
                        item.Visibility = Visibility.Visible;
                        item.IsHitTestVisible = true;
                    }
                    continue;
                }

                if (isPhotoMode)
                {
                    bool isPhoto = layout.IsPhotoFile;
                    item.Visibility = isPhoto ? Visibility.Visible : Visibility.Collapsed;
                    item.IsHitTestVisible = isPhoto;
                    item.Margin = new Thickness(0);
                    item.Padding = new Thickness(0);
                    item.BorderThickness = new Thickness(0);

                    if (!isPhoto)
                    {
                        item.Width = 0;
                        item.Height = 0;
                        item.MinWidth = 0;
                        item.MinHeight = 0;
                        item.MaxWidth = 0;
                        item.MaxHeight = 0;
                        item.Opacity = 0;
                        item.IsHitTestVisible = false;
                        continue;
                    }

                    // Keep photo containers chrome-free so justified width math matches WrapPanel layout.
                    item.ClearValue(FrameworkElement.WidthProperty);
                    item.ClearValue(FrameworkElement.HeightProperty);
                    item.ClearValue(FrameworkElement.MinWidthProperty);
                    item.ClearValue(FrameworkElement.MinHeightProperty);
                    item.ClearValue(FrameworkElement.MaxWidthProperty);
                    item.ClearValue(FrameworkElement.MaxHeightProperty);
                    // Avoid "load once as tiles, then morph into collage" flicker.
                    // The collage pass makes items visible after final size is applied.
                    item.Opacity = 0;
                    continue;
                }

                item.Visibility = Visibility.Visible;
                item.ClearValue(FrameworkElement.WidthProperty);
                item.ClearValue(FrameworkElement.HeightProperty);
                item.ClearValue(FrameworkElement.MinWidthProperty);
                item.ClearValue(FrameworkElement.MinHeightProperty);
                item.ClearValue(FrameworkElement.MaxWidthProperty);
                item.ClearValue(FrameworkElement.MaxHeightProperty);
                item.ClearValue(FrameworkElement.MarginProperty);
                item.ClearValue(System.Windows.Controls.Control.PaddingProperty);
                item.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
                item.Opacity = 1;
                item.IsHitTestVisible = true;
            }
        }

        private void UpdatePhotoTileLayouts()
        {
            if (FileList == null ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            double contentWidth = GetContentLayoutWidth();
            if (contentWidth <= 1)
            {
                return;
            }

            // Horizontal gap between collage items; keep at zero for dense packing.
            double gap = 0;
            var wrapPanel = FindVisualChild<WrapPanel>(FileList);
            double wrapWidth = 0;
            if (wrapPanel != null)
            {
                wrapWidth = wrapPanel.Width > 1
                    ? wrapPanel.Width
                    : Math.Max(0, contentWidth - GetHorizontalMargin(wrapPanel));
            }
            double fallbackWidth = Math.Max(0, contentWidth - GetPhotoScrollbarReserveWidth());
            double availableWidth = Math.Max(120, wrapWidth > 1 ? wrapWidth : fallbackWidth);
            double dpiScaleX = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                dpiScaleX = Math.Max(1.0, dpi.DpiScaleX);
            }
            catch
            {
            }

            double pixelAlignedAvailableWidth = Math.Floor(availableWidth * dpiScaleX) / dpiScaleX;
            if (pixelAlignedAvailableWidth > 1)
            {
                availableWidth = pixelAlignedAvailableWidth;
            }

            availableWidth = Math.Max(120, availableWidth - PhotoWrapSafetyDip);
            double targetRowHeight = Math.Clamp(180 * zoomFactor, 110, 280);
            var photos = new List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>();
            foreach (ListBoxItem item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Visibility != Visibility.Visible ||
                    item.Content is not FrameworkElement root ||
                    root.Tag is not PhotoTileLayoutInfo layout ||
                    !layout.IsPhotoFile)
                {
                    continue;
                }
                double measuredAspect = layout.AspectRatio;
                if (layout.ThumbnailImage.Source is BitmapSource bitmapSource &&
                    bitmapSource.PixelWidth > 0 &&
                    bitmapSource.PixelHeight > 0)
                {
                    measuredAspect = (double)bitmapSource.PixelWidth / bitmapSource.PixelHeight;
                }
                double aspect = Math.Clamp(measuredAspect, 0.08, 20.0);
                photos.Add((item, root, layout, aspect));
            }
            if (photos.Count == 0)
            {
                return;
            }

            var rows = BuildPhotoRows(photos, availableWidth, targetRowHeight);
            RebalancePhotoRows(rows);

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row.Count == 0)
                {
                    continue;
                }

                int count = row.Count;
                double rowAspectSum = row.Sum(entry => entry.Aspect);
                if (rowAspectSum <= 0.0001)
                {
                    continue;
                }

                double usableWidth = Math.Max(count / dpiScaleX, availableWidth - ((count - 1) * gap));
                int targetRowPixels = Math.Max(count, (int)Math.Floor(usableWidth * dpiScaleX));
                int[] pixelWidths = DistributeJustifiedPixels(
                    row.Select(entry => entry.Aspect).ToArray(),
                    targetRowPixels);
                double rowHeight = (targetRowPixels / dpiScaleX) / rowAspectSum;
                for (int j = 0; j < count; j++)
                {
                    double width = pixelWidths[j] / dpiScaleX;
                    ApplyPhotoTileSize(row[j].Container, row[j].Root, row[j].Layout, width, rowHeight, leftOffset: 0);
                    ReloadPhotoIfNeeded(row[j].Layout, width, rowHeight);
                }
            }
        }

        private List<List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>> BuildPhotoRows(
            List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)> photos,
            double availableWidth,
            double targetRowHeight)
        {
            var rows = new List<List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>>();
            if (photos.Count == 0)
            {
                return rows;
            }

            var currentRow = new List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>();
            double currentAspectSum = 0;
            for (int i = 0; i < photos.Count; i++)
            {
                var photo = photos[i];
                currentRow.Add(photo);
                currentAspectSum += photo.Aspect;

                int count = currentRow.Count;
                if (count < 2)
                {
                    continue;
                }

                double rowHeight = GetJustifiedRowHeight(availableWidth, currentAspectSum, count, gap: 0);
                bool reachedTargetHeight = rowHeight <= targetRowHeight;
                bool safetyCutoff = count >= 7;
                if (reachedTargetHeight || safetyCutoff)
                {
                    rows.Add(currentRow);
                    currentRow = new List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>();
                    currentAspectSum = 0;
                }
            }

            if (currentRow.Count > 0)
            {
                rows.Add(currentRow);
            }

            return rows;
        }

        private static void RebalancePhotoRows(
            List<List<(ListBoxItem Container, FrameworkElement Root, PhotoTileLayoutInfo Layout, double Aspect)>> rows)
        {
            if (rows.Count < 2)
            {
                return;
            }

            // Avoid 2+1 endings that leave a visible half-empty last row.
            for (int i = rows.Count - 1; i > 0; i--)
            {
                var current = rows[i];
                var previous = rows[i - 1];
                if (current.Count != 1)
                {
                    continue;
                }

                if (previous.Count >= 3)
                {
                    var moved = previous[^1];
                    previous.RemoveAt(previous.Count - 1);
                    current.Insert(0, moved);
                    continue;
                }

                if (previous.Count == 2)
                {
                    previous.Add(current[0]);
                    rows.RemoveAt(i);
                }
            }
        }
        private static double GetJustifiedRowHeight(double availableWidth, double aspectSum, int count, double gap)
        {
            if (count <= 0 || aspectSum <= 0.0001)
            {
                return 0;
            }
            double usableWidth = availableWidth - ((count - 1) * gap);
            if (usableWidth <= 0)
            {
                return 0;
            }
            return usableWidth / aspectSum;
        }
        private static int[] DistributeJustifiedPixels(double[] aspects, int targetPixels)
        {
            int count = aspects.Length;
            var result = new int[count];
            if (count == 0 || targetPixels <= 0)
            {
                return result;
            }
            double aspectSum = Math.Max(0.0001, aspects.Sum());
            var fractions = new (int Index, double Fraction)[count];
            int usedPixels = 0;
            for (int i = 0; i < count; i++)
            {
                double ideal = targetPixels * (aspects[i] / aspectSum);
                int baseWidth = Math.Max(1, (int)Math.Floor(ideal));
                result[i] = baseWidth;
                usedPixels += baseWidth;
                fractions[i] = (i, ideal - baseWidth);
            }
            int delta = targetPixels - usedPixels;
            if (delta > 0)
            {
                foreach (var entry in fractions.OrderByDescending(f => f.Fraction).ThenBy(f => f.Index))
                {
                    if (delta <= 0)
                    {
                        break;
                    }
                    result[entry.Index]++;
                    delta--;
                }
            }
            else if (delta < 0)
            {
                foreach (var entry in fractions.OrderBy(f => f.Fraction).ThenByDescending(f => f.Index))
                {
                    if (delta >= 0)
                    {
                        break;
                    }
                    if (result[entry.Index] > 1)
                    {
                        result[entry.Index]--;
                        delta++;
                    }
                }
            }
            int cursor = 0;
            while (delta > 0)
            {
                result[cursor % count]++;
                cursor++;
                delta--;
            }
            cursor = 0;
            while (delta < 0 && cursor < count * 3)
            {
                int index = cursor % count;
                if (result[index] > 1)
                {
                    result[index]--;
                    delta++;
                }
                cursor++;
            }
            return result;
        }
        private int GetRequestedPhotoDecodePixels(double displayWidth, double displayHeight, double oversampleFactor)
        {
            double maxDisplayPixels = Math.Max(32, Math.Max(displayWidth, displayHeight));
            double dpiScale = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                dpiScale = Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
            }
            catch
            {
            }

            double requested = maxDisplayPixels * dpiScale * Math.Max(1.0, oversampleFactor);
            return (int)Math.Ceiling(Math.Max(128, requested));
        }

        /// <summary>
        /// Reload the photo thumbnail at higher resolution if the current decode size is too small.
        /// </summary>
        private void ReloadPhotoIfNeeded(PhotoTileLayoutInfo layout, double displayWidth, double displayHeight)
        {
            if (!layout.IsPhotoFile ||
                string.IsNullOrWhiteSpace(layout.PhotoPath))
            {
                return;
            }

            int neededPixels = GetRequestedPhotoDecodePixels(displayWidth, displayHeight, 2.8);
            neededPixels = ResolvePhotoDecodePixels(layout.PhotoPath, neededPixels);
            int currentPixels = GetImageSourcePixelWidth(layout.ThumbnailImage.Source);

            // Only reload if we need visibly more source resolution.
            if (currentPixels > 0 && neededPixels <= currentPixels * 1.08)
            {
                return;
            }

            var upgraded = LoadPhotoPreviewSource(layout.PhotoPath, neededPixels);
            if (upgraded != null &&
                !ReferenceEquals(layout.ThumbnailImage.Source, upgraded))
            {
                layout.ThumbnailImage.Source = upgraded;
            }
        }

        private static void ApplyPhotoTileSize(
            ListBoxItem container,
            FrameworkElement root,
            PhotoTileLayoutInfo layout,
            double previewWidth,
            double previewHeight,
            double leftOffset)
        {
            double cardWidth = previewWidth + PhotoCardHorizontalPadding;
            double imageWidth = Math.Max(18, previewWidth);
            double imageHeight = Math.Max(18, previewHeight);
            double textWidth = Math.Max(84, cardWidth - 8);

            var targetMargin = leftOffset > 0.5
                ? new Thickness(leftOffset, 0, 0, 0)
                : new Thickness(0);
            if (!container.Margin.Equals(targetMargin))
            {
                container.Margin = targetMargin;
            }

            if (Math.Abs(container.Width - cardWidth) > 0.5)
            {
                container.Width = cardWidth;
            }

            if (Math.Abs(container.Height - imageHeight) > 0.5)
            {
                container.Height = imageHeight;
            }

            container.Opacity = 1;
            container.IsHitTestVisible = true;

            if (Math.Abs(root.Width - cardWidth) > 0.5)
            {
                root.Width = cardWidth;
            }
            root.UseLayoutRounding = false;

            if (Math.Abs(layout.ThumbnailFrame.Width - previewWidth) > 0.5)
            {
                layout.ThumbnailFrame.Width = previewWidth;
            }
            layout.ThumbnailFrame.UseLayoutRounding = false;

            if (Math.Abs(layout.ThumbnailFrame.Height - previewHeight) > 0.5)
            {
                layout.ThumbnailFrame.Height = previewHeight;
            }

            if (Math.Abs(layout.ThumbnailImage.Width - imageWidth) > 0.5)
            {
                layout.ThumbnailImage.Width = imageWidth;
            }
            layout.ThumbnailImage.UseLayoutRounding = false;
            layout.ThumbnailImage.SnapsToDevicePixels = false;

            if (Math.Abs(layout.ThumbnailImage.Height - imageHeight) > 0.5)
            {
                layout.ThumbnailImage.Height = imageHeight;
            }

            if (Math.Abs(layout.NameText.Width - textWidth) > 0.5)
            {
                layout.NameText.Width = textWidth;
            }

            if (Math.Abs(layout.MetaText.Width - textWidth) > 0.5)
            {
                layout.MetaText.Width = textWidth;
            }
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

        private double GetPhotoScrollbarReserveWidth()
        {
            if (ContentContainer == null)
            {
                return 0;
            }

            if (ContentContainer.ComputedVerticalScrollBarVisibility != Visibility.Visible)
            {
                return 0;
            }

            double measuredReserve = ContentContainer.ActualWidth - ContentContainer.ViewportWidth;
            if (double.IsNaN(measuredReserve) || double.IsInfinity(measuredReserve))
            {
                return 0;
            }

            return Math.Max(0, measuredReserve);
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

