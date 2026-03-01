using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        public bool isContentVisible = true;
        public double expandedHeight;
        public string currentFolderPath = "";
        public double collapsedTopPosition;
        public double baseTopPosition;
        private Point _dragStartPoint;
        public static bool StartCollapsedByDefault = true;
        public static bool ExpandOnHover = false;
        public string assignedPresetName = "";
        public bool showHiddenItems = false;
        public bool showParentNavigationItem = true;
        public bool showFileExtensions = true;
        public bool expandOnHover = false;
        public bool openFoldersExternally = false;
        public bool showSettingsButton = true;
        public string defaultFolderPath = "";
        public string movementMode = "titlebar";
        public string searchVisibilityMode = SearchVisibilityAlways;
        public string viewMode = ViewModeIcons;
        public bool showMetadataType = true;
        public bool showMetadataSize = true;
        public bool showMetadataCreated = false;
        public bool showMetadataModified = true;
        public bool showMetadataDimensions = true;
        public List<string> metadataOrder = new List<string> { "type", "size", "created", "modified", "dimensions" };
        private bool _hoverTemporarilySuspendedByDoubleClick = false;
        private bool _hoverExpanded = false;
        private bool _hasHoverRestoreState = false;
        private double _hoverRestoreBaseTop;
        private double _hoverRestoreCollapsedTop;
        private double _hoverRestoreExpandedHeight;
        private bool _isCollapseAnimationRunning = false;
        private bool _isBottomAnchored = false;
        private bool _isExpandedShiftedByBounds = false;
        private bool _hasForcedCollapseReturnTop = false;
        private double _forcedCollapseReturnTop;
        private bool _isBoundsCorrectionInProgress = false;
        private bool _isTemporarilyForeground;
        private bool _isDragMoveActive = false;
        private bool _isManualResizeActive = false;
        private UIElement? _dragHandle;
        private Point _dragStartMouseScreen;
        private Point _dragStartWindowPosition;
        private DesktopPanel? _mergeTargetPanel;
        private UIElement? _resizeHandle;
        private Point _resizeStartMouseScreen;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private HwndSource? _windowSource;
        private CancellationTokenSource? _hoverLeaveCts;
        private bool? _queuedHoverTargetVisible;
        private CancellationTokenSource? _searchCts;
        private bool _deferSortUntilSearchComplete;
        private CancellationTokenSource? _folderLoadCts;
        private bool _suppressSearchTextChanged = false;
        private readonly HashSet<string> _searchInjectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<System.Windows.Controls.ListBoxItem> _searchInjectedItems = new List<System.Windows.Controls.ListBoxItem>();
        private readonly HashSet<string> _baseItemPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private AppearanceSettings? _currentAppearance;
        private EventHandler? _headerCornerAnimationHandler;
        private double _headerTopCornerRadius = 14;
        private double _headerBottomCornerRadius = 0;
        public const string SearchVisibilityAlways = "always";
        public const string SearchVisibilityExpanded = "expanded";
        public const string SearchVisibilityHidden = "hidden";
        public const string ViewModeIcons = "icons";
        public const string ViewModeDetails = "details";
        public const string ViewModePhotos = "photos";
        public const string MetadataType = "type";
        public const string MetadataSize = "size";
        public const string MetadataCreated = "created";
        public const string MetadataModified = "modified";
        public const string MetadataDimensions = "dimensions";
        private static readonly string[] DefaultMetadataOrder =
        {
            MetadataType,
            MetadataSize,
            MetadataCreated,
            MetadataModified,
            MetadataDimensions
        };
        private const double HeaderSearchWidth = 154;
        private const double HeaderSearchSpacerWidth = 8;
        private const double HeaderTitleMinWidth = 96;
        private const double HeaderHorizontalPadding = 24;
        private const double HeaderCoreFixedWidth = 64; // fixed spacer (8) + collapse (28) + close (28)
        private const int SearchVisibilityAnimationMs = 170;
        private const double BottomAnchorTolerance = 3.0;
        private const double ResizeGripHitSize = 18.0;
        private const int WmNcHitTest = 0x0084;
        private const int WmNcLButtonDblClk = 0x00A3;
        private const int WmSysCommand = 0x0112;
        private const int ScSize = 0xF000;
        private const int ScMove = 0xF010;
        private const int ScMaximize = 0xF030;
        private const int HtClient = 0x0001;
        private const int HtBottomRight = 0x0011;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExAppWindow = 0x00040000;
        private static readonly IntPtr HwndBottom = new IntPtr(1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
        private static readonly Thickness ExpandedChromeBorderThickness = new Thickness(1);
        private static readonly Thickness CollapsedChromeBorderThickness = new Thickness(1);
        private static readonly Thickness ExpandedChromePadding = new Thickness(1);
        private static readonly Thickness CollapsedChromePadding = new Thickness(0);
        public bool IsPreviewPanel { get; set; } = false;
        public PanelKind PanelType { get; set; } = PanelKind.None;
        public string PanelId { get; set; } = $"panel:{Guid.NewGuid():N}";
        public List<string> PinnedItems { get; } = new List<string>();
        public double zoomFactor = 1.0;
        public bool IsBottomAnchored
        {
            get => _isBottomAnchored;
            set => _isBottomAnchored = value;
        }

        private bool IsHoverBehaviorEnabled => expandOnHover && !_hoverTemporarilySuspendedByDoubleClick;

        public DesktopPanel()
        {
            InitializeComponent();
            expandedHeight = this.Height;
            collapsedTopPosition = this.Top;
            baseTopPosition = this.Top;

            ApplyAppearance(MainWindow.Appearance);
            MainWindow.AppearanceChanged += OnAppearanceChanged;
            this.Closed += (s, e) =>
            {
                CancelPendingHoverLeave();
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _searchCts = null;
                _folderLoadCts?.Cancel();
                _folderLoadCts?.Dispose();
                _folderLoadCts = null;

                MainWindow.AppearanceChanged -= OnAppearanceChanged;
                if (_windowSource != null)
                {
                    _windowSource.RemoveHook(DesktopPanelWindowProc);
                    _windowSource = null;
                }
                if (!MainWindow.IsExiting)
                {
                    MainWindow.MarkPanelHidden(this);
                }
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            };

            this.Loaded += DesktopPanel_Loaded;
            this.SourceInitialized += DesktopPanel_SourceInitialized;
            this.LocationChanged += DesktopPanel_LocationChanged;
            this.SizeChanged += DesktopPanel_SizeChanged;
            this.Activated += DesktopPanel_Activated;
            this.Deactivated += DesktopPanel_Deactivated;
            this.MouseMove += Window_MouseMoveHoverProbe;
            this.MouseEnter += Window_MouseEnter;
            this.MouseLeave += Window_MouseLeave;
            this.PreviewKeyDown += DesktopPanel_PreviewKeyDown;
            ApplySettingsButtonVisibility();
            ApplySearchVisibility();
            ApplyCollapsedVisualState(collapsed: false, animateCorners: false);
            UpdateDropZoneVisibility();
        }

        public void UpdateDropZoneVisibility()
        {
            if (DropZone == null) return;
            bool hasContent = FileList.Items.Count > 0 ||
                              !string.IsNullOrWhiteSpace(currentFolderPath) ||
                              PinnedItems.Count > 0;
            DropZone.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        }

        private double GetCollapsedHeight()
        {
            double headerHeight = 46;
            if (HeaderBar != null && HeaderBar.ActualHeight > 0)
            {
                headerHeight = HeaderBar.ActualHeight;
            }
            else if (HeaderRow != null)
            {
                if (HeaderRow.ActualHeight > 0)
                {
                    headerHeight = HeaderRow.ActualHeight;
                }
                else if (!double.IsNaN(HeaderRow.Height.Value) &&
                         !double.IsInfinity(HeaderRow.Height.Value) &&
                         HeaderRow.Height.Value > 0)
                {
                    headerHeight = HeaderRow.Height.Value;
                }
            }

            double padding = CollapsedChromePadding.Top + CollapsedChromePadding.Bottom;
            double border = CollapsedChromeBorderThickness.Top + CollapsedChromeBorderThickness.Bottom;
            double raw = Math.Max(headerHeight, headerHeight + padding + border);
            return SnapVerticalDipToDevicePixel(raw);
        }

        public double GetCollapsedHeightForRestore()
        {
            return GetCollapsedHeight();
        }

        private Rect GetWorkAreaForPanel()
        {
            try
            {
                double width = ActualWidth > 0 ? ActualWidth : Width;
                double height = ActualHeight > 0 ? ActualHeight : Height;
                int left = (int)Math.Round(Left);
                int top = (int)Math.Round(Top);
                int rectWidth = Math.Max(1, (int)Math.Round(width));
                int rectHeight = Math.Max(1, (int)Math.Round(height));
                var screen = WinForms.Screen.FromRectangle(new System.Drawing.Rectangle(left, top, rectWidth, rectHeight));
                var area = screen.WorkingArea;
                return new Rect(area.Left, area.Top, area.Width, area.Height);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private static double ClampTopToWorkArea(Rect workArea, double windowHeight, double top)
        {
            double safeHeight = Math.Max(1, windowHeight);
            if (safeHeight >= workArea.Height)
            {
                return workArea.Top;
            }

            double minTop = workArea.Top;
            double maxTop = workArea.Bottom - safeHeight;
            return Math.Max(minTop, Math.Min(maxTop, top));
        }

        private double SnapVerticalDipToDevicePixel(double value)
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (Math.Abs(scaleY) < 0.0001)
                {
                    return Math.Round(value);
                }

                return Math.Round(value * scaleY) / scaleY;
            }
            catch
            {
                return Math.Round(value);
            }
        }

        private void SnapWindowVerticalBounds(ref double top, ref double height)
        {
            top = SnapVerticalDipToDevicePixel(top);
            height = Math.Max(1, SnapVerticalDipToDevicePixel(height));
        }

        private bool EnsurePanelInsideVerticalWorkArea()
        {
            if (_isBoundsCorrectionInProgress) return false;

            Rect workArea = GetWorkAreaForPanel();
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double clampedTop = ClampTopToWorkArea(workArea, currentHeight, Top);
            if (Math.Abs(clampedTop - Top) <= 0.5)
            {
                return false;
            }

            _isBoundsCorrectionInProgress = true;
            try
            {
                Top = clampedTop;
            }
            finally
            {
                _isBoundsCorrectionInProgress = false;
            }

            return true;
        }

        private bool EnsurePanelNotAboveWorkArea()
        {
            if (_isBoundsCorrectionInProgress) return false;

            double minTop = GetWorkAreaForPanel().Top;
            if (Top >= minTop - 0.5)
            {
                return false;
            }

            _isBoundsCorrectionInProgress = true;
            try
            {
                Top = minTop;
            }
            finally
            {
                _isBoundsCorrectionInProgress = false;
            }

            return true;
        }

        private void SetContentScrollbarsFrozen(bool frozen)
        {
            if (ContentContainer == null) return;

            ContentContainer.VerticalScrollBarVisibility = frozen
                ? System.Windows.Controls.ScrollBarVisibility.Hidden
                : System.Windows.Controls.ScrollBarVisibility.Auto;
        }

        private void SetContentLayerVisibility(bool visible)
        {
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (ContentFrame != null)
            {
                ContentFrame.BeginAnimation(OpacityProperty, null);
                ContentFrame.Visibility = visibility;
                ContentFrame.Opacity = visible ? 1 : 0;
            }
            if (ContentContainer != null)
            {
                ContentContainer.Visibility = visibility;
            }
        }

        private void AnimateContentIn()
        {
            if (ContentFrame == null) return;
            ContentFrame.Visibility = Visibility.Visible;
            ContentFrame.RenderTransformOrigin = new Point(0.5, 0);

            ContentFrame.Opacity = 0;
            ContentFrame.RenderTransform = new ScaleTransform(1, 0.92);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            fadeIn.Completed += (s, e) =>
            {
                ContentFrame.BeginAnimation(OpacityProperty, null);
                ContentFrame.Opacity = 1;
            };
            var scaleY = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            scaleY.Completed += (s, e) =>
            {
                if (ContentFrame.RenderTransform is ScaleTransform completedSt)
                {
                    completedSt.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    completedSt.ScaleY = 1;
                }
            };
            ContentFrame.BeginAnimation(OpacityProperty, fadeIn);
            ((ScaleTransform)ContentFrame.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }

        private void AnimateShadow(bool expanding)
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance;
            double headerBaseOpacity = MainWindow.ResolveHeaderShadowOpacity(appearance);
            double headerBaseBlur = MainWindow.ResolveHeaderShadowBlur(appearance);
            double bodyBaseOpacity = MainWindow.ResolveBodyShadowOpacity(appearance);
            double bodyBaseBlur = MainWindow.ResolveBodyShadowBlur(appearance);

            double headerTargetOpacity = expanding ? headerBaseOpacity : Math.Max(0, headerBaseOpacity * 0.45);
            double headerTargetBlur = expanding ? headerBaseBlur : Math.Max(0, headerBaseBlur * 0.5);
            double bodyTargetOpacity = expanding ? bodyBaseOpacity : Math.Max(0, bodyBaseOpacity * 0.35);
            double bodyTargetBlur = expanding ? bodyBaseBlur : Math.Max(0, bodyBaseBlur * 0.45);

            var dur = TimeSpan.FromMilliseconds(320);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (HeaderShadow != null)
            {
                var headerOpacityAnim = new DoubleAnimation(headerTargetOpacity, dur) { EasingFunction = ease };
                var headerBlurAnim = new DoubleAnimation(headerTargetBlur, dur) { EasingFunction = ease };
                HeaderShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, headerOpacityAnim);
                HeaderShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, headerBlurAnim);
            }
            if (BodyShadow != null)
            {
                var bodyOpacityAnim = new DoubleAnimation(bodyTargetOpacity, dur) { EasingFunction = ease };
                var bodyBlurAnim = new DoubleAnimation(bodyTargetBlur, dur) { EasingFunction = ease };
                BodyShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, bodyOpacityAnim);
                BodyShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, bodyBlurAnim);
            }
        }

        private void SetHeaderCornerBaseRadius(double radius)
        {
            _headerTopCornerRadius = Math.Max(0, radius);
            ApplyHeaderCornerRadius(isContentVisible ? 0 : _headerTopCornerRadius);
        }

        private void StopHeaderCornerAnimation()
        {
            if (_headerCornerAnimationHandler == null) return;
            CompositionTarget.Rendering -= _headerCornerAnimationHandler;
            _headerCornerAnimationHandler = null;
        }

        private void ApplyHeaderCornerRadius(double bottomRadius)
        {
            if (HeaderBar == null) return;

            double clamped = Math.Max(0, Math.Min(_headerTopCornerRadius, bottomRadius));
            _headerBottomCornerRadius = clamped;
            HeaderBar.CornerRadius = new CornerRadius(_headerTopCornerRadius, _headerTopCornerRadius, clamped, clamped);
            if (HeaderShadowHost != null)
            {
                double outerTop = _headerTopCornerRadius + 2;
                double outerBottom = clamped + 2;
                HeaderShadowHost.CornerRadius = new CornerRadius(outerTop, outerTop, outerBottom, outerBottom);
            }
        }

        private void AnimateHeaderBottomCorners(double targetBottomRadius, TimeSpan duration, IEasingFunction? easing = null)
        {
            if (duration.TotalMilliseconds <= 0)
            {
                ApplyHeaderCornerRadius(targetBottomRadius);
                return;
            }

            StopHeaderCornerAnimation();

            double from = _headerBottomCornerRadius;
            double to = Math.Max(0, Math.Min(_headerTopCornerRadius, targetBottomRadius));
            if (Math.Abs(from - to) < 0.01)
            {
                ApplyHeaderCornerRadius(to);
                return;
            }

            DateTime started = DateTime.UtcNow;
            _headerCornerAnimationHandler = (s, e) =>
            {
                double progress = (DateTime.UtcNow - started).TotalMilliseconds / duration.TotalMilliseconds;
                if (progress >= 1)
                {
                    StopHeaderCornerAnimation();
                    ApplyHeaderCornerRadius(to);
                    return;
                }

                progress = Math.Max(0, Math.Min(1, progress));
                double eased = easing != null ? easing.Ease(progress) : progress;
                double value = from + ((to - from) * eased);
                ApplyHeaderCornerRadius(value);
            };

            CompositionTarget.Rendering += _headerCornerAnimationHandler;
        }

        private void ApplyCollapsedVisualState(bool collapsed, bool animateCorners, TimeSpan? duration = null)
        {
            ResizeMode = ResizeMode.NoResize;
            if (PanelChrome != null)
            {
                PanelChrome.BorderThickness = collapsed ? CollapsedChromeBorderThickness : ExpandedChromeBorderThickness;
                PanelChrome.Padding = collapsed ? CollapsedChromePadding : ExpandedChromePadding;
            }
            if (ManualResizeGrip != null)
            {
                ManualResizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            if (BodyShadowHost != null)
            {
                BodyShadowHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            RebuildTabBar(collapsed);
            UpdateHeaderBottomBorderForCurrentState(collapsed);

            double targetBottomRadius = collapsed ? _headerTopCornerRadius : 0;
            if (animateCorners)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                AnimateHeaderBottomCorners(targetBottomRadius, duration ?? TimeSpan.FromMilliseconds(320), ease);
            }
            else
            {
                StopHeaderCornerAnimation();
                ApplyHeaderCornerRadius(targetBottomRadius);
            }
        }

        private void UpdateHeaderBottomBorderForCurrentState(bool collapsed)
        {
            if (HeaderBar == null)
            {
                return;
            }

            if (collapsed)
            {
                HeaderBar.BorderThickness = new Thickness(0);
                return;
            }

            bool hasMultipleTabs = _tabs.Count > 1;
            HeaderBar.BorderThickness = hasMultipleTabs
                ? new Thickness(0)
                : new Thickness(0, 0, 0, 1);
        }

        private bool IsBottomAligned(double top, double height)
        {
            double bottom = top + height;
            return Math.Abs(bottom - GetWorkAreaForPanel().Bottom) <= BottomAnchorTolerance;
        }

        private double GetBottomAnchoredCollapsedTop(double collapsedHeight)
        {
            Rect workArea = GetWorkAreaForPanel();
            return Math.Max(workArea.Top, workArea.Bottom - collapsedHeight);
        }

        private bool ShouldAnchorToBottom(double collapsedHeight)
        {
            if (!isContentVisible)
            {
                return false;
            }

            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            // Only anchor as a special case when the panel is actually expanded.
            if (currentHeight <= collapsedHeight + 0.5)
            {
                return false;
            }

            return IsBottomAligned(Top, currentHeight);
        }

        private bool ShouldUseBottomAnchorOnExpand(double collapsedHeight)
        {
            if (_isBottomAnchored)
            {
                return true;
            }

            // If a collapsed panel sits at the bottom edge, expand upward so
            // hover-expand/collapse keeps the title bar at the screen bottom.
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            if (currentHeight <= collapsedHeight + 0.5)
            {
                return IsBottomAligned(Top, currentHeight);
            }

            return false;
        }

        private double GetCollapseReferenceTop()
        {
            if (_hasForcedCollapseReturnTop)
            {
                return _forcedCollapseReturnTop;
            }

            if (_hoverExpanded && _hasHoverRestoreState)
            {
                return _hoverRestoreCollapsedTop;
            }

            if (double.IsNaN(collapsedTopPosition) || double.IsInfinity(collapsedTopPosition))
            {
                return baseTopPosition;
            }

            return collapsedTopPosition;
        }

        private void SyncAnchoringFromCurrentBounds()
        {
            double collapsedHeight = GetCollapsedHeight();
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;

            if (isContentVisible)
            {
                if (_hoverExpanded && _hasHoverRestoreState)
                {
                    double persistedExpandedHeight = Math.Max(collapsedHeight, currentHeight);
                    expandedHeight = persistedExpandedHeight;
                    _hoverRestoreExpandedHeight = persistedExpandedHeight;
                    return;
                }

                if (_isExpandedShiftedByBounds)
                {
                    expandedHeight = Math.Max(collapsedHeight, currentHeight);
                    return;
                }

                _isBottomAnchored = false;
                baseTopPosition = Top;
                collapsedTopPosition = Top;
                expandedHeight = Math.Max(collapsedHeight, currentHeight);
            }
            else
            {
                collapsedTopPosition = Top;
                _isBottomAnchored = false;
                baseTopPosition = collapsedTopPosition;
                _isExpandedShiftedByBounds = false;
                _hasForcedCollapseReturnTop = false;
            }
        }

        private void CaptureHoverRestoreState()
        {
            _hasHoverRestoreState = true;
            _hoverRestoreBaseTop = baseTopPosition;
            _hoverRestoreCollapsedTop = Top;
            _hoverRestoreExpandedHeight = Math.Max(GetCollapsedHeight(), expandedHeight);
        }

        private void ApplyHoverRestoreStateForCollapse()
        {
            if (!_hasHoverRestoreState) return;

            baseTopPosition = _hoverRestoreBaseTop;
            collapsedTopPosition = _hoverRestoreCollapsedTop;
            double collapsedHeight = GetCollapsedHeight();
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double persistedExpandedHeight = Math.Max(collapsedHeight, Math.Max(_hoverRestoreExpandedHeight, currentHeight));
            _hoverRestoreExpandedHeight = persistedExpandedHeight;
            expandedHeight = persistedExpandedHeight;
        }

        private void RebaseHoverRestoreStateFromCurrentBounds()
        {
            double collapsedHeight = GetCollapsedHeight();
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double persistedExpandedHeight = Math.Max(collapsedHeight, currentHeight);

            _isBottomAnchored = false;
            _isExpandedShiftedByBounds = false;
            _hasForcedCollapseReturnTop = false;
            baseTopPosition = Top;
            collapsedTopPosition = Top;
            expandedHeight = persistedExpandedHeight;
            _hasHoverRestoreState = true;
            _hoverRestoreBaseTop = baseTopPosition;
            _hoverRestoreCollapsedTop = collapsedTopPosition;
            _hoverRestoreExpandedHeight = persistedExpandedHeight;
        }

        private void ClearHoverRestoreState()
        {
            _hasHoverRestoreState = false;
        }

        public void SetExpandOnHover(bool enabled)
        {
            expandOnHover = enabled;
            _hoverTemporarilySuspendedByDoubleClick = false;
            _queuedHoverTargetVisible = null;

            if (!enabled)
            {
                CancelPendingHoverLeave();
                _hoverExpanded = false;
                ClearHoverRestoreState();
            }
        }

        private void HandleHeaderDoubleClickToggle()
        {
            if (!expandOnHover)
            {
                ToggleCollapseAnimated();
                return;
            }

            if (_isCollapseAnimationRunning)
            {
                return;
            }

            CancelPendingHoverLeave();
            _queuedHoverTargetVisible = null;
            _hoverExpanded = false;
            ClearHoverRestoreState();

            if (_hoverTemporarilySuspendedByDoubleClick)
            {
                _hoverTemporarilySuspendedByDoubleClick = false;
                if (isContentVisible)
                {
                    ToggleCollapseAnimated();
                }
                return;
            }

            _hoverTemporarilySuspendedByDoubleClick = true;
            if (!isContentVisible)
            {
                ToggleCollapseAnimated();
            }
        }

        private void CancelPendingHoverLeave()
        {
            var pending = _hoverLeaveCts;
            _hoverLeaveCts = null;
            if (pending == null)
            {
                return;
            }

            pending.Cancel();
            pending.Dispose();
        }

        private void RequestHoverExpandAnimated()
        {
            CancelPendingHoverLeave();

            if (!IsHoverBehaviorEnabled)
            {
                _queuedHoverTargetVisible = null;
                return;
            }

            if (isContentVisible)
            {
                _queuedHoverTargetVisible = null;
                return;
            }

            if (_isCollapseAnimationRunning)
            {
                _queuedHoverTargetVisible = true;
                return;
            }

            _queuedHoverTargetVisible = null;
            CaptureHoverRestoreState();
            _hoverExpanded = true;
            ToggleCollapseAnimated();
        }

        private void RequestHoverCollapseAnimated()
        {
            if (!IsHoverBehaviorEnabled || !_hoverExpanded)
            {
                _queuedHoverTargetVisible = null;
                return;
            }

            if (IsMouseOver || IsCursorWithinPanelBounds())
            {
                _queuedHoverTargetVisible = null;
                return;
            }

            if (_isCollapseAnimationRunning)
            {
                _queuedHoverTargetVisible = false;
                return;
            }

            _queuedHoverTargetVisible = null;
            ApplyHoverRestoreStateForCollapse();
            ToggleCollapseAnimated();
        }

        private void ProcessQueuedHoverState()
        {
            if (_isCollapseAnimationRunning)
            {
                return;
            }

            if (!_queuedHoverTargetVisible.HasValue)
            {
                if (_hoverExpanded && IsHoverBehaviorEnabled && !IsMouseOver && !IsCursorWithinPanelBounds())
                {
                    RequestHoverCollapseAnimated();
                }
                return;
            }

            bool targetVisible = _queuedHoverTargetVisible.Value;
            _queuedHoverTargetVisible = null;

            if (targetVisible)
            {
                RequestHoverExpandAnimated();
            }
            else
            {
                RequestHoverCollapseAnimated();
            }
        }

        private void DesktopPanel_Loaded(object sender, RoutedEventArgs e)
        {
            EnsurePanelInsideVerticalWorkArea();
            SyncAnchoringFromCurrentBounds();
            ApplySearchVisibility();
            ApplyCollapsedVisualState(!isContentVisible, animateCorners: false);
        }

        private void DesktopPanel_SourceInitialized(object? sender, EventArgs e)
        {
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            _windowSource?.AddHook(DesktopPanelWindowProc);
            ApplyDesktopWindowBehavior();
        }

        private static int GetExtendedWindowStyle(IntPtr handle)
        {
            if (IntPtr.Size == 8)
            {
                return (int)GetWindowLongPtr64(handle, GwlExStyle).ToInt64();
            }

            return GetWindowLong32(handle, GwlExStyle);
        }

        private static void SetExtendedWindowStyle(IntPtr handle, int style)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style));
                return;
            }

            SetWindowLong32(handle, GwlExStyle, style);
        }

        private void ApplyDesktopWindowBehavior()
        {
            if (IsPreviewPanel) return;
            IntPtr handle = _windowSource?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            int extendedStyle = GetExtendedWindowStyle(handle);
            int desiredStyle = (extendedStyle | WsExToolWindow) & ~WsExAppWindow;
            if (desiredStyle != extendedStyle)
            {
                SetExtendedWindowStyle(handle, desiredStyle);
            }

            ShowInTaskbar = false;
            SendPanelToBack();
        }

        internal void SendPanelToBack()
        {
            if (_isTemporarilyForeground) return;
            if (IsPreviewPanel) return;
            IntPtr handle = _windowSource?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            SetWindowPos(
                handle,
                HwndBottom,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }

        public void SetTemporaryForegroundMode(bool enabled)
        {
            _isTemporarilyForeground = enabled;

            if (enabled)
            {
                Topmost = true;
                return;
            }

            Topmost = false;
            SendPanelToBack();
        }

        private IntPtr DesktopPanelWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (TryHandleShellContextMenuWindowMessage(msg, wParam, lParam, ref handled, out IntPtr shellMenuResult))
            {
                return shellMenuResult;
            }

            if (msg == WmNcLButtonDblClk)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WmSysCommand)
            {
                int command = (int)(wParam.ToInt64() & 0xFFF0);
                if (command == ScMove || command == ScMaximize || command == ScSize)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            if (msg != WmNcHitTest)
            {
                return IntPtr.Zero;
            }

            handled = true;
            return new IntPtr(HtClient);
        }

        private void DesktopPanel_LocationChanged(object? sender, EventArgs e)
        {
            if (_isDragMoveActive) return;
            if (_isCollapseAnimationRunning) return;

            if (EnsurePanelInsideVerticalWorkArea())
            {
                return;
            }
            SyncAnchoringFromCurrentBounds();
            MainWindow.SaveSettings();
        }

        private void DesktopPanel_Activated(object? sender, EventArgs e)
        {
            SendPanelToBack();
            if (SearchBox != null && SearchBox.IsKeyboardFocusWithin)
            {
                FocusManager.SetFocusedElement(this, this);
                Keyboard.Focus(this);
            }
        }

        private void DesktopPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isDragMoveActive) return;
            if (_isCollapseAnimationRunning) return;

            ApplySearchVisibility(animate: false);
            SyncAnchoringFromCurrentBounds();
            MainWindow.SaveSettings();
        }

        private void ManualResizeGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
            if (_isCollapseAnimationRunning || _isDragMoveActive) return;

            // Double-click on the resize grip triggers the same behavior as "Fit to content".
            if (e.ClickCount >= 2)
            {
                FitToContent();
                e.Handled = true;
                return;
            }

            if (sender is UIElement resizeHandle)
            {
                BeginManualResize(resizeHandle);
                e.Handled = true;
            }
        }

        private void BeginManualResize(UIElement resizeHandle)
        {
            if (_isManualResizeActive || _isDragMoveActive) return;
            if (!isContentVisible || _isCollapseAnimationRunning) return;

            StopPanelAnimations();
            _resizeHandle = resizeHandle;
            _resizeStartMouseScreen = GetMouseScreenPositionDip();
            _resizeStartWidth = ActualWidth > 0 ? ActualWidth : Width;
            _resizeStartHeight = ActualHeight > 0 ? ActualHeight : Height;
            _isManualResizeActive = true;

            resizeHandle.MouseMove += ResizeHandle_MouseMove;
            resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
            resizeHandle.LostMouseCapture += ResizeHandle_LostMouseCapture;
            resizeHandle.CaptureMouse();
        }

        private void EndManualResize(bool commitSize)
        {
            var handle = _resizeHandle;
            if (handle != null)
            {
                handle.MouseMove -= ResizeHandle_MouseMove;
                handle.MouseLeftButtonUp -= ResizeHandle_MouseLeftButtonUp;
                handle.LostMouseCapture -= ResizeHandle_LostMouseCapture;
                if (handle.IsMouseCaptured)
                {
                    handle.ReleaseMouseCapture();
                }
                _resizeHandle = null;
            }

            bool wasResizing = _isManualResizeActive;
            _isManualResizeActive = false;

            if (commitSize && wasResizing)
            {
                double collapsedHeight = GetCollapsedHeight();
                double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                expandedHeight = Math.Max(collapsedHeight, currentHeight);
                SyncAnchoringFromCurrentBounds();
                MainWindow.SaveSettings();
            }
        }

        private void ResizeHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isManualResizeActive) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndManualResize(commitSize: true);
                return;
            }

            Point current = GetMouseScreenPositionDip();
            double deltaX = current.X - _resizeStartMouseScreen.X;
            double deltaY = current.Y - _resizeStartMouseScreen.Y;
            double minWidth = Math.Max(220, MinWidth > 0 ? MinWidth : 0);
            double minHeight = Math.Max(GetCollapsedHeight(), MinHeight > 0 ? MinHeight : 0);
            Rect workArea = GetWorkAreaForPanel();

            double targetWidth = Math.Max(minWidth, _resizeStartWidth + deltaX);
            double targetHeight = Math.Max(minHeight, _resizeStartHeight + deltaY);
            double maxHeightToBottom = Math.Max(minHeight, workArea.Bottom - Top);

            Width = targetWidth;
            Height = Math.Min(targetHeight, maxHeightToBottom);
            UpdateWrapPanelWidth();
        }

        private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndManualResize(commitSize: true);
            e.Handled = true;
        }

        private void ResizeHandle_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            EndManualResize(commitSize: true);
        }

        private void DesktopPanel_Deactivated(object? sender, EventArgs e)
        {
            if (IsMouseOver || IsCursorWithinPanelBounds()) return;
            if (_isRubberBandSelecting)
            {
                EndRubberBandSelection();
            }
            if (FileList?.SelectedItems.Count > 0)
            {
                FileList.SelectedItems.Clear();
            }
        }

        private void StopPanelAnimations()
        {
            bool wasAnimating = _isCollapseAnimationRunning;
            BeginAnimation(TopProperty, null);
            BeginAnimation(HeightProperty, null);
            SetContentScrollbarsFrozen(false);
            if (ContentFrame != null)
            {
                ContentFrame.BeginAnimation(OpacityProperty, null);
                if (ContentFrame.RenderTransform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    st.ScaleY = 1;
                }
                ContentFrame.Opacity = isContentVisible ? 1 : 0;
            }
            if (HeaderShadow != null && wasAnimating)
            {
                HeaderShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
                HeaderShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            }
            if (BodyShadow != null && wasAnimating)
            {
                BodyShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
                BodyShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            }
            StopHeaderCornerAnimation();
            _isCollapseAnimationRunning = false;
        }

        public void ForceCollapseState(bool isCollapsed)
        {
            StopPanelAnimations();
            _isCollapseAnimationRunning = true;
            SetContentScrollbarsFrozen(false);
            double collapsedHeight = GetCollapsedHeight();
            try
            {
                if (isCollapsed)
                {
                    if (isContentVisible)
                    {
                        double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                        if (!(_hoverExpanded && _hasHoverRestoreState))
                        {
                            bool hasExpandedBounds = currentHeight > collapsedHeight + 0.5;
                            bool hasStoredExpandedHeight = expandedHeight > collapsedHeight + 0.5;
                            if (hasExpandedBounds || !hasStoredExpandedHeight)
                            {
                                expandedHeight = Math.Max(collapsedHeight, currentHeight);
                            }
                        }
                    }

                    bool anchorToBottom = ShouldAnchorToBottom(collapsedHeight);
                    double collapseReferenceTop = GetCollapseReferenceTop();
                    double targetTop = anchorToBottom ? GetBottomAnchoredCollapsedTop(collapsedHeight) : collapseReferenceTop;
                    Rect workArea = GetWorkAreaForPanel();
                    targetTop = ClampTopToWorkArea(workArea, collapsedHeight, targetTop);
                    double targetHeight = collapsedHeight;
                    SnapWindowVerticalBounds(ref targetTop, ref targetHeight);

                    this.Top = targetTop;
                    this.Height = targetHeight;
                    SetContentLayerVisibility(false);
                    isContentVisible = false;
                    _hoverExpanded = false;
                    collapsedTopPosition = targetTop;
                    _isBottomAnchored = anchorToBottom;
                    baseTopPosition = collapsedTopPosition;
                    _isExpandedShiftedByBounds = false;
                    _hasForcedCollapseReturnTop = false;
                    ApplyCollapsedVisualState(collapsed: true, animateCorners: false);
                    ApplySearchVisibility();
                    ClearHoverRestoreState();
                }
                else
                {
                    double targetHeight = Math.Max(collapsedHeight, expandedHeight);
                    bool anchorToBottom = ShouldUseBottomAnchorOnExpand(collapsedHeight);
                    double referenceTop = GetCollapseReferenceTop();
                    if (double.IsNaN(referenceTop) || double.IsInfinity(referenceTop))
                    {
                        referenceTop = Top;
                    }
                    collapsedTopPosition = referenceTop;
                    baseTopPosition = referenceTop;
                    double targetTop = anchorToBottom
                        ? GetBottomAnchoredCollapsedTop(collapsedHeight) + collapsedHeight - targetHeight
                        : referenceTop;
                    Rect workArea = GetWorkAreaForPanel();
                    targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);
                    SnapWindowVerticalBounds(ref targetTop, ref targetHeight);
                    bool shiftedUpByBounds = !anchorToBottom && targetTop < referenceTop - 0.5;
                    _hasForcedCollapseReturnTop = true;
                    _forcedCollapseReturnTop = referenceTop;

                    this.Top = targetTop;
                    this.Height = targetHeight;
                    SetContentLayerVisibility(true);
                    Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Render);
                    isContentVisible = true;
                    _isBottomAnchored = anchorToBottom;
                    _isExpandedShiftedByBounds = shiftedUpByBounds;
                    if (anchorToBottom)
                    {
                        collapsedTopPosition = GetBottomAnchoredCollapsedTop(collapsedHeight);
                        baseTopPosition = collapsedTopPosition;
                    }

                    ApplyCollapsedVisualState(collapsed: false, animateCorners: false);
                    ApplySearchVisibility();
                }
            }
            finally
            {
                _isCollapseAnimationRunning = false;
            }
        }

        private Point GetMouseScreenPositionDip()
        {
            var raw = WinForms.Control.MousePosition;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                return source.CompositionTarget.TransformFromDevice.Transform(new Point(raw.X, raw.Y));
            }

            return new Point(raw.X, raw.Y);
        }

        private void BeginManualWindowDrag(UIElement dragHandle)
        {
            if (_isDragMoveActive) return;

            StopPanelAnimations();
            _dragHandle = dragHandle;
            _dragStartMouseScreen = GetMouseScreenPositionDip();
            _dragStartWindowPosition = new Point(Left, Top);
            _isDragMoveActive = true;

            dragHandle.MouseMove += DragHandle_MouseMove;
            dragHandle.MouseLeftButtonUp += DragHandle_MouseLeftButtonUp;
            dragHandle.LostMouseCapture += DragHandle_LostMouseCapture;
            dragHandle.CaptureMouse();
        }

        private void EndManualWindowDrag(bool commitPosition)
        {
            var handle = _dragHandle;
            if (handle != null)
            {
                handle.MouseMove -= DragHandle_MouseMove;
                handle.MouseLeftButtonUp -= DragHandle_MouseLeftButtonUp;
                handle.LostMouseCapture -= DragHandle_LostMouseCapture;
                if (handle.IsMouseCaptured)
                {
                    handle.ReleaseMouseCapture();
                }
                _dragHandle = null;
            }

            bool wasDragging = _isDragMoveActive;
            _isDragMoveActive = false;

            // Check for panel merge before normal commit
            if (commitPosition && wasDragging && _mergeTargetPanel != null)
            {
                MergePanelIntoTarget();
                return;
            }

            ClearMergeTarget();

            if (commitPosition && wasDragging)
            {
                const double dragCommitThreshold = 1.5;
                bool positionChanged =
                    Math.Abs(Left - _dragStartWindowPosition.X) > dragCommitThreshold ||
                    Math.Abs(Top - _dragStartWindowPosition.Y) > dragCommitThreshold;
                if (!positionChanged)
                {
                    return;
                }

                EnsurePanelInsideVerticalWorkArea();
                _isExpandedShiftedByBounds = false;
                _hasForcedCollapseReturnTop = false;
                if (_hoverExpanded)
                {
                    RebaseHoverRestoreStateFromCurrentBounds();
                }
                else
                {
                    SyncAnchoringFromCurrentBounds();
                }
                MainWindow.SaveSettings();
            }
        }

        /// <summary>
        /// Merges all tabs from this panel into the merge target panel, then closes this panel.
        /// </summary>
        private void MergePanelIntoTarget()
        {
            var target = _mergeTargetPanel;
            ClearMergeTarget();

            if (target == null || !target.IsVisible) return;

            // Save current state of all tabs
            SaveActiveTabState();

            // Transfer all tabs to the target panel
            foreach (var tab in _tabs.ToList())
            {
                target.InsertTab(tab, switchTo: false);
            }

            // Switch target to the first newly added tab
            if (target.Tabs.Count > 0)
            {
                target.SwitchToTab(target.Tabs.Count - _tabs.Count);
            }

            // Close this panel
            Close();
            MainWindow.SaveSettings();
        }

        private void DragHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDragMoveActive) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndManualWindowDrag(commitPosition: true);
                return;
            }

            Point current = GetMouseScreenPositionDip();
            double deltaX = current.X - _dragStartMouseScreen.X;
            double deltaY = current.Y - _dragStartMouseScreen.Y;
            Left = _dragStartWindowPosition.X + deltaX;
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double desiredTop = _dragStartWindowPosition.Y + deltaY;
            Top = ClampTopToWorkArea(GetWorkAreaForPanel(), currentHeight, desiredTop);

            // Check for panel merge: is the mouse over another panel's header?
            UpdateMergeTarget(current);
        }

        /// <summary>
        /// During panel drag, checks if the mouse is over another panel's header area
        /// and shows/hides merge feedback accordingly.
        /// </summary>
        private void UpdateMergeTarget(Point mouseScreenDip)
        {
            DesktopPanel? newTarget = null;

            foreach (var other in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
            {
                if (other == this || !other.IsVisible) continue;

                // Check if mouse is within the other panel's header bounds
                double headerHeight = 46;
                if (mouseScreenDip.X >= other.Left && mouseScreenDip.X <= other.Left + other.ActualWidth &&
                    mouseScreenDip.Y >= other.Top && mouseScreenDip.Y <= other.Top + headerHeight)
                {
                    newTarget = other;
                    break;
                }
            }

            if (newTarget != _mergeTargetPanel)
            {
                // Hide indicator on old target
                if (_mergeTargetPanel != null)
                {
                    _mergeTargetPanel.TabDropPreview.Visibility = Visibility.Collapsed;
                }

                _mergeTargetPanel = newTarget;

                // Show indicator on new target
                if (_mergeTargetPanel != null)
                {
                    _mergeTargetPanel.TabDropPreview.Visibility = Visibility.Visible;
                }
            }
        }

        private void ClearMergeTarget()
        {
            if (_mergeTargetPanel != null)
            {
                _mergeTargetPanel.TabDropPreview.Visibility = Visibility.Collapsed;
                _mergeTargetPanel = null;
            }
        }

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndManualWindowDrag(commitPosition: true);
            e.Handled = true;
        }

        private void DragHandle_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            EndManualWindowDrag(commitPosition: true);
        }

        private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (movementMode == "locked") return;
            if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
            BeginManualWindowDrag(this);
            e.Handled = true;
        }

        private static bool ShouldIgnoreHeaderDoubleClick(DependencyObject? source)
        {
            if (source == null)
            {
                return false;
            }

            return FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) != null ||
                   FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) != null ||
                   FindAncestor<System.Windows.Controls.PasswordBox>(source) != null ||
                   FindAncestor<System.Windows.Controls.ComboBox>(source) != null;
        }

        private void HeaderBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount < 2)
            {
                return;
            }

            if (ShouldIgnoreHeaderDoubleClick(e.OriginalSource as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            HandleHeaderDoubleClickToggle();
        }

        private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (movementMode == "locked") return;

            if (movementMode == "titlebar" &&
                e.ChangedButton == MouseButton.Left &&
                e.LeftButton == MouseButtonState.Pressed &&
                sender is UIElement dragHandle)
            {
                BeginManualWindowDrag(dragHandle);
                e.Handled = true;
            }
        }

        private void ToggleCollapseAnimated()
        {
            if (_isCollapseAnimationRunning) return;

            StopPanelAnimations();
            _isCollapseAnimationRunning = true;

            var duration = TimeSpan.FromMilliseconds(320);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            double collapsedHeight = GetCollapsedHeight();

            if (isContentVisible)
            {
                double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                if (!(_hoverExpanded && _hasHoverRestoreState))
                {
                    expandedHeight = Math.Max(collapsedHeight, currentHeight);
                }
                double targetHeight = collapsedHeight;
                bool anchorToBottom = ShouldAnchorToBottom(collapsedHeight);
                double collapseReferenceTop = GetCollapseReferenceTop();
                double targetTop = anchorToBottom ? GetBottomAnchoredCollapsedTop(collapsedHeight) : collapseReferenceTop;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);
                SnapWindowVerticalBounds(ref targetTop, ref targetHeight);
                SetContentScrollbarsFrozen(true);

                // Content stays visible during collapse - ClipToBounds clips it naturally
                // Fade content opacity during the collapse for a polished look
                if (ContentFrame != null)
                {
                    var contentFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    ContentFrame.BeginAnimation(OpacityProperty, contentFade);
                }
                ApplyCollapsedVisualState(collapsed: true, animateCorners: true, duration);
                AnimateShadow(expanding: false);

                Action onComplete = () =>
                {
                    this.Top = targetTop;
                    this.Height = targetHeight;
                    if (ContentFrame != null) ContentFrame.BeginAnimation(OpacityProperty, null);
                    SetContentLayerVisibility(false);
                    SetContentScrollbarsFrozen(false);
                    isContentVisible = false;
                    _hoverExpanded = false;
                    collapsedTopPosition = targetTop;
                    _isBottomAnchored = anchorToBottom;
                    baseTopPosition = collapsedTopPosition;
                    _isExpandedShiftedByBounds = false;
                    _hasForcedCollapseReturnTop = false;
                    ApplyCollapsedVisualState(collapsed: true, animateCorners: false);
                    ApplySearchVisibility();
                    ClearHoverRestoreState();
                    _isCollapseAnimationRunning = false;
                    ProcessQueuedHoverState();
                    MainWindow.SaveSettings();
                };

                bool animateTop = Math.Abs(targetTop - this.Top) > 0.5;
                if (animateTop)
                {
                    AnimateTopAndHeight(targetTop, targetHeight, duration, ease, onComplete);
                }
                else
                {
                    AnimateHeight(targetHeight, duration, ease, onComplete);
                }
            }
            else
            {
                SetContentScrollbarsFrozen(false);
                double targetHeight = Math.Max(collapsedHeight, expandedHeight);
                bool anchorToBottom = ShouldUseBottomAnchorOnExpand(collapsedHeight);
                double referenceTop = GetCollapseReferenceTop();
                if (double.IsNaN(referenceTop) || double.IsInfinity(referenceTop))
                {
                    referenceTop = Top;
                }
                collapsedTopPosition = referenceTop;
                baseTopPosition = referenceTop;
                double targetTop = anchorToBottom
                    ? GetBottomAnchoredCollapsedTop(collapsedHeight) + collapsedHeight - targetHeight
                    : referenceTop;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);
                SnapWindowVerticalBounds(ref targetTop, ref targetHeight);
                bool shiftedUpByBounds = !anchorToBottom && targetTop < referenceTop - 0.5;
                _hasForcedCollapseReturnTop = true;
                _forcedCollapseReturnTop = referenceTop;

                ApplyCollapsedVisualState(collapsed: false, animateCorners: true, duration);
                AnimateShadow(expanding: true);

                // Make content visible immediately but transparent, so it fades in WITH the height animation
                if (ContentFrame != null)
                {
                    ContentFrame.BeginAnimation(OpacityProperty, null);
                    ContentFrame.Visibility = Visibility.Visible;
                    ContentFrame.Opacity = 0;
                }
                if (ContentContainer != null) ContentContainer.Visibility = Visibility.Visible;
                Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Render);
                AnimateContentIn();

                Action onComplete = () =>
                {
                    this.Top = targetTop;
                    this.Height = targetHeight;
                    isContentVisible = true;
                    _isBottomAnchored = anchorToBottom;
                    _isExpandedShiftedByBounds = shiftedUpByBounds;
                    if (anchorToBottom)
                    {
                        collapsedTopPosition = GetBottomAnchoredCollapsedTop(collapsedHeight);
                        baseTopPosition = collapsedTopPosition;
                    }
                    ApplyCollapsedVisualState(collapsed: false, animateCorners: false);
                    ApplySearchVisibility();
                    _isCollapseAnimationRunning = false;
                    ProcessQueuedHoverState();
                    MainWindow.SaveSettings();
                };

                bool animateTop = Math.Abs(targetTop - this.Top) > 0.5;
                if (animateTop)
                {
                    AnimateTopAndHeight(targetTop, targetHeight, duration, ease, onComplete);
                }
                else
                {
                    AnimateHeight(targetHeight, duration, ease, onComplete);
                }
            }
        }

        private void AnimateHeight(double targetHeight, TimeSpan duration, IEasingFunction ease, Action? onComplete = null)
        {
            var animation = new DoubleAnimation(targetHeight, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            if (onComplete != null)
            {
                animation.Completed += (s, e) => onComplete();
            }
            this.BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void AnimateTopAndHeight(double targetTop, double targetHeight, TimeSpan duration, IEasingFunction ease, Action? onComplete = null)
        {
            var topAnim = new DoubleAnimation(targetTop, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            var heightAnim = new DoubleAnimation(targetHeight, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };

            if (onComplete != null)
            {
                heightAnim.Completed += (s, e) => onComplete();
            }

            this.BeginAnimation(TopProperty, topAnim, HandoffBehavior.SnapshotAndReplace);
            this.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
        }

        public void ApplyMovementMode(string mode)
        {
            movementMode = mode;
            var moveBtn = FindName("MoveButton") as System.Windows.Controls.Button;
            if (moveBtn != null)
            {
                moveBtn.Visibility = mode == "button" ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void ApplySettingsButtonVisibility()
        {
            var settingsButton = FindName("SettingsButton") as System.Windows.Controls.Button;
            if (settingsButton != null)
            {
                settingsButton.Visibility = showSettingsButton ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public static string NormalizeSearchVisibilityMode(string? mode)
        {
            if (string.Equals(mode, SearchVisibilityExpanded, StringComparison.OrdinalIgnoreCase))
            {
                return SearchVisibilityExpanded;
            }

            if (string.Equals(mode, SearchVisibilityHidden, StringComparison.OrdinalIgnoreCase))
            {
                return SearchVisibilityHidden;
            }

            return SearchVisibilityAlways;
        }

        public static string NormalizeViewMode(string? mode)
        {
            if (string.Equals(mode, ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return ViewModeDetails;
            }

            if (string.Equals(mode, ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return ViewModePhotos;
            }

            return ViewModeIcons;
        }

        public static List<string> NormalizeMetadataOrder(IEnumerable<string>? order)
        {
            var normalized = new List<string>(DefaultMetadataOrder.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (order != null)
            {
                foreach (var item in order)
                {
                    string key = NormalizeMetadataKey(item);
                    if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    {
                        continue;
                    }

                    normalized.Add(key);
                }
            }

            foreach (var key in DefaultMetadataOrder)
            {
                if (seen.Add(key))
                {
                    normalized.Add(key);
                }
            }

            return normalized;
        }

        private static string NormalizeMetadataKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            if (string.Equals(key, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataType;
            }

            if (string.Equals(key, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataSize;
            }

            if (string.Equals(key, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataCreated;
            }

            if (string.Equals(key, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataModified;
            }

            if (string.Equals(key, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataDimensions;
            }

            return string.Empty;
        }

        public void SetSearchVisibilityMode(string? mode)
        {
            string normalized = NormalizeSearchVisibilityMode(mode);
            bool changed = !string.Equals(searchVisibilityMode, normalized, StringComparison.OrdinalIgnoreCase);
            searchVisibilityMode = normalized;

            if (changed && string.Equals(searchVisibilityMode, SearchVisibilityHidden, StringComparison.OrdinalIgnoreCase))
            {
                ResetSearchState(clearSearchBox: true);
                RestoreUnfilteredPanelItems();
            }

            ApplySearchVisibility();
        }

        private bool ShouldShowSearch()
        {
            string normalized = NormalizeSearchVisibilityMode(searchVisibilityMode);
            if (string.Equals(normalized, SearchVisibilityHidden, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(normalized, SearchVisibilityExpanded, StringComparison.OrdinalIgnoreCase))
            {
                return isContentVisible;
            }

            return true;
        }

        private void ApplySearchVisibility(bool animate = true)
        {
            bool showSearch = ShouldShowSearch();
            bool shouldAnimate = animate && IsLoaded;

            if (SearchColumn != null)
            {
                SearchColumn.Width = GridLength.Auto;
            }

            if (SearchSpacerColumn != null)
            {
                SearchSpacerColumn.Width = GridLength.Auto;
            }

            if (SearchContainer == null || SearchSpacer == null)
            {
                return;
            }

            GetAdaptiveHeaderSearchWidths(out double adaptiveSearchWidth, out double adaptiveSpacerWidth);
            double targetSearchWidth = showSearch ? adaptiveSearchWidth : 0;
            double targetSpacerWidth = showSearch ? adaptiveSpacerWidth : 0;

            if (!shouldAnimate)
            {
                SearchContainer.BeginAnimation(FrameworkElement.WidthProperty, null);
                SearchContainer.BeginAnimation(UIElement.OpacityProperty, null);
                SearchSpacer.BeginAnimation(FrameworkElement.WidthProperty, null);

                SearchContainer.Width = targetSearchWidth;
                SearchContainer.Opacity = showSearch ? 1 : 0;
                SearchContainer.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;

                SearchSpacer.Width = targetSpacerWidth;
                SearchSpacer.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            double currentSearchWidth = ResolveCurrentWidth(SearchContainer, targetSearchWidth);
            double currentSpacerWidth = ResolveCurrentWidth(SearchSpacer, targetSpacerWidth);
            double targetOpacity = showSearch ? 1 : 0;

            SearchContainer.BeginAnimation(FrameworkElement.WidthProperty, null);
            SearchContainer.BeginAnimation(UIElement.OpacityProperty, null);
            SearchSpacer.BeginAnimation(FrameworkElement.WidthProperty, null);

            SearchContainer.Width = currentSearchWidth;
            SearchSpacer.Width = currentSpacerWidth;

            if (showSearch)
            {
                SearchContainer.Visibility = Visibility.Visible;
                SearchSpacer.Visibility = Visibility.Visible;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(SearchVisibilityAnimationMs);

            var widthAnimation = new DoubleAnimation
            {
                To = targetSearchWidth,
                Duration = duration,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            widthAnimation.Completed += (_, _) =>
            {
                SearchContainer.BeginAnimation(FrameworkElement.WidthProperty, null);
                SearchContainer.Width = targetSearchWidth;
                SearchContainer.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
            };

            var spacerAnimation = new DoubleAnimation
            {
                To = targetSpacerWidth,
                Duration = duration,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            spacerAnimation.Completed += (_, _) =>
            {
                SearchSpacer.BeginAnimation(FrameworkElement.WidthProperty, null);
                SearchSpacer.Width = targetSpacerWidth;
                SearchSpacer.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
            };

            var opacityAnimation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(showSearch ? SearchVisibilityAnimationMs : SearchVisibilityAnimationMs - 40),
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            opacityAnimation.Completed += (_, _) =>
            {
                SearchContainer.BeginAnimation(UIElement.OpacityProperty, null);
                SearchContainer.Opacity = targetOpacity;
            };

            SearchContainer.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
            SearchSpacer.BeginAnimation(FrameworkElement.WidthProperty, spacerAnimation, HandoffBehavior.SnapshotAndReplace);
            SearchContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        private void GetAdaptiveHeaderSearchWidths(out double searchWidth, out double spacerWidth)
        {
            double available = GetHeaderSearchAvailableWidth();
            if (available <= 0.5)
            {
                searchWidth = 0;
                spacerWidth = 0;
                return;
            }

            searchWidth = Math.Min(HeaderSearchWidth, available);
            spacerWidth = Math.Min(HeaderSearchSpacerWidth, Math.Max(0, available - searchWidth));
        }

        private double GetHeaderSearchAvailableWidth()
        {
            double headerWidth = 0;
            if (HeaderBar != null && HeaderBar.ActualWidth > 0)
            {
                headerWidth = HeaderBar.ActualWidth;
            }
            else if (ActualWidth > 0)
            {
                headerWidth = ActualWidth;
            }

            if (headerWidth <= 1)
            {
                return HeaderSearchWidth + HeaderSearchSpacerWidth;
            }

            double reservedWidth = HeaderHorizontalPadding + HeaderCoreFixedWidth;
            reservedWidth += GetVisibleElementWidth(MoveButton, 34);
            reservedWidth += GetVisibleElementWidth(SettingsButton, 24);

            return Math.Max(0, headerWidth - reservedWidth - HeaderTitleMinWidth);
        }

        private static double GetVisibleElementWidth(FrameworkElement? element, double fallbackWidth)
        {
            if (element == null || element.Visibility != Visibility.Visible)
            {
                return 0;
            }

            double width = element.ActualWidth;
            if (width <= 0)
            {
                if (!double.IsNaN(element.Width) && !double.IsInfinity(element.Width) && element.Width > 0)
                {
                    width = element.Width;
                }
                else
                {
                    width = fallbackWidth;
                }
            }

            return Math.Max(0, width + element.Margin.Left + element.Margin.Right);
        }

        private static double ResolveCurrentWidth(FrameworkElement element, double fallback)
        {
            if (!double.IsNaN(element.Width) && !double.IsInfinity(element.Width))
            {
                return Math.Max(0, element.Width);
            }

            if (element.ActualWidth > 0)
            {
                return element.ActualWidth;
            }

            return fallback;
        }

        private void Collapse_Click(object sender, RoutedEventArgs e)
        {
            ToggleCollapseAnimated();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BurgerMenu_Click(object sender, RoutedEventArgs e)
        {
            var settings = new PanelSettings(this);
            settings.Owner = this;
            settings.ShowDialog();
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;
                zoomFactor += (e.Delta > 0) ? 0.1 : -0.1;
                zoomFactor = Math.Max(0.7, Math.Min(2.0, zoomFactor));
                ApplyZoom();
                return;
            }

            if (ContentContainer != null)
            {
                double newOffset = ContentContainer.VerticalOffset - (e.Delta * 0.5);
                newOffset = Math.Max(0, Math.Min(newOffset, ContentContainer.ScrollableHeight));
                ContentContainer.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
