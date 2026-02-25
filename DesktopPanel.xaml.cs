using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Input;
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
        public static bool ExpandOnHover = true;
        public string assignedPresetName = "";
        public bool showHiddenItems = false;
        public bool expandOnHover = true;
        public bool openFoldersExternally = false;
        public bool showSettingsButton = true;
        public string defaultFolderPath = "";
        public string movementMode = "titlebar";
        private bool _hoverExpanded = false;
        private bool _isCollapseAnimationRunning = false;
        private bool _isBottomAnchored = false;
        private bool _isBoundsCorrectionInProgress = false;
        private bool _isDragMoveActive = false;
        private CancellationTokenSource? _searchCts;
        private AppearanceSettings? _currentAppearance;
        private EventHandler? _headerCornerAnimationHandler;
        private double _headerTopCornerRadius = 14;
        private double _headerBottomCornerRadius = 0;
        private const double BottomAnchorTolerance = 3.0;
        private static readonly Thickness ExpandedChromeBorderThickness = new Thickness(1);
        private static readonly Thickness CollapsedChromeBorderThickness = new Thickness(0);
        private static readonly Thickness ExpandedChromePadding = new Thickness(1);
        private static readonly Thickness CollapsedChromePadding = new Thickness(0);
        public PanelKind PanelType { get; set; } = PanelKind.None;
        public string PanelId { get; set; } = $"panel:{Guid.NewGuid():N}";
        public List<string> PinnedItems { get; } = new List<string>();
        public double zoomFactor = 1.0;

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
                MainWindow.AppearanceChanged -= OnAppearanceChanged;
                if (!MainWindow.IsExiting)
                {
                    MainWindow.MarkPanelHidden(this);
                }
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            };

            this.Loaded += DesktopPanel_Loaded;
            this.LocationChanged += DesktopPanel_LocationChanged;
            this.SizeChanged += DesktopPanel_SizeChanged;
            this.MouseEnter += Window_MouseEnter;
            this.MouseLeave += Window_MouseLeave;
            ApplySettingsButtonVisibility();
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
            double headerHeight = (HeaderBar != null && HeaderBar.ActualHeight > 0) ? HeaderBar.ActualHeight : 52;
            double padding = CollapsedChromePadding.Top + CollapsedChromePadding.Bottom;
            double border = CollapsedChromeBorderThickness.Top + CollapsedChromeBorderThickness.Bottom;
            return Math.Max(headerHeight, headerHeight + padding + border);
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

        private void SetContentLayerVisibility(bool visible)
        {
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (ContentFrame != null)
            {
                ContentFrame.Visibility = visibility;
            }
            if (ContentContainer != null)
            {
                ContentContainer.Visibility = visibility;
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
            ResizeMode = collapsed ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            if (PanelChrome != null)
            {
                PanelChrome.BorderThickness = collapsed ? CollapsedChromeBorderThickness : ExpandedChromeBorderThickness;
                PanelChrome.Padding = collapsed ? CollapsedChromePadding : ExpandedChromePadding;
            }
            if (HeaderBar != null)
            {
                HeaderBar.BorderThickness = collapsed ? new Thickness(0) : new Thickness(0, 0, 0, 1);
            }

            double targetBottomRadius = collapsed ? _headerTopCornerRadius : 0;
            if (animateCorners)
            {
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                AnimateHeaderBottomCorners(targetBottomRadius, duration ?? TimeSpan.FromMilliseconds(200), ease);
            }
            else
            {
                StopHeaderCornerAnimation();
                ApplyHeaderCornerRadius(targetBottomRadius);
            }
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
            if (_isBottomAnchored) return true;

            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double currentBottom = Top + currentHeight;
            double workBottom = GetWorkAreaForPanel().Bottom;
            if (currentBottom >= workBottom - BottomAnchorTolerance) return true;

            return Top + Math.Max(collapsedHeight, expandedHeight) >= workBottom - BottomAnchorTolerance;
        }

        private bool ShouldUseBottomAnchorOnExpand(double collapsedHeight)
        {
            if (_isBottomAnchored) return true;

            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            if (IsBottomAligned(Top, currentHeight)) return true;

            return IsBottomAligned(baseTopPosition, collapsedHeight);
        }

        private void SyncAnchoringFromCurrentBounds()
        {
            double collapsedHeight = GetCollapsedHeight();
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;

            if (isContentVisible)
            {
                if (IsBottomAligned(Top, currentHeight))
                {
                    _isBottomAnchored = true;
                    collapsedTopPosition = GetBottomAnchoredCollapsedTop(collapsedHeight);
                    baseTopPosition = collapsedTopPosition;
                }
                else
                {
                    _isBottomAnchored = false;
                    baseTopPosition = Top;
                    collapsedTopPosition = Top;
                }

                expandedHeight = Math.Max(collapsedHeight, currentHeight);
            }
            else
            {
                collapsedTopPosition = Top;
                _isBottomAnchored = IsBottomAligned(Top, currentHeight);
                baseTopPosition = collapsedTopPosition;
            }
        }

        private void DesktopPanel_Loaded(object sender, RoutedEventArgs e)
        {
            EnsurePanelInsideVerticalWorkArea();
            SyncAnchoringFromCurrentBounds();
            ApplyCollapsedVisualState(!isContentVisible, animateCorners: false);
        }

        private void DesktopPanel_LocationChanged(object? sender, EventArgs e)
        {
            if (_isDragMoveActive) return;

            if (!_isCollapseAnimationRunning)
            {
                EnsurePanelInsideVerticalWorkArea();
                SyncAnchoringFromCurrentBounds();
            }
            MainWindow.SaveSettings();
        }

        private void DesktopPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isDragMoveActive) return;

            if (!_isCollapseAnimationRunning)
            {
                EnsurePanelInsideVerticalWorkArea();
                SyncAnchoringFromCurrentBounds();
            }
            MainWindow.SaveSettings();
        }

        private void StopPanelAnimations()
        {
            BeginAnimation(TopProperty, null);
            BeginAnimation(HeightProperty, null);
            StopHeaderCornerAnimation();
            _isCollapseAnimationRunning = false;
        }

        public void ForceCollapseState(bool isCollapsed)
        {
            StopPanelAnimations();
            double collapsedHeight = GetCollapsedHeight();

            if (isCollapsed)
            {
                if (isContentVisible)
                {
                    double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                    expandedHeight = Math.Max(collapsedHeight, currentHeight);
                }

                bool anchorToBottom = ShouldAnchorToBottom(collapsedHeight);
                double targetTop = anchorToBottom ? GetBottomAnchoredCollapsedTop(collapsedHeight) : baseTopPosition;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, collapsedHeight, targetTop);

                this.Top = targetTop;
                this.Height = collapsedHeight;
                SetContentLayerVisibility(false);
                isContentVisible = false;
                _hoverExpanded = false;
                collapsedTopPosition = targetTop;
                _isBottomAnchored = anchorToBottom;
                baseTopPosition = collapsedTopPosition;
                ApplyCollapsedVisualState(collapsed: true, animateCorners: false);
            }
            else
            {
                double targetHeight = Math.Max(collapsedHeight, expandedHeight);
                bool anchorToBottom = ShouldUseBottomAnchorOnExpand(collapsedHeight);
                double targetTop = anchorToBottom
                    ? GetBottomAnchoredCollapsedTop(collapsedHeight) + collapsedHeight - targetHeight
                    : baseTopPosition;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);

                this.Top = targetTop;
                this.Height = targetHeight;
                SetContentLayerVisibility(true);
                isContentVisible = true;
                _isBottomAnchored = anchorToBottom;
                if (anchorToBottom)
                {
                    collapsedTopPosition = GetBottomAnchoredCollapsedTop(collapsedHeight);
                    baseTopPosition = collapsedTopPosition;
                }
                else
                {
                    baseTopPosition = this.Top;
                    collapsedTopPosition = this.Top;
                }

                ApplyCollapsedVisualState(collapsed: false, animateCorners: false);
            }
        }

        private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (movementMode == "locked") return;
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                _isDragMoveActive = true;
                try
                {
                    this.DragMove();
                }
                finally
                {
                    _isDragMoveActive = false;
                }
                EnsurePanelInsideVerticalWorkArea();
                SyncAnchoringFromCurrentBounds();
                MainWindow.SaveSettings();
            }
        }

        private DateTime _lastHeaderClickTime = DateTime.MinValue;
        private Point _lastHeaderClickPos;

        private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var now = DateTime.UtcNow;
            var pos = e.GetPosition(HeaderBar);

            bool isDoubleClick = (now - _lastHeaderClickTime).TotalMilliseconds <= 500
                && Math.Abs(pos.X - _lastHeaderClickPos.X) <= 4
                && Math.Abs(pos.Y - _lastHeaderClickPos.Y) <= 4;

            _lastHeaderClickTime = now;
            _lastHeaderClickPos = pos;

            if (isDoubleClick)
            {
                _lastHeaderClickTime = DateTime.MinValue;
                e.Handled = true;
                ToggleCollapseAnimated();
                return;
            }

            if (movementMode == "locked") return;

            if (movementMode == "titlebar")
            {
                _isDragMoveActive = true;
                try
                {
                    this.DragMove();
                }
                finally
                {
                    _isDragMoveActive = false;
                }
                EnsurePanelInsideVerticalWorkArea();
                SyncAnchoringFromCurrentBounds();
                MainWindow.SaveSettings();
            }
        }

        private void ToggleCollapseAnimated()
        {
            if (_isCollapseAnimationRunning) return;

            StopPanelAnimations();
            _isCollapseAnimationRunning = true;

            var duration = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            double collapsedHeight = GetCollapsedHeight();

            if (isContentVisible)
            {
                double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                expandedHeight = Math.Max(collapsedHeight, currentHeight);
                double targetHeight = collapsedHeight;
                bool anchorToBottom = ShouldAnchorToBottom(collapsedHeight);
                double targetTop = anchorToBottom ? GetBottomAnchoredCollapsedTop(collapsedHeight) : baseTopPosition;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);

                ApplyCollapsedVisualState(collapsed: true, animateCorners: true, duration);

                Action onComplete = () =>
                {
                    this.Top = targetTop;
                    this.Height = targetHeight;
                    SetContentLayerVisibility(false);
                    isContentVisible = false;
                    _hoverExpanded = false;
                    collapsedTopPosition = targetTop;
                    _isBottomAnchored = anchorToBottom;
                    baseTopPosition = collapsedTopPosition;
                    ApplyCollapsedVisualState(collapsed: true, animateCorners: false);
                    _isCollapseAnimationRunning = false;
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
                SetContentLayerVisibility(true);
                double targetHeight = Math.Max(collapsedHeight, expandedHeight);
                bool anchorToBottom = ShouldUseBottomAnchorOnExpand(collapsedHeight);
                double targetTop = anchorToBottom
                    ? GetBottomAnchoredCollapsedTop(collapsedHeight) + collapsedHeight - targetHeight
                    : baseTopPosition;
                Rect workArea = GetWorkAreaForPanel();
                targetTop = ClampTopToWorkArea(workArea, targetHeight, targetTop);

                ApplyCollapsedVisualState(collapsed: false, animateCorners: true, duration);

                Action onComplete = () =>
                {
                    this.Top = targetTop;
                    this.Height = targetHeight;
                    isContentVisible = true;
                    _isBottomAnchored = anchorToBottom;
                    if (anchorToBottom)
                    {
                        collapsedTopPosition = GetBottomAnchoredCollapsedTop(collapsedHeight);
                        baseTopPosition = collapsedTopPosition;
                    }
                    else
                    {
                        baseTopPosition = this.Top;
                        collapsedTopPosition = this.Top;
                    }
                    ApplyCollapsedVisualState(collapsed: false, animateCorners: false);
                    _isCollapseAnimationRunning = false;
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
