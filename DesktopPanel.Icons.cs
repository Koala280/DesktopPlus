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
using Point = System.Windows.Point;

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
        private static readonly Dictionary<string, long> PhotoPreviewCacheSizeBytes =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> PhotoDimensionsCacheOrder = new Queue<string>();
        private static readonly Queue<string> PhotoPreviewCacheOrder = new Queue<string>();
        private const int PhotoDimensionsCacheLimit = 4096;
        private const int PhotoPreviewCacheLimit = 160;
        private const int MaxNativePhotoDecodePixels = 16384;
        private const int MaxPhotoPreviewDecodePixels = 4096;
        private const long PhotoPreviewCacheBudgetBytes = 128L * 1024L * 1024L;
        private static long PhotoPreviewCacheTotalBytes;
        private static readonly Dictionary<string, ImageSource> ExplorerIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> ExplorerIconCacheSizeBytes =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> ExplorerIconCacheOrder = new Queue<string>();
        private const int ExplorerIconCacheLimit = 320;
        private const long ExplorerIconCacheBudgetBytes = 64L * 1024L * 1024L;
        private static long ExplorerIconCacheTotalBytes;
        private const double PhotoCardHorizontalPadding = 0;
        private const double PhotoCardHorizontalMargin = 0;
        private const double PhotoWrapSafetyDip = 0.35;
        private static readonly Brush PhotoTileHoverOverlayBrush = CreateFrozenBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        private static readonly Brush PhotoTileHoverBorderBrush = CreateFrozenBrush(Color.FromArgb(0x72, 0xC8, 0xE2, 0xFF));
        private static readonly Brush PhotoTileSelectedOverlayBrush = CreateFrozenBrush(Color.FromArgb(0x16, 0x74, 0xB6, 0xF4));
        private static readonly Brush PhotoTileSelectedBorderBrush = CreateFrozenBrush(Color.FromArgb(0xAA, 0x8E, 0xCD, 0xFF));
        private static readonly Brush PhotoTileSelectedHoverOverlayBrush = CreateFrozenBrush(Color.FromArgb(0x20, 0x80, 0xC0, 0xFF));
        private static readonly Brush PhotoTileSelectedHoverBorderBrush = CreateFrozenBrush(Color.FromArgb(0xCC, 0xB5, 0xDE, 0xFF));
        private static readonly Style PhotoTileOverlayStyle = CreatePhotoTileOverlayStyle();

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

        private sealed class PhotoLayoutEntry
        {
            public ListBoxItem Container { get; init; } = null!;
            public FrameworkElement Root { get; init; } = null!;
            public PhotoTileLayoutInfo Layout { get; init; } = null!;
            public double Aspect { get; init; }
        }

        private sealed class PhotoMosaicColumn
        {
            public List<PhotoLayoutEntry> Entries { get; } = new List<PhotoLayoutEntry>();
        }

        private sealed class PhotoMosaicRow
        {
            public List<PhotoMosaicColumn> Columns { get; } = new List<PhotoMosaicColumn>();
            public double Height { get; init; }
        }

        private sealed class PhotoMosaicCandidate
        {
            public List<PhotoMosaicColumn> Columns { get; } = new List<PhotoMosaicColumn>();
            public HashSet<PhotoLayoutEntry> SelectedEntries { get; } = new HashSet<PhotoLayoutEntry>();
            public double Height { get; init; }
            public double Score { get; init; }
        }

        private readonly ItemsPanelTemplate _standardItemsPanelTemplate = CreateWrapItemsPanelTemplate();
        private readonly ItemsPanelTemplate _photoItemsPanelTemplate = CreatePhotoCanvasItemsPanelTemplate();
        private bool _isPhotoItemsPanelActive;

        private enum DetailsSortColumn
        {
            Name,
            Type,
            Size,
            Created,
            Modified,
            Dimensions,
            Authors,
            Categories,
            Tags,
            Title
        }

        private DetailsSortColumn _detailsSortColumn = DetailsSortColumn.Name;
        private bool _detailsSortAscending = true;
        private bool _detailsSortActive;
        private readonly List<string> _detailsDefaultOrderPaths = new List<string>();
        private Border? _detailsHeaderDragSource;
        private string? _detailsHeaderDragKey;
        private Point _detailsHeaderDragStartPoint;
        private bool _detailsHeaderDragging;
        private Border? _detailsHeaderDropTarget;
        private bool _detailsHeaderDropInsertAfter;
        private string? _detailsHeaderContextColumnKey;
        private bool _detailsHeaderResizing;
        private string? _detailsHeaderResizeLeftKey;
        private string? _detailsHeaderResizeRightKey;
        private Point _detailsHeaderResizeStartPoint;
        private double _detailsHeaderResizeStartLeftWidth;
        private double _detailsHeaderResizeStartRightWidth;
        private const double DetailsHeaderDragThreshold = 5.0;
        private const double DetailsHeaderResizeHitWidth = 8.0;
        private const string DetailsHeaderDropIndicatorTag = "DesktopPlus.DetailsHeaderDropIndicator";

        private static ItemsPanelTemplate CreateWrapItemsPanelTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(WrapPanel));
            factory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
            factory.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
            factory.SetValue(Panel.IsItemsHostProperty, true);
            return new ItemsPanelTemplate(factory);
        }

        private static ItemsPanelTemplate CreatePhotoCanvasItemsPanelTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.Canvas));
            factory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            factory.SetValue(Panel.IsItemsHostProperty, true);
            return new ItemsPanelTemplate(factory);
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Style CreatePhotoTileOverlayStyle()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
            style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));

            var hoverTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsMouseOver")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem),
                        1)
                },
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, PhotoTileHoverOverlayBrush));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, PhotoTileHoverBorderBrush));
            hoverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Triggers.Add(hoverTrigger);

            var selectedTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsSelected")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem),
                        1)
                },
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, PhotoTileSelectedOverlayBrush));
            selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, PhotoTileSelectedBorderBrush));
            selectedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Triggers.Add(selectedTrigger);

            var selectedHoverTrigger = new MultiDataTrigger();
            selectedHoverTrigger.Conditions.Add(new Condition
            {
                Binding = new System.Windows.Data.Binding("IsSelected")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem),
                        1)
                },
                Value = true
            });
            selectedHoverTrigger.Conditions.Add(new Condition
            {
                Binding = new System.Windows.Data.Binding("IsMouseOver")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem),
                        1)
                },
                Value = true
            });
            selectedHoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, PhotoTileSelectedHoverOverlayBrush));
            selectedHoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, PhotoTileSelectedHoverBorderBrush));
            selectedHoverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Triggers.Add(selectedHoverTrigger);

            return style;
        }

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
                long entryBytes = GetApproxDecodedByteSize(iconSource);
                if (ExplorerIconCache.ContainsKey(cacheKey))
                {
                    ExplorerIconCache[cacheKey] = iconSource;
                    if (ExplorerIconCacheSizeBytes.TryGetValue(cacheKey, out long existingBytes))
                    {
                        ExplorerIconCacheTotalBytes = Math.Max(0L, ExplorerIconCacheTotalBytes - existingBytes);
                    }

                    ExplorerIconCacheSizeBytes[cacheKey] = entryBytes;
                    ExplorerIconCacheTotalBytes += entryBytes;
                }
                else
                {
                    ExplorerIconCache[cacheKey] = iconSource;
                    ExplorerIconCacheOrder.Enqueue(cacheKey);
                    ExplorerIconCacheSizeBytes[cacheKey] = entryBytes;
                    ExplorerIconCacheTotalBytes += entryBytes;
                }

                while (ExplorerIconCacheOrder.Count > ExplorerIconCacheLimit ||
                    ExplorerIconCacheTotalBytes > ExplorerIconCacheBudgetBytes)
                {
                    if (ExplorerIconCacheOrder.Count == 0)
                    {
                        break;
                    }

                    string evictedKey = ExplorerIconCacheOrder.Dequeue();
                    if (ExplorerIconCache.Remove(evictedKey) &&
                        ExplorerIconCacheSizeBytes.TryGetValue(evictedKey, out long evictedBytes))
                    {
                        ExplorerIconCacheTotalBytes = Math.Max(0L, ExplorerIconCacheTotalBytes - evictedBytes);
                    }

                    ExplorerIconCacheSizeBytes.Remove(evictedKey);
                }
            }
        }

        private static void StorePhotoPreviewCacheEntry(string path, ImageSource previewSource)
        {
            lock (PhotoPreviewCache)
            {
                long entryBytes = GetApproxDecodedByteSize(previewSource);
                if (PhotoPreviewCache.ContainsKey(path))
                {
                    PhotoPreviewCache[path] = previewSource;
                    if (PhotoPreviewCacheSizeBytes.TryGetValue(path, out long existingBytes))
                    {
                        PhotoPreviewCacheTotalBytes = Math.Max(0L, PhotoPreviewCacheTotalBytes - existingBytes);
                    }

                    PhotoPreviewCacheSizeBytes[path] = entryBytes;
                    PhotoPreviewCacheTotalBytes += entryBytes;
                }
                else
                {
                    PhotoPreviewCache[path] = previewSource;
                    PhotoPreviewCacheOrder.Enqueue(path);
                    PhotoPreviewCacheSizeBytes[path] = entryBytes;
                    PhotoPreviewCacheTotalBytes += entryBytes;
                }

                while (PhotoPreviewCacheOrder.Count > PhotoPreviewCacheLimit ||
                    PhotoPreviewCacheTotalBytes > PhotoPreviewCacheBudgetBytes)
                {
                    if (PhotoPreviewCacheOrder.Count == 0)
                    {
                        break;
                    }

                    string evictedPath = PhotoPreviewCacheOrder.Dequeue();
                    if (PhotoPreviewCache.Remove(evictedPath) &&
                        PhotoPreviewCacheSizeBytes.TryGetValue(evictedPath, out long evictedBytes))
                    {
                        PhotoPreviewCacheTotalBytes = Math.Max(0L, PhotoPreviewCacheTotalBytes - evictedBytes);
                    }

                    PhotoPreviewCacheSizeBytes.Remove(evictedPath);
                }
            }
        }

        private static long GetApproxDecodedByteSize(ImageSource? source)
        {
            if (source is BitmapSource bitmap &&
                bitmap.PixelWidth > 0 &&
                bitmap.PixelHeight > 0)
            {
                int bitsPerPixel = bitmap.Format.BitsPerPixel;
                if (bitsPerPixel <= 0)
                {
                    bitsPerPixel = 32;
                }

                long rowBytes = ((long)bitmap.PixelWidth * bitsPerPixel + 7L) / 8L;
                return Math.Max(0L, rowBytes * bitmap.PixelHeight);
            }

            return 0;
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
            int requestedPixels = Math.Clamp(decodePixelWidth, 128, MaxPhotoPreviewDecodePixels);
            if (TryGetPhotoPixelDimensions(path, out int nativeWidth, out _))
            {
                int boundedNativeWidth = Math.Clamp(nativeWidth, 128, MaxNativePhotoDecodePixels);
                requestedPixels = Math.Min(requestedPixels, boundedNativeWidth);
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
                Tag = path,
                Focusable = !isBackButton
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

        private void EnsureItemsHostPanel(bool isPhotoMode)
        {
            if (FileList == null || _isPhotoItemsPanelActive == isPhotoMode)
            {
                return;
            }

            FileList.ItemsPanel = isPhotoMode ? _photoItemsPanelTemplate : _standardItemsPanelTemplate;
            _isPhotoItemsPanelActive = isPhotoMode;
            FileList.InvalidateMeasure();
            FileList.InvalidateArrange();
            ContentContainer?.InvalidateMeasure();
            ContentContainer?.InvalidateArrange();
            SelectionHost?.InvalidateMeasure();
            SelectionHost?.InvalidateArrange();

            // Swapping between the photo canvas and the regular WrapPanel happens
            // asynchronously in WPF. Queue one more layout pass so the new host exists
            // before width/placement math runs; otherwise stale photo measurements can
            // survive until some unrelated later refresh.
            if (IsLoaded)
            {
                QueueWrapPanelWidthUpdate();
            }
        }

        private System.Windows.Controls.Canvas? GetPhotoItemsCanvas()
        {
            return FileList == null
                ? null
                : FindVisualChild<System.Windows.Controls.Canvas>(FileList);
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

        private bool IsMetadataColumnSorted(string metadataKey)
        {
            return _detailsSortActive &&
                   _detailsSortColumn == MapMetadataToSortColumn(metadataKey);
        }

        private static Geometry GetMetadataSortChevronGeometry(bool ascending)
        {
            return Geometry.Parse(ascending
                ? "M0,5 L4,0 L8,5 Z"
                : "M0,0 L4,5 L8,0 Z");
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
            if (sender is not FrameworkElement header || header.Tag is not string metadataKey)
            {
                return;
            }

            DetailsSortColumn clickedColumn = MapMetadataToSortColumn(metadataKey);
            if (!_detailsSortActive)
            {
                _detailsSortActive = true;
                _detailsSortColumn = clickedColumn;
                _detailsSortAscending = true;
            }
            else if (_detailsSortColumn != clickedColumn)
            {
                _detailsSortColumn = clickedColumn;
                _detailsSortAscending = true;
            }
            else if (_detailsSortAscending)
            {
                _detailsSortAscending = false;
            }
            else
            {
                _detailsSortActive = false;
                _detailsSortColumn = DetailsSortColumn.Name;
                _detailsSortAscending = true;
                RestoreDefaultDetailsOrderInPlace();

                bool hadActiveSearchRequest = _searchCts != null &&
                    PanelType == PanelKind.Folder &&
                    !string.IsNullOrWhiteSpace(SearchBox?.Text);
                if (hadActiveSearchRequest)
                {
                    _deferSortUntilSearchComplete = false;
                }

                RebuildListItemVisuals(sortItems: false);
                e.Handled = true;
                return;
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

        private FrameworkElement CreateMetadataHeaderElement(
            string metadataKey,
            double fontSize,
            Brush labelBrush,
            Brush accentBrush)
        {
            bool isSorted = IsMetadataColumnSorted(metadataKey);

            var container = new Border
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = metadataKey
            };

            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            headerRow.Children.Add(new TextBlock
            {
                Text = GetMetadataColumnLabelText(metadataKey),
                Foreground = isSorted ? accentBrush : labelBrush,
                FontSize = fontSize,
                FontWeight = isSorted ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (isSorted)
            {
                headerRow.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = GetMetadataSortChevronGeometry(_detailsSortAscending),
                    Width = 7,
                    Height = 4,
                    Margin = new Thickness(6, 1, 0, 0),
                    Stretch = Stretch.Fill,
                    Fill = accentBrush,
                    SnapsToDevicePixels = true,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            container.Child = headerRow;
            container.PreviewMouseLeftButtonDown += MetadataHeader_PreviewMouseLeftButtonDown;
            container.MouseLeftButtonUp += MetadataHeader_MouseLeftButtonUp;
            return container;
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
            backItem.Visibility = ShouldShowParentNavigationListItem()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SortCurrentFolderItemsInPlace()
        {
            if (FileList == null || PanelType != PanelKind.Folder || !_detailsSortActive)
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
            var desiredOrder = new List<ListBoxItem>();
            if (backItem != null)
            {
                desiredOrder.Add(backItem);
            }

            desiredOrder.AddRange(sortable);
            ApplyFileListOrderInPlace(desiredOrder, selectedPaths);
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
                case DetailsSortColumn.Authors:
                    comparison = string.Compare(
                        GetComparableDetailsText(MetadataAuthors, leftPath, leftIsFolder),
                        GetComparableDetailsText(MetadataAuthors, rightPath, rightIsFolder),
                        StringComparison.CurrentCultureIgnoreCase);
                    break;
                case DetailsSortColumn.Categories:
                    comparison = string.Compare(
                        GetComparableDetailsText(MetadataCategories, leftPath, leftIsFolder),
                        GetComparableDetailsText(MetadataCategories, rightPath, rightIsFolder),
                        StringComparison.CurrentCultureIgnoreCase);
                    break;
                case DetailsSortColumn.Tags:
                    comparison = string.Compare(
                        GetComparableDetailsText(MetadataTags, leftPath, leftIsFolder),
                        GetComparableDetailsText(MetadataTags, rightPath, rightIsFolder),
                        StringComparison.CurrentCultureIgnoreCase);
                    break;
                case DetailsSortColumn.Title:
                    comparison = string.Compare(
                        GetComparableDetailsText(MetadataTitle, leftPath, leftIsFolder),
                        GetComparableDetailsText(MetadataTitle, rightPath, rightIsFolder),
                        StringComparison.CurrentCultureIgnoreCase);
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

        private void RestoreDefaultDetailsOrderInPlace()
        {
            if (FileList == null || PanelType != PanelKind.Folder || _detailsDefaultOrderPaths.Count == 0)
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
            var currentOrderLookup = allItems
                .Where(i => !ReferenceEquals(i, backItem) && i.Tag is string)
                .Select((item, index) => new { item, index })
                .ToDictionary(entry => entry.item.Tag as string ?? string.Empty, entry => entry.index, StringComparer.OrdinalIgnoreCase);
            var defaultOrderLookup = _detailsDefaultOrderPaths
                .Select((path, index) => new { path, index })
                .ToDictionary(entry => entry.path, entry => entry.index, StringComparer.OrdinalIgnoreCase);

            var sortable = allItems
                .Where(i => !ReferenceEquals(i, backItem))
                .ToList();

            sortable.Sort((left, right) =>
            {
                string leftPath = left.Tag as string ?? string.Empty;
                string rightPath = right.Tag as string ?? string.Empty;

                bool hasLeftDefaultOrder = defaultOrderLookup.TryGetValue(leftPath, out int leftDefaultOrder);
                bool hasRightDefaultOrder = defaultOrderLookup.TryGetValue(rightPath, out int rightDefaultOrder);
                if (hasLeftDefaultOrder || hasRightDefaultOrder)
                {
                    if (hasLeftDefaultOrder && hasRightDefaultOrder)
                    {
                        int defaultComparison = leftDefaultOrder.CompareTo(rightDefaultOrder);
                        if (defaultComparison != 0)
                        {
                            return defaultComparison;
                        }
                    }
                    else
                    {
                        return hasLeftDefaultOrder ? -1 : 1;
                    }
                }

                currentOrderLookup.TryGetValue(leftPath, out int leftCurrentOrder);
                currentOrderLookup.TryGetValue(rightPath, out int rightCurrentOrder);
                return leftCurrentOrder.CompareTo(rightCurrentOrder);
            });
            var desiredOrder = new List<ListBoxItem>();
            if (backItem != null)
            {
                desiredOrder.Add(backItem);
            }

            desiredOrder.AddRange(sortable);
            ApplyFileListOrderInPlace(desiredOrder, selectedPaths);
        }

        private void ApplyFileListOrderInPlace(
            IReadOnlyList<ListBoxItem> desiredOrder,
            ISet<string> selectedPaths)
        {
            if (FileList == null || desiredOrder.Count == 0)
            {
                return;
            }

            for (int targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
            {
                ListBoxItem desiredItem = desiredOrder[targetIndex];
                if (targetIndex < FileList.Items.Count &&
                    ReferenceEquals(FileList.Items[targetIndex], desiredItem))
                {
                    continue;
                }

                int currentIndex = FileList.Items.IndexOf(desiredItem);
                if (currentIndex < 0)
                {
                    continue;
                }

                FileList.Items.RemoveAt(currentIndex);
                FileList.Items.Insert(targetIndex, desiredItem);
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

            var actualWidths = GetActualDetailsColumnWidths(rowWidth);
            var visibleColumns = GetVisibleDetailsColumns();
            var panel = new Grid
            {
                Width = rowWidth,
                Margin = new Thickness(4, 2, 4, 2),
                Opacity = baseOpacity
            };

            int columnIndex = 0;
            foreach (string metadataKey in visibleColumns)
            {
                double columnWidth = actualWidths.TryGetValue(metadataKey, out double resolvedWidth)
                    ? resolvedWidth
                    : GetStoredDetailsColumnWidth(metadataKey);
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });

                FrameworkElement cellElement;
                if (string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase))
                {
                    cellElement = CreateDetailsNameCell(
                        displayName,
                        path,
                        isFolder,
                        iconSize,
                        textSize,
                        nameBrush);
                }
                else
                {
                    cellElement = new TextBlock
                    {
                        Text = GetDetailsColumnValueText(metadataKey, displayName, path, isFolder, isBackButton),
                        Foreground = metadataBrush,
                        FontSize = metaSize,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                }

                Grid.SetColumn(cellElement, columnIndex++);
                panel.Children.Add(cellElement);
            }

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
                        ?? LoadExplorerStyleIcon(path, isFolder, 256))
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

            var thumbnailHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true,
                SnapsToDevicePixels = true
            };
            thumbnailHost.Children.Add(thumbnailFrame);
            if (isPhotoFile)
            {
                thumbnailHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(0),
                    Style = PhotoTileOverlayStyle
                });
            }

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

            panel.Children.Add(thumbnailHost);
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
            bool metadataAuthors = false,
            bool metadataCategories = false,
            bool metadataTags = false,
            bool metadataTitle = false,
            IEnumerable<string>? metadataOrderOverride = null,
            IDictionary<string, double>? metadataWidthsOverride = null,
            bool persistSettings = true)
        {
            string normalized = NormalizeViewMode(requestedViewMode);
            bool viewModeChanged = !string.Equals(viewMode, normalized, StringComparison.OrdinalIgnoreCase);
            var normalizedOrder = NormalizeMetadataOrder(metadataOrderOverride ?? metadataOrder);
            var normalizedWidths = NormalizeMetadataWidths(metadataWidthsOverride ?? metadataWidths);
            bool changed =
                viewModeChanged ||
                showMetadataType != metadataType ||
                showMetadataSize != metadataSize ||
                showMetadataCreated != metadataCreated ||
                showMetadataModified != metadataModified ||
                showMetadataDimensions != metadataDimensions ||
                showMetadataAuthors != metadataAuthors ||
                showMetadataCategories != metadataCategories ||
                showMetadataTags != metadataTags ||
                showMetadataTitle != metadataTitle ||
                !metadataOrder.SequenceEqual(normalizedOrder, StringComparer.OrdinalIgnoreCase) ||
                !NormalizeMetadataWidths(metadataWidths)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(normalizedWidths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));

            viewMode = normalized;
            showMetadataType = metadataType;
            showMetadataSize = metadataSize;
            showMetadataCreated = metadataCreated;
            showMetadataModified = metadataModified;
            showMetadataDimensions = metadataDimensions;
            showMetadataAuthors = metadataAuthors;
            showMetadataCategories = metadataCategories;
            showMetadataTags = metadataTags;
            showMetadataTitle = metadataTitle;
            metadataOrder = normalizedOrder;
            metadataWidths = normalizedWidths;

            if (!changed)
            {
                return;
            }

            if (viewModeChanged)
            {
                RecreateFileListContainersForCurrentView();
            }
            else
            {
                RebuildListItemVisuals();
            }

            if (persistSettings)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
        }

        private void RecreateFileListContainersForCurrentView()
        {
            if (FileList == null)
            {
                return;
            }

            var existingItems = FileList.Items
                .OfType<ListBoxItem>()
                .ToList();
            if (existingItems.Count == 0)
            {
                RefreshDetailsHeader();
                UpdateWrapPanelWidth();
                UpdateDropZoneVisibility();
                return;
            }

            var selectedPaths = new HashSet<string>(
                FileList.SelectedItems
                    .OfType<ListBoxItem>()
                    .Select(item => item.Tag as string)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);
            var injectedPaths = new HashSet<string>(
                _searchInjectedItems
                    .Select(item => item.Tag as string)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            var rebuiltItems = new List<ListBoxItem>(existingItems.Count);
            var rebuiltInjectedItems = new List<ListBoxItem>();

            foreach (ListBoxItem item in existingItems)
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

                ListBoxItem rebuiltItem = CreateFileListBoxItem(
                    displayName,
                    path,
                    isBackButton,
                    _currentAppearance);
                rebuiltItem.Visibility = isBackButton
                    ? (ShouldShowParentNavigationListItem() ? Visibility.Visible : Visibility.Collapsed)
                    : item.Visibility;
                rebuiltItem.IsHitTestVisible = item.IsHitTestVisible;
                rebuiltItem.Opacity = item.Opacity;

                rebuiltItems.Add(rebuiltItem);
                if (!isBackButton && selectedPaths.Contains(path))
                {
                    rebuiltItem.IsSelected = true;
                }

                if (injectedPaths.Contains(path))
                {
                    rebuiltInjectedItems.Add(rebuiltItem);
                }
            }

            FileList.Items.Clear();
            foreach (ListBoxItem rebuiltItem in rebuiltItems)
            {
                FileList.Items.Add(rebuiltItem);
            }

            _searchInjectedItems.Clear();
            _searchInjectedItems.AddRange(rebuiltInjectedItems);

            RefreshDetailsHeader();
            UpdateWrapPanelWidth();
            UpdateDropZoneVisibility();
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
                if (isBackButton)
                {
                    item.Visibility = ShouldShowParentNavigationListItem()
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            if (sortItems)
            {
                SortCurrentFolderItemsInPlace();
            }
            RefreshDetailsHeader();
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
            QueueWrapPanelWidthUpdate();
        }

        public bool FitWidthToContent()
        {
            return FitToContentInternal(adjustWidth: true, adjustHeight: false);
        }

        public bool FitHeightToContent()
        {
            return FitToContentInternal(adjustWidth: false, adjustHeight: true);
        }

        public void FitToContent()
        {
            FitToContentInternal(adjustWidth: true, adjustHeight: true);
        }

        private bool FitToContentInternal(bool adjustWidth, bool adjustHeight)
        {
            if (ContentContainer == null || FileList == null || (!adjustWidth && !adjustHeight))
            {
                return false;
            }

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

                bool isPhotoMode = string.Equals(
                    NormalizeViewMode(viewMode),
                    ViewModePhotos,
                    StringComparison.OrdinalIgnoreCase);
                var wrapPanel = !isPhotoMode ? FindVisualChild<WrapPanel>(FileList) : null;
                var photoCanvas = isPhotoMode ? GetPhotoItemsCanvas() : null;
                if ((!isPhotoMode && wrapPanel == null) ||
                    (isPhotoMode && photoCanvas == null))
                {
                    FinalizeFitToContentChange(changed: false);
                    return false;
                }

                Rect workArea = GetWorkAreaForPanel();
                bool widthChanged = adjustWidth && TryAdjustWidthToContent(isPhotoMode, wrapPanel, workArea);
                bool heightChanged = adjustHeight && TryAdjustHeightToContent(isPhotoMode, wrapPanel, photoCanvas, workArea);
                bool anyChanged = widthChanged || heightChanged;
                FinalizeFitToContentChange(anyChanged);
                return anyChanged;
            }
            finally
            {
                SetContentScrollbarsFrozen(false);
            }
        }

        private void FinalizeFitToContentChange(bool changed)
        {
            if (!changed)
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

        private bool TryAdjustWidthToContent(bool isPhotoMode, WrapPanel? wrapPanel, Rect workArea)
        {
            if (isPhotoMode || FileList == null || wrapPanel == null)
            {
                return false;
            }

            double startWidth = ActualWidth > 0 ? ActualWidth : Width;
            int baselineFirstRowCount = GetFirstRowItemCount(FileList, wrapPanel);
            double minWindowWidth = Math.Max(220, MinWidth > 0 ? MinWidth : 0);
            double maxWindowWidth = Math.Max(minWindowWidth, workArea.Right - Left);
            double currentLayoutWidth = GetContentLayoutWidth();
            if (currentLayoutWidth <= 1)
            {
                return false;
            }

            double neededWrapWidth = GetVisibleItemRightEdge(FileList, wrapPanel);
            double firstRowRightEdge = GetFirstRowRightEdge(FileList, wrapPanel);
            double stableNeededWrapWidth = Math.Max(neededWrapWidth, firstRowRightEdge);
            if (stableNeededWrapWidth <= 0)
            {
                return false;
            }

            double wrapHorizontalMargin = GetHorizontalMargin(wrapPanel);
            double layoutSafetyPixels = Math.Ceiling(2 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            double neededContentWidth = Math.Ceiling(stableNeededWrapWidth + wrapHorizontalMargin + layoutSafetyPixels);
            double currentWindowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double chrome = currentWindowWidth - currentLayoutWidth;
            double targetWidth = chrome + neededContentWidth;
            targetWidth = Math.Max(minWindowWidth, Math.Min(targetWidth, maxWindowWidth));

            if (Math.Abs(targetWidth - currentWindowWidth) <= 0.5)
            {
                return false;
            }

            ApplyWindowWidthAndRefreshLayout(targetWidth);

            // Guard against wrap-threshold drift and late viewport updates:
            // never reduce items in the first row when fitting width.
            if (baselineFirstRowCount > 0 && Width < startWidth - 0.5)
            {
                int fittedFirstRowCount = GetFirstRowItemCount(FileList, wrapPanel);
                if (fittedFirstRowCount < baselineFirstRowCount)
                {
                    double correctedWidth = FindSmallestWidthPreservingFirstRowCount(
                        wrapPanel,
                        baselineFirstRowCount,
                        targetWidth,
                        Math.Max(minWindowWidth, Math.Min(startWidth, maxWindowWidth)));
                    if (Math.Abs(correctedWidth - Width) > 0.5)
                    {
                        ApplyWindowWidthAndRefreshLayout(correctedWidth);
                    }
                }
            }

            return Math.Abs(Width - startWidth) > 0.5;
        }

        private void ApplyWindowWidthAndRefreshLayout(double targetWidth)
        {
            Width = targetWidth;
            QueueWrapPanelWidthUpdate();
            Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
            UpdateLayout();
        }

        private double FindSmallestWidthPreservingFirstRowCount(
            WrapPanel wrapPanel,
            int baselineFirstRowCount,
            double minCandidateWidth,
            double maxCandidateWidth)
        {
            double low = Math.Min(minCandidateWidth, maxCandidateWidth);
            double high = Math.Max(minCandidateWidth, maxCandidateWidth);
            double best = high;

            for (int iteration = 0; iteration < 9 && high - low > 0.5; iteration++)
            {
                double candidate = Math.Ceiling((low + high) / 2.0);
                ApplyWindowWidthAndRefreshLayout(candidate);

                int candidateFirstRowCount = GetFirstRowItemCount(FileList!, wrapPanel);
                if (candidateFirstRowCount >= baselineFirstRowCount)
                {
                    best = candidate;
                    high = candidate;
                }
                else
                {
                    low = candidate;
                }
            }

            return best;
        }

        private bool TryAdjustHeightToContent(
            bool isPhotoMode,
            WrapPanel? wrapPanel,
            System.Windows.Controls.Canvas? photoCanvas,
            Rect workArea)
        {
            if (ContentContainer == null)
            {
                return false;
            }

            double finalHostWidth = isPhotoMode
                ? (photoCanvas != null && photoCanvas.Width > 0 ? photoCanvas.Width : GetContentLayoutWidth())
                : (wrapPanel != null && wrapPanel.Width > 0 ? wrapPanel.Width : GetContentLayoutWidth());
            if (finalHostWidth <= 1)
            {
                return false;
            }

            double desiredContentHeight;
            if (isPhotoMode)
            {
                if (photoCanvas == null)
                {
                    return false;
                }

                photoCanvas.Width = finalHostWidth;
                photoCanvas.Measure(new System.Windows.Size(finalHostWidth, double.PositiveInfinity));
                desiredContentHeight = photoCanvas.Height > 0
                    ? photoCanvas.Height
                    : photoCanvas.DesiredSize.Height;
            }
            else
            {
                if (wrapPanel == null)
                {
                    return false;
                }

                wrapPanel.Measure(new System.Windows.Size(finalHostWidth, double.PositiveInfinity));
                desiredContentHeight = wrapPanel.DesiredSize.Height;
            }

            double viewportHeight = ContentContainer.ViewportHeight > 0
                ? ContentContainer.ViewportHeight
                : ContentContainer.ActualHeight;
            if (viewportHeight <= 1)
            {
                return false;
            }

            double startTop = Top;
            double startHeight = Height;
            double verticalChrome = Height - viewportHeight;
            double targetHeight = verticalChrome + desiredContentHeight + 6;
            double collapsedHeight = GetCollapsedHeight();
            double minWindowHeight = Math.Max(collapsedHeight, MinHeight > 0 ? MinHeight : 0);
            double maxWindowHeightInWorkArea = Math.Max(minWindowHeight, workArea.Height);
            targetHeight = Math.Max(minWindowHeight, Math.Min(targetHeight, maxWindowHeightInWorkArea));

            double targetTop = Top;
            double maxHeightAtCurrentTop = Math.Max(minWindowHeight, workArea.Bottom - targetTop);
            if (targetHeight > maxHeightAtCurrentTop + 0.5)
            {
                double shiftedTop = workArea.Bottom - targetHeight;
                targetTop = ClampTopToWorkArea(workArea, targetHeight, shiftedTop);

                double maxHeightAtShiftedTop = Math.Max(minWindowHeight, workArea.Bottom - targetTop);
                targetHeight = Math.Max(minWindowHeight, Math.Min(targetHeight, maxHeightAtShiftedTop));
            }
            else
            {
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);
            }

            SnapWindowVerticalBounds(ref targetTop, ref targetHeight);

            if (Math.Abs(targetTop - Top) > 0.5)
            {
                Top = targetTop;
            }

            if (Math.Abs(targetHeight - Height) > 0.5)
            {
                Height = targetHeight;
                expandedHeight = Math.Max(collapsedHeight, targetHeight);
            }

            return Math.Abs(Top - startTop) > 0.5 || Math.Abs(Height - startHeight) > 0.5;
        }

        private void UpdateWrapPanelWidth()
        {
            if (ContentContainer == null || FileList == null) return;
            bool isPhotoMode = string.Equals(
                NormalizeViewMode(viewMode),
                ViewModePhotos,
                StringComparison.OrdinalIgnoreCase);
            EnsureItemsHostPanel(isPhotoMode);
            ApplyPhotoModeItemCompaction(isPhotoMode);
            FileList.UseLayoutRounding = !isPhotoMode;
            ContentContainer.UseLayoutRounding = !isPhotoMode;

            if (isPhotoMode)
            {
                var photoCanvas = GetPhotoItemsCanvas();
                if (photoCanvas == null)
                {
                    FileList.UpdateLayout();
                    photoCanvas = GetPhotoItemsCanvas();
                }
                if (photoCanvas != null)
                {
                    double vw = ContentContainer.ViewportWidth;
                    double baseWidth = GetContentLayoutWidth();
                    double availableWidth = (!double.IsNaN(vw) && vw > 1) ? vw : baseWidth;
                    photoCanvas.Margin = new Thickness(0);
                    photoCanvas.UseLayoutRounding = false;
                    photoCanvas.SnapsToDevicePixels = false;
                    if (availableWidth > 0 &&
                        Math.Abs(photoCanvas.Width - availableWidth) > 0.5)
                    {
                        photoCanvas.Width = availableWidth;
                        photoCanvas.InvalidateMeasure();
                        photoCanvas.InvalidateArrange();
                        FileList.InvalidateMeasure();
                    }
                }
            }
            else
            {
                FileList.ClearValue(FrameworkElement.WidthProperty);
                FileList.ClearValue(FrameworkElement.HeightProperty);

                var wrapPanel = FindVisualChild<WrapPanel>(FileList);
                if (wrapPanel != null)
                {
                    var targetMargin = new Thickness(4);
                    if (!wrapPanel.Margin.Equals(targetMargin))
                    {
                        wrapPanel.Margin = targetMargin;
                        wrapPanel.InvalidateMeasure();
                        wrapPanel.InvalidateArrange();
                    }
                    wrapPanel.UseLayoutRounding = true;
                    wrapPanel.SnapsToDevicePixels = true;

                    double baseWidth = GetContentLayoutWidth();
                    double availableWidth = Math.Max(0, baseWidth - GetHorizontalMargin(wrapPanel));
                    if (availableWidth > 0 &&
                        Math.Abs(wrapPanel.Width - availableWidth) > 0.5)
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

        private bool CurrentViewNeedsContentLayoutRefresh()
        {
            string normalizedViewMode = NormalizeViewMode(viewMode);
            return string.Equals(normalizedViewMode, ViewModePhotos, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedViewMode, ViewModeDetails, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyPhotoModeItemCompaction(bool isPhotoMode)
        {
            if (FileList == null)
            {
                return;
            }

            foreach (ListBoxItem item in FileList.Items.OfType<ListBoxItem>())
            {
                bool matchesSearch = ShouldItemMatchCurrentSearchFilter(item);

                if (item.Content is not FrameworkElement root ||
                    root.Tag is not PhotoTileLayoutInfo layout)
                {
                    bool shouldShow = matchesSearch;
                    if (isPhotoMode &&
                        item.Tag is string pathTag &&
                        !IsPhotoPath(pathTag))
                    {
                        shouldShow = false;
                    }

                    item.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                    if (!isPhotoMode)
                    {
                        ClearPhotoModeContainerLayout(item);
                        item.Opacity = shouldShow ? 1 : 0;
                    }
                    item.IsHitTestVisible = shouldShow;
                    continue;
                }

                if (isPhotoMode)
                {
                    bool shouldShow = layout.IsPhotoFile && matchesSearch;
                    item.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                    item.IsHitTestVisible = shouldShow;
                    item.Margin = new Thickness(0);
                    item.Padding = new Thickness(0);
                    item.BorderThickness = new Thickness(0);

                    if (!shouldShow)
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

                item.Visibility = matchesSearch ? Visibility.Visible : Visibility.Collapsed;
                ClearPhotoModeContainerLayout(item);
                item.Opacity = matchesSearch ? 1 : 0;
                item.IsHitTestVisible = matchesSearch;
            }
        }

        private static void ClearPhotoModeContainerLayout(ListBoxItem item)
        {
            item.ClearValue(FrameworkElement.WidthProperty);
            item.ClearValue(FrameworkElement.HeightProperty);
            item.ClearValue(FrameworkElement.MinWidthProperty);
            item.ClearValue(FrameworkElement.MinHeightProperty);
            item.ClearValue(FrameworkElement.MaxWidthProperty);
            item.ClearValue(FrameworkElement.MaxHeightProperty);
            item.ClearValue(FrameworkElement.MarginProperty);
            item.ClearValue(System.Windows.Controls.Canvas.LeftProperty);
            item.ClearValue(System.Windows.Controls.Canvas.TopProperty);
            item.ClearValue(System.Windows.Controls.Control.PaddingProperty);
            item.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        }

        private bool ShouldItemMatchCurrentSearchFilter(ListBoxItem item)
        {
            string filter = SearchBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return IsParentNavigationItem(item) ||
                GetSearchCandidateText(item).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdatePhotoTileLayouts()
        {
            if (FileList == null ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var photoCanvas = GetPhotoItemsCanvas();
            if (photoCanvas == null)
            {
                FileList.UpdateLayout();
                photoCanvas = GetPhotoItemsCanvas();
                if (photoCanvas == null)
                {
                    return;
                }
            }

            double contentWidth = GetContentLayoutWidth();
            if (contentWidth <= 1)
            {
                return;
            }

            // Use the viewport width (excludes scrollbar) as the ground truth for
            // available space.  Fall back to ContentContainer.ActualWidth when the
            // viewport hasn't been measured yet.
            double viewportWidth = ContentContainer.ViewportWidth;
            if (double.IsNaN(viewportWidth) || viewportWidth <= 1)
            {
                viewportWidth = contentWidth;
            }
            double availableWidth = Math.Max(120, viewportWidth);
            double targetRowHeight = Math.Clamp(220 * zoomFactor, 140, 340);
            var photos = new List<PhotoLayoutEntry>();
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
                photos.Add(new PhotoLayoutEntry
                {
                    Container = item,
                    Root = root,
                    Layout = layout,
                    Aspect = aspect
                });
            }
            if (photos.Count == 0)
            {
                photoCanvas.Width = availableWidth;
                photoCanvas.Height = 0;
                FileList.Width = availableWidth;
                FileList.Height = 0;
                return;
            }

            var rows = BuildPhotoRows(photos, availableWidth, targetRowHeight);
            double top = 0;

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row.Columns.Count == 0 || row.Height <= 0.5)
                {
                    continue;
                }

                var columns = rowIndex % 2 == 0
                    ? row.Columns
                    : row.Columns.AsEnumerable().Reverse().ToList();
                double left = 0;
                for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var column = columns[columnIndex];
                    double inverseAspectSum = Math.Max(
                        0.0001,
                        column.Entries.Sum(entry => 1.0 / entry.Aspect));
                    double remainingRowWidth = Math.Max(1, availableWidth - left);
                    double columnWidth = columnIndex == columns.Count - 1
                        ? remainingRowWidth
                        : Math.Max(48, row.Height / inverseAspectSum);
                    double columnTop = top;
                    for (int entryIndex = 0; entryIndex < column.Entries.Count; entryIndex++)
                    {
                        var entry = column.Entries[entryIndex];
                        double remainingColumnHeight = Math.Max(1, row.Height - (columnTop - top));
                        double tileHeight = entryIndex == column.Entries.Count - 1
                            ? remainingColumnHeight
                            : Math.Max(48, columnWidth / entry.Aspect);
                        ApplyPhotoTileSize(
                            entry.Container,
                            entry.Root,
                            entry.Layout,
                            columnWidth,
                            tileHeight,
                            leftOffset: 0);
                        System.Windows.Controls.Canvas.SetLeft(entry.Container, left);
                        System.Windows.Controls.Canvas.SetTop(entry.Container, columnTop);
                        ReloadPhotoIfNeeded(entry.Layout, columnWidth, tileHeight);
                        columnTop += tileHeight;
                    }

                    left += columnWidth;
                }

                top += row.Height;
            }

            photoCanvas.Width = availableWidth;
            photoCanvas.Height = Math.Max(0, top);
            FileList.Width = availableWidth;
            FileList.Height = Math.Max(0, top);
        }

        private List<PhotoMosaicRow> BuildPhotoRows(
            List<PhotoLayoutEntry> photos,
            double availableWidth,
            double targetRowHeight)
        {
            var rows = new List<PhotoMosaicRow>();
            if (photos.Count == 0)
            {
                return rows;
            }

            var remaining = new List<PhotoLayoutEntry>(photos);
            while (remaining.Count > 0)
            {
                var candidate = SelectBestPhotoMosaicCandidate(remaining, availableWidth, targetRowHeight);
                if (candidate == null)
                {
                    var fallback = CreateFallbackPhotoMosaicRow(
                        remaining.Take(Math.Min(3, remaining.Count)).ToList(),
                        availableWidth);
                    rows.Add(fallback);
                    foreach (var entry in fallback.Columns.SelectMany(column => column.Entries).ToList())
                    {
                        remaining.Remove(entry);
                    }
                    continue;
                }

                var row = new PhotoMosaicRow { Height = candidate.Height };
                foreach (var column in candidate.Columns)
                {
                    row.Columns.Add(column);
                }
                rows.Add(row);
                foreach (var entry in candidate.SelectedEntries)
                {
                    remaining.Remove(entry);
                }
            }

            return rows;
        }

        private PhotoMosaicCandidate? SelectBestPhotoMosaicCandidate(
            List<PhotoLayoutEntry> remaining,
            double availableWidth,
            double targetRowHeight)
        {
            if (remaining.Count == 0)
            {
                return null;
            }

            bool isLastRow = remaining.Count <= 4;
            var orderedByAspect = remaining
                .OrderBy(entry => entry.Aspect)
                .ToList();
            var candidatePool = new List<PhotoLayoutEntry>();
            AddPhotoCandidatePoolEntries(candidatePool, orderedByAspect.Take(3));
            AddPhotoCandidatePoolEntries(candidatePool, orderedByAspect.Skip(Math.Max(0, orderedByAspect.Count - 3)));
            AddPhotoCandidatePoolEntries(
                candidatePool,
                orderedByAspect
                    .OrderBy(entry => Math.Abs(entry.Aspect - 1.25))
                    .Take(2));
            if (candidatePool.Count == 0)
            {
                return null;
            }

            PhotoMosaicCandidate? bestCandidate = null;
            for (int i = 0; i < candidatePool.Count; i++)
            {
                var first = candidatePool[i];
                if (remaining.Count == 1 || first.Aspect >= 2.15)
                {
                    TryConsiderPhotoMosaicCandidate(
                        ref bestCandidate,
                        availableWidth,
                        targetRowHeight,
                        isLastRow,
                        CreatePhotoMosaicColumn(first));
                }

                for (int j = i + 1; j < candidatePool.Count; j++)
                {
                    var second = candidatePool[j];
                    TryConsiderPhotoMosaicCandidate(
                        ref bestCandidate,
                        availableWidth,
                        targetRowHeight,
                        isLastRow,
                        CreatePhotoMosaicColumn(first),
                        CreatePhotoMosaicColumn(second));

                    for (int k = j + 1; k < candidatePool.Count; k++)
                    {
                        var third = candidatePool[k];
                        TryConsiderPhotoMosaicCandidate(
                            ref bestCandidate,
                            availableWidth,
                            targetRowHeight,
                            isLastRow,
                            CreatePhotoMosaicColumn(first),
                            CreatePhotoMosaicColumn(second),
                            CreatePhotoMosaicColumn(third));
                        TryConsiderPhotoMosaicCandidate(
                            ref bestCandidate,
                            availableWidth,
                            targetRowHeight,
                            isLastRow,
                            CreatePhotoMosaicColumn(first),
                            CreatePhotoMosaicColumn(second, third));
                        TryConsiderPhotoMosaicCandidate(
                            ref bestCandidate,
                            availableWidth,
                            targetRowHeight,
                            isLastRow,
                            CreatePhotoMosaicColumn(first, second),
                            CreatePhotoMosaicColumn(third));

                        for (int l = k + 1; l < candidatePool.Count; l++)
                        {
                            var fourth = candidatePool[l];
                            TryConsiderPhotoMosaicCandidate(
                                ref bestCandidate,
                                availableWidth,
                                targetRowHeight,
                                isLastRow,
                                CreatePhotoMosaicColumn(first),
                                CreatePhotoMosaicColumn(second),
                                CreatePhotoMosaicColumn(third, fourth));
                            TryConsiderPhotoMosaicCandidate(
                                ref bestCandidate,
                                availableWidth,
                                targetRowHeight,
                                isLastRow,
                                CreatePhotoMosaicColumn(first),
                                CreatePhotoMosaicColumn(second, third),
                                CreatePhotoMosaicColumn(fourth));
                            TryConsiderPhotoMosaicCandidate(
                                ref bestCandidate,
                                availableWidth,
                                targetRowHeight,
                                isLastRow,
                                CreatePhotoMosaicColumn(first, second),
                                CreatePhotoMosaicColumn(third),
                                CreatePhotoMosaicColumn(fourth));
                            TryConsiderPhotoMosaicCandidate(
                                ref bestCandidate,
                                availableWidth,
                                targetRowHeight,
                                isLastRow,
                                CreatePhotoMosaicColumn(first, second),
                                CreatePhotoMosaicColumn(third, fourth));
                        }
                    }
                }
            }

            if (bestCandidate == null)
            {
                var fallbackEntries = remaining
                    .OrderBy(entry => Math.Abs(entry.Aspect - 1.25))
                    .Take(Math.Min(3, remaining.Count))
                    .ToArray();
                if (fallbackEntries.Length > 0)
                {
                    TryConsiderPhotoMosaicCandidate(
                        ref bestCandidate,
                        availableWidth,
                        targetRowHeight,
                        isLastRow: true,
                        fallbackEntries.Select(entry => CreatePhotoMosaicColumn(entry)).ToArray());
                }
            }

            return bestCandidate;
        }

        private static void AddPhotoCandidatePoolEntries(
            List<PhotoLayoutEntry> target,
            IEnumerable<PhotoLayoutEntry> source)
        {
            foreach (var entry in source)
            {
                if (!target.Contains(entry))
                {
                    target.Add(entry);
                }
            }
        }

        private void TryConsiderPhotoMosaicCandidate(
            ref PhotoMosaicCandidate? bestCandidate,
            double availableWidth,
            double targetRowHeight,
            bool isLastRow,
            params PhotoMosaicColumn[] columns)
        {
            if (!TryCreatePhotoMosaicCandidate(
                    columns,
                    availableWidth,
                    targetRowHeight,
                    isLastRow,
                    out var candidate))
            {
                return;
            }

            if (bestCandidate == null || candidate.Score < bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        private bool TryCreatePhotoMosaicCandidate(
            IReadOnlyList<PhotoMosaicColumn> columns,
            double availableWidth,
            double targetRowHeight,
            bool isLastRow,
            out PhotoMosaicCandidate candidate)
        {
            candidate = null!;
            if (columns.Count == 0)
            {
                return false;
            }

            var selectedEntries = new HashSet<PhotoLayoutEntry>();
            double inverseWidthFactorSum = 0;
            var columnFactors = new double[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (column.Entries.Count == 0)
                {
                    return false;
                }

                double inverseAspectSum = 0;
                foreach (var entry in column.Entries)
                {
                    if (!selectedEntries.Add(entry))
                    {
                        return false;
                    }

                    inverseAspectSum += 1.0 / Math.Max(0.08, entry.Aspect);
                }

                if (inverseAspectSum <= 0.0001)
                {
                    return false;
                }

                columnFactors[i] = inverseAspectSum;
                inverseWidthFactorSum += 1.0 / inverseAspectSum;
            }
            if (inverseWidthFactorSum <= 0.0001)
            {
                return false;
            }

            double rowHeight = availableWidth / inverseWidthFactorSum;
            if (double.IsNaN(rowHeight) || double.IsInfinity(rowHeight) || rowHeight < 52)
            {
                return false;
            }

            double minRowHeight = Math.Max(110, targetRowHeight * 0.6);
            double maxRowHeight = Math.Max(320, targetRowHeight * 2.1);
            double minColumnWidth = Math.Max(80, availableWidth * 0.14);
            double minTileHeight = Math.Max(72, targetRowHeight * 0.42);
            double maxTileHeight = Math.Max(240, targetRowHeight * 1.7);
            double score = Math.Abs(rowHeight - targetRowHeight) / Math.Max(1, targetRowHeight);
            if (rowHeight < minRowHeight)
            {
                score += ((minRowHeight - rowHeight) / minRowHeight) * 2.4;
            }
            if (rowHeight > maxRowHeight)
            {
                score += ((rowHeight - maxRowHeight) / maxRowHeight) * 1.9;
            }

            bool hasStack = false;
            int totalEntries = 0;
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                double columnWidth = rowHeight / columnFactors[i];
                if (double.IsNaN(columnWidth) || double.IsInfinity(columnWidth) || columnWidth < 44)
                {
                    return false;
                }

                totalEntries += column.Entries.Count;
                if (column.Entries.Count > 1)
                {
                    hasStack = true;
                }

                if (columnWidth < minColumnWidth)
                {
                    score += ((minColumnWidth - columnWidth) / minColumnWidth) * 1.5;
                }

                foreach (var entry in column.Entries)
                {
                    double tileHeight = columnWidth / Math.Max(0.08, entry.Aspect);
                    if (double.IsNaN(tileHeight) || double.IsInfinity(tileHeight) || tileHeight < 44)
                    {
                        return false;
                    }

                    if (tileHeight < minTileHeight)
                    {
                        score += ((minTileHeight - tileHeight) / minTileHeight) * 1.8;
                    }
                    if (tileHeight > maxTileHeight)
                    {
                        score += ((tileHeight - maxTileHeight) / maxTileHeight) * 0.8;
                    }
                    if (entry.Aspect >= 2.2 && column.Entries.Count > 1)
                    {
                        score += 0.25;
                    }
                }
            }

            if (hasStack)
            {
                score -= 0.1;
            }

            score -= Math.Min(0.24, 0.04 * Math.Max(0, totalEntries - 1));

            if (columns.Count == 1 && columns[0].Entries.Count == 1)
            {
                double aspect = columns[0].Entries[0].Aspect;
                if (aspect >= 2.6)
                {
                    score -= 0.5;
                }
                else if (aspect >= 2.15)
                {
                    score -= 0.24;
                }
                else
                {
                    score += 1.7;
                }
            }

            if (isLastRow)
            {
                score -= 0.12;
            }

            candidate = new PhotoMosaicCandidate
            {
                Height = rowHeight,
                Score = score
            };
            foreach (var column in columns)
            {
                candidate.Columns.Add(column);
            }
            foreach (var entry in selectedEntries)
            {
                candidate.SelectedEntries.Add(entry);
            }

            return true;
        }

        private static PhotoMosaicColumn CreatePhotoMosaicColumn(params PhotoLayoutEntry[] entries)
        {
            var column = new PhotoMosaicColumn();
            foreach (var entry in entries)
            {
                column.Entries.Add(entry);
            }

            return column;
        }

        private static PhotoMosaicRow CreateFallbackPhotoMosaicRow(
            IReadOnlyList<PhotoLayoutEntry> entries,
            double availableWidth)
        {
            double aspectSum = Math.Max(0.0001, entries.Sum(entry => entry.Aspect));
            double rowHeight = Math.Max(96, availableWidth / aspectSum);
            var row = new PhotoMosaicRow { Height = rowHeight };
            foreach (var entry in entries)
            {
                row.Columns.Add(CreatePhotoMosaicColumn(entry));
            }

            return row;
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

            var targetMargin = leftOffset > 0.5
                ? new Thickness(leftOffset, 0, 0, 0)
                : new Thickness(0);
            if (!container.Margin.Equals(targetMargin))
            {
                container.Margin = targetMargin;
            }

            // Use exact values (no 0.5 threshold) to prevent cumulative rounding drift.
            container.Width = cardWidth;
            container.Height = imageHeight;
            container.Opacity = 1;
            container.IsHitTestVisible = true;

            root.Width = cardWidth;
            root.Height = imageHeight;
            root.UseLayoutRounding = false;

            layout.ThumbnailFrame.Width = previewWidth;
            layout.ThumbnailFrame.Height = previewHeight;
            layout.ThumbnailFrame.UseLayoutRounding = false;

            layout.ThumbnailImage.Width = imageWidth;
            layout.ThumbnailImage.Height = imageHeight;
            layout.ThumbnailImage.UseLayoutRounding = false;
            layout.ThumbnailImage.SnapsToDevicePixels = false;
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

            RefreshDetailsHeader();
        }

        private double GetContentLayoutWidth()
        {
            if (ContentContainer == null)
            {
                return 0;
            }

            var contentPresenter = FindVisualChild<ScrollContentPresenter>(ContentContainer);
            if (contentPresenter != null && contentPresenter.ActualWidth > 0)
            {
                return contentPresenter.ActualWidth;
            }

            if (ContentContainer.ViewportWidth > 0)
            {
                return ContentContainer.ViewportWidth;
            }

            if (ContentContainer.ActualWidth > 0)
            {
                return ContentContainer.ActualWidth;
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
