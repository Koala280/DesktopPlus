using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.VisualBasic.FileIO;
using DataFormats = System.Windows.DataFormats;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using WpfListBox = System.Windows.Controls.ListBox;
using VBFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private const string InternalListReorderFormat = "DesktopPlus.InternalListReorder";
        private const string InternalPanelDragSourceFormat = "DesktopPlus.InternalPanelDragSource";
        private const string PreferredDropEffectFormat = "Preferred DropEffect";
        private const int PreferredDropEffectCopy = 1;
        private const int PreferredDropEffectMove = 2;
        private bool _isRubberBandSelecting;
        private bool _rubberBandAdditive;
        private Point _rubberBandStartPoint;
        private bool _internalReorderDropHandled;
        private readonly HashSet<object> _rubberBandSelectionSnapshot = new HashSet<object>();
        private ListBoxItem? _renameEditItem;
        private System.Windows.Controls.TextBox? _renameEditBox;
        private TextBlock? _renameEditOriginalLabel;
        private System.Windows.Controls.Panel? _renameEditOriginalHostPanel;
        private int _renameEditOriginalHostIndex = -1;
        private string _renameOriginalPath = string.Empty;
        private string _renameOriginalDisplayName = string.Empty;
        private ListBoxItem? _fileDragStartItem;
        private bool _fileDragStartWithModifiers;
        private ListBoxItem? _lastRenameClickItem;
        private DateTime _lastRenameClickUtc = DateTime.MinValue;
        private Point _lastRenameClickPosition;
        private bool _lastRenameClickWasOnName;
        private bool _isNormalizingFileListSelection;
        private readonly SemaphoreSlim _incomingFileImportSemaphore = new SemaphoreSlim(1, 1);
        private const double FileListReorderPreviewAnimationMs = 190.0;
        private int _fileListReorderInsertIndex = -1;
        private ListBoxItem? _fileListReorderSourceItem;
        private List<Rect>? _fileListReorderSlots;

        private sealed class InternalListReorderPayload
        {
            public string SourcePanelId { get; init; } = string.Empty;
            public ListBoxItem? SourceItem { get; init; }
        }

        private sealed class IncomingFileImportResult
        {
            public bool FolderChanged { get; set; }
            public List<string> ListItemsToAdd { get; } = new List<string>();
            public List<string> Failures { get; } = new List<string>();
        }

        private List<ListBoxItem> GetVisibleReorderableFileListItems()
        {
            return FileList?.Items
                .OfType<ListBoxItem>()
                .Where(item => item.Visibility == Visibility.Visible && !IsParentNavigationItem(item))
                .ToList() ?? new List<ListBoxItem>();
        }

        private static bool IsPointWithinElementBounds(FrameworkElement? element, Point point, double tolerance = 0)
        {
            if (element == null)
            {
                return false;
            }

            double width = element.ActualWidth;
            double height = element.ActualHeight;
            return point.X >= -tolerance &&
                   point.Y >= -tolerance &&
                   point.X <= width + tolerance &&
                   point.Y <= height + tolerance;
        }

        private Rect GetFileListItemLayoutBounds(ListBoxItem item)
        {
            if (FileList == null || item == null)
            {
                return Rect.Empty;
            }

            try
            {
                Point topLeft = item.TransformToAncestor(FileList).Transform(new Point(0, 0));
                TranslateTransform? transform = GetFileListItemTransform(item, createIfMissing: false);
                if (transform != null)
                {
                    topLeft.X -= transform.X;
                    topLeft.Y -= transform.Y;
                }

                return new Rect(topLeft, new System.Windows.Size(item.ActualWidth, item.ActualHeight));
            }
            catch
            {
                return Rect.Empty;
            }
        }

        private static double GetSquaredDistanceToRect(Point point, Rect rect)
        {
            double dx = 0;
            if (point.X < rect.Left)
            {
                dx = rect.Left - point.X;
            }
            else if (point.X > rect.Right)
            {
                dx = point.X - rect.Right;
            }

            double dy = 0;
            if (point.Y < rect.Top)
            {
                dy = rect.Top - point.Y;
            }
            else if (point.Y > rect.Bottom)
            {
                dy = point.Y - rect.Bottom;
            }

            return (dx * dx) + (dy * dy);
        }

        private bool ShouldInsertAfterFileListTarget(Rect targetBounds, Point mousePositionInFileList)
        {
            string normalizedViewMode = NormalizeViewMode(viewMode);
            if (string.Equals(normalizedViewMode, ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return mousePositionInFileList.Y > targetBounds.Top + (targetBounds.Height / 2.0);
            }

            if (mousePositionInFileList.Y < targetBounds.Top)
            {
                return false;
            }

            if (mousePositionInFileList.Y > targetBounds.Bottom)
            {
                return true;
            }

            return mousePositionInFileList.X > targetBounds.Left + (targetBounds.Width / 2.0);
        }

        private bool TryGetFileListReorderInsertIndex(ListBoxItem sourceItem, Point mousePositionInFileList, out int insertIndex)
        {
            insertIndex = -1;

            if (FileList == null || sourceItem == null)
            {
                return false;
            }

            var reorderableItems = GetVisibleReorderableFileListItems();
            int sourceIndex = reorderableItems.IndexOf(sourceItem);
            if (sourceIndex < 0)
            {
                return false;
            }

            if (reorderableItems.Count <= 1)
            {
                insertIndex = sourceIndex;
                return true;
            }

            // Cache slot positions once (before any animations) to avoid animation-affected
            // positions causing the insert index to oscillate back and forth.
            // Same approach as tab reorder which uses accumulated widths instead of TranslatePoint.
            if (_fileListReorderSlots == null || _fileListReorderSlots.Count != reorderableItems.Count)
            {
                _fileListReorderSlots = reorderableItems.Select(GetFileListItemLayoutBounds).ToList();
            }

            Rect sourceBounds = _fileListReorderSlots[sourceIndex];
            Rect occupiedBounds = sourceBounds;
            bool hasOccupiedBounds = !sourceBounds.IsEmpty;
            if (!sourceBounds.IsEmpty && sourceBounds.Contains(mousePositionInFileList))
            {
                insertIndex = sourceIndex;
                return true;
            }

            for (int i = 0; i < reorderableItems.Count; i++)
            {
                if (ReferenceEquals(reorderableItems[i], sourceItem))
                {
                    continue;
                }

                Rect candidateBounds = _fileListReorderSlots[i];
                if (!candidateBounds.IsEmpty)
                {
                    occupiedBounds = hasOccupiedBounds ? Rect.Union(occupiedBounds, candidateBounds) : candidateBounds;
                    hasOccupiedBounds = true;
                }

                if (candidateBounds.IsEmpty || !candidateBounds.Contains(mousePositionInFileList))
                {
                    continue;
                }

                if (i > sourceIndex)
                {
                    insertIndex = i + 1;
                }
                else
                {
                    insertIndex = i;
                }

                insertIndex = Math.Max(0, Math.Min(insertIndex, reorderableItems.Count));
                return true;
            }

            if (ReferenceEquals(_fileListReorderSourceItem, sourceItem) &&
                _fileListReorderInsertIndex >= 0 &&
                hasOccupiedBounds &&
                occupiedBounds.Contains(mousePositionInFileList))
            {
                insertIndex = _fileListReorderInsertIndex;
                return true;
            }

            // Fallback: find nearest item using cached slot positions
            int nearestIndex = -1;
            double nearestDist = double.MaxValue;
            for (int i = 0; i < reorderableItems.Count; i++)
            {
                if (ReferenceEquals(reorderableItems[i], sourceItem))
                {
                    continue;
                }

                Rect bounds = _fileListReorderSlots[i];
                if (bounds.IsEmpty)
                {
                    continue;
                }

                double dist = GetSquaredDistanceToRect(mousePositionInFileList, bounds);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIndex = i;
                }
            }

            if (nearestIndex < 0)
            {
                insertIndex = sourceIndex;
                return true;
            }

            Rect targetBounds = _fileListReorderSlots[nearestIndex];
            if (targetBounds.IsEmpty)
            {
                return false;
            }

            insertIndex = nearestIndex + (ShouldInsertAfterFileListTarget(targetBounds, mousePositionInFileList) ? 1 : 0);
            insertIndex = Math.Max(0, Math.Min(insertIndex, reorderableItems.Count));
            return true;
        }

        private void ApplyFileListReorderPreview(ListBoxItem sourceItem, int insertIndex)
        {
            var reorderableItems = GetVisibleReorderableFileListItems();
            int sourceIndex = reorderableItems.IndexOf(sourceItem);
            if (sourceIndex < 0)
            {
                ClearFileListReorderPreview();
                return;
            }

            insertIndex = Math.Max(0, Math.Min(insertIndex, reorderableItems.Count));
            if (ReferenceEquals(_fileListReorderSourceItem, sourceItem) &&
                _fileListReorderInsertIndex == insertIndex)
            {
                return;
            }

            _fileListReorderSourceItem = sourceItem;
            _fileListReorderInsertIndex = insertIndex;

            // Use cached slot positions to avoid reading animation-affected layout bounds.
            // Offsets are computed purely from the stable original positions.
            if (_fileListReorderSlots == null || _fileListReorderSlots.Count != reorderableItems.Count)
            {
                _fileListReorderSlots = reorderableItems.Select(GetFileListItemLayoutBounds).ToList();
            }

            // Build preview order as indices: remove source, insert at target
            var previewOrder = Enumerable.Range(0, reorderableItems.Count).ToList();
            previewOrder.RemoveAt(sourceIndex);
            int adjustedInsertIndex = insertIndex > sourceIndex ? insertIndex - 1 : insertIndex;
            adjustedInsertIndex = Math.Max(0, Math.Min(adjustedInsertIndex, previewOrder.Count));
            previewOrder.Insert(adjustedInsertIndex, sourceIndex);

            foreach (ListBoxItem item in reorderableItems)
            {
                item.Opacity = ReferenceEquals(item, sourceItem) ? 0.82 : 1.0;
                System.Windows.Controls.Panel.SetZIndex(item, ReferenceEquals(item, sourceItem) ? 3 : 0);
            }

            for (int targetSlot = 0; targetSlot < previewOrder.Count; targetSlot++)
            {
                int originalSlot = previewOrder[targetSlot];
                Rect originalPos = _fileListReorderSlots[originalSlot];
                Rect targetPos = _fileListReorderSlots[targetSlot];
                ApplyFileListItemOffset(
                    reorderableItems[originalSlot],
                    targetPos.Left - originalPos.Left,
                    targetPos.Top - originalPos.Top);
            }
        }

        private void ClearFileListReorderPreview()
        {
            foreach (ListBoxItem item in GetVisibleReorderableFileListItems())
            {
                ApplyFileListItemOffset(item, 0, 0);
                item.Opacity = 1.0;
                System.Windows.Controls.Panel.SetZIndex(item, 0);
            }

            _fileListReorderInsertIndex = -1;
            _fileListReorderSourceItem = null;
            _fileListReorderSlots = null;
        }

        private static void ClearFileListReorderPreviewAcrossPanels()
        {
            foreach (DesktopPanel panel in System.Windows.Application.Current?.Windows.OfType<DesktopPanel>() ?? Enumerable.Empty<DesktopPanel>())
            {
                panel.ClearFileListReorderPreview();
            }
        }

        private static void ApplyFileListItemOffset(ListBoxItem item, double targetX, double targetY)
        {
            TranslateTransform? transform = GetFileListItemTransform(item);
            if (transform == null)
            {
                return;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(FileListReorderPreviewAnimationMs))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(FileListReorderPreviewAnimationMs))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static TranslateTransform? GetFileListItemTransform(ListBoxItem item, bool createIfMissing = true)
        {
            if (item.RenderTransform is TranslateTransform translate)
            {
                return translate;
            }

            if (item.RenderTransform is TransformGroup group)
            {
                TranslateTransform? existing = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (existing != null || !createIfMissing)
                {
                    return existing;
                }

                var appended = new TranslateTransform();
                group.Children.Add(appended);
                return appended;
            }

            if (!createIfMissing)
            {
                return null;
            }

            var created = new TranslateTransform();
            item.RenderTransform = created;
            return created;
        }

        private bool TryReorderFileListItem(ListBoxItem sourceItem, int insertIndex)
        {
            if (FileList == null || sourceItem == null)
            {
                return false;
            }

            var reorderableItems = GetVisibleReorderableFileListItems();
            int sourceIndex = reorderableItems.IndexOf(sourceItem);
            if (sourceIndex < 0)
            {
                return false;
            }

            insertIndex = Math.Max(0, Math.Min(insertIndex, reorderableItems.Count));
            var reorderedItems = reorderableItems.ToList();
            reorderedItems.RemoveAt(sourceIndex);

            int adjustedInsertIndex = insertIndex > sourceIndex ? insertIndex - 1 : insertIndex;
            adjustedInsertIndex = Math.Max(0, Math.Min(adjustedInsertIndex, reorderedItems.Count));
            if (adjustedInsertIndex == sourceIndex)
            {
                return false;
            }

            ListBoxItem? insertBeforeItem = adjustedInsertIndex < reorderedItems.Count
                ? reorderedItems[adjustedInsertIndex]
                : null;

            FileList.Items.Remove(sourceItem);
            if (insertBeforeItem != null)
            {
                int fullInsertIndex = FileList.Items.IndexOf(insertBeforeItem);
                if (fullInsertIndex < 0)
                {
                    FileList.Items.Add(sourceItem);
                }
                else
                {
                    FileList.Items.Insert(fullInsertIndex, sourceItem);
                }
            }
            else
            {
                FileList.Items.Add(sourceItem);
            }

            FileList.SelectedItem = sourceItem;
            sourceItem.IsSelected = true;
            return true;
        }

        private static bool IsToggleModifierPressed()
        {
            var modifiers = Keyboard.Modifiers;
            return modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt);
        }

        private static bool IsRangeModifierPressed()
        {
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        }

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private static bool IsTextInputFocused()
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase ||
                Keyboard.FocusedElement is PasswordBox ||
                Keyboard.FocusedElement is System.Windows.Controls.RichTextBox)
            {
                return true;
            }

            if (Keyboard.FocusedElement is DependencyObject focused)
            {
                return FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(focused) != null ||
                       FindAncestor<System.Windows.Controls.RichTextBox>(focused) != null;
            }

            return false;
        }

        private static bool IsFileSystemPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return File.Exists(path) || Directory.Exists(path);
        }

        private List<string> GetSelectedFileSystemPaths()
        {
            if (FileList == null) return new List<string>();

            return FileList.SelectedItems
                .OfType<ListBoxItem>()
                .Where(item => !IsParentNavigationItem(item))
                .Select(item => item.Tag as string)
                .Where(IsFileSystemPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
        }

        private void NormalizeFileListSelection()
        {
            if (_isNormalizingFileListSelection || FileList == null)
            {
                return;
            }

            var blockedItems = FileList.SelectedItems
                .OfType<ListBoxItem>()
                .Where(IsParentNavigationItem)
                .ToList();

            if (blockedItems.Count == 0)
            {
                return;
            }

            _isNormalizingFileListSelection = true;
            try
            {
                foreach (var item in blockedItems)
                {
                    item.IsSelected = false;
                }
            }
            finally
            {
                _isNormalizingFileListSelection = false;
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NormalizeFileListSelection();
        }

        private void RememberRenameClickCandidate(ListBoxItem? item, Point position, bool wasOnName)
        {
            _lastRenameClickItem = item;
            _lastRenameClickUtc = DateTime.UtcNow;
            _lastRenameClickPosition = position;
            _lastRenameClickWasOnName = wasOnName;
        }

        private static DependencyObject? GetDependencyParent(DependencyObject? current)
        {
            if (current == null)
            {
                return null;
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(current);
            }

            if (current is FrameworkContentElement frameworkContent)
            {
                return frameworkContent.Parent;
            }

            return LogicalTreeHelper.GetParent(current);
        }

        private bool IsRenameClickOnItemName(ListBoxItem item, DependencyObject? source)
        {
            if (source == null || !TryGetItemNameLabel(item, out var label))
            {
                return false;
            }

            DependencyObject? current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, label))
                {
                    return true;
                }

                current = GetDependencyParent(current);
            }

            return false;
        }

        private bool TryBeginRenameBySlowSecondClick(ListBoxItem clickedItem, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1)
            {
                return false;
            }

            if (FileList == null || _renameEditBox != null)
            {
                return false;
            }

            Point clickPos = e.GetPosition(clickedItem);
            DateTime now = DateTime.UtcNow;
            bool clickOnName = IsRenameClickOnItemName(clickedItem, e.OriginalSource as DependencyObject);

            bool selectedSingleItem = FileList.SelectedItems.Count == 1 &&
                                      ReferenceEquals(FileList.SelectedItem, clickedItem);

            bool sameItem = ReferenceEquals(_lastRenameClickItem, clickedItem);
            double elapsedMs = (now - _lastRenameClickUtc).TotalMilliseconds;

            int minDelayMs = System.Windows.Forms.SystemInformation.DoubleClickTime + 40;
            const int maxDelayMs = 2200;

            RememberRenameClickCandidate(clickedItem, clickPos, clickOnName);

            if (!selectedSingleItem ||
                !sameItem ||
                !clickOnName ||
                elapsedMs < minDelayMs ||
                elapsedMs > maxDelayMs)
            {
                return false;
            }

            if (!TryGetRenameItemContext(clickedItem, out _, out _, out _, out _))
            {
                return false;
            }

            if (!BeginInlineRename(clickedItem))
            {
                return false;
            }

            _lastRenameClickItem = null;
            _lastRenameClickUtc = DateTime.MinValue;
            _lastRenameClickWasOnName = false;
            return true;
        }

        private bool CanRenameSelection()
        {
            if (FileList?.SelectedItems.Count != 1 || FileList.SelectedItem is not ListBoxItem item)
            {
                return false;
            }

            return TryGetRenameItemContext(item, out _, out _, out _, out _);
        }

        private bool TryBeginRenameSelection()
        {
            if (FileList?.SelectedItems.Count != 1 || FileList.SelectedItem is not ListBoxItem item)
            {
                return false;
            }

            return BeginInlineRename(item);
        }

        private bool TryGetRenameItemContext(
            ListBoxItem item,
            out string path,
            out System.Windows.Controls.Panel hostPanel,
            out TextBlock label,
            out int labelIndex)
        {
            path = string.Empty;
            hostPanel = null!;
            label = null!;
            labelIndex = -1;

            if (PanelType == PanelKind.RecycleBin)
            {
                return false;
            }

            if (item == null || item.Tag is not string itemPath || string.IsNullOrWhiteSpace(itemPath))
            {
                return false;
            }

            if (IsParentNavigationItem(item))
            {
                return false;
            }

            if (!File.Exists(itemPath) && !Directory.Exists(itemPath))
            {
                return false;
            }

            if (!TryGetEditableNameHost(item, out var editableHost, out var editableLabel, out int editableIndex))
            {
                return false;
            }

            path = itemPath;
            hostPanel = editableHost;
            label = editableLabel;
            labelIndex = editableIndex;
            return true;
        }

        private bool BeginInlineRename(ListBoxItem item)
        {
            if (_renameEditBox != null)
            {
                return false;
            }

            if (!TryGetRenameItemContext(item, out string path, out System.Windows.Controls.Panel hostPanel, out TextBlock label, out int labelIndex))
            {
                return false;
            }

            string originalDisplayName = string.IsNullOrWhiteSpace(label.Text)
                ? GetDisplayNameForPath(path)
                : label.Text.Trim();
            if (string.IsNullOrWhiteSpace(originalDisplayName))
            {
                originalDisplayName = GetPathLeafName(path);
            }

            var editor = new System.Windows.Controls.TextBox
            {
                Text = originalDisplayName,
                Style = TryFindResource("InlineRenameTextBox") as Style,
                FontSize = label.FontSize,
                FontFamily = label.FontFamily,
                FontWeight = label.FontWeight,
                Foreground = TryFindResource("PanelText") as System.Windows.Media.Brush ?? label.Foreground,
                MinWidth = 0,
                Margin = label.Margin,
                Padding = new Thickness(4, 1, 4, 1),
                TextAlignment = label.TextAlignment,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = label.HorizontalAlignment,
                CaretBrush = TryFindResource("PanelText") as System.Windows.Media.Brush ??
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 245, 250)),
                SelectionBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xA8, 0x6F, 0x8E, 0xFF)),
                SelectionOpacity = 0.35,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden
            };
            ConfigureInlineRenameEditorLayout(editor, hostPanel, label);

            editor.KeyDown += RenameEditor_KeyDown;
            editor.LostKeyboardFocus += RenameEditor_LostKeyboardFocus;
            CopyAttachedLayoutProperties(label, editor);
            if (!ReplaceChildAtLabelSlot(hostPanel, label, labelIndex, editor, out int resolvedHostIndex))
            {
                editor.KeyDown -= RenameEditor_KeyDown;
                editor.LostKeyboardFocus -= RenameEditor_LostKeyboardFocus;
                return false;
            }

            _renameEditItem = item;
            _renameEditBox = editor;
            _renameEditOriginalLabel = label;
            _renameEditOriginalHostPanel = hostPanel;
            _renameEditOriginalHostIndex = resolvedHostIndex;
            _renameOriginalPath = path;
            _renameOriginalDisplayName = originalDisplayName;

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_renameEditBox, editor))
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);

            return true;
        }

        private void RenameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!ReferenceEquals(sender, _renameEditBox))
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                EndInlineRenameSession(commit: true);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                EndInlineRenameSession(commit: false);
                e.Handled = true;
            }
        }

        private void RenameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _renameEditBox))
            {
                EndInlineRenameSession(commit: true);
            }
        }

        private void EndInlineRenameSession(bool commit)
        {
            if (_renameEditBox == null ||
                _renameEditItem == null ||
                _renameEditOriginalLabel == null ||
                _renameEditOriginalHostPanel == null ||
                _renameEditOriginalHostIndex < 0)
            {
                return;
            }

            var editor = _renameEditBox;
            var originalLabel = _renameEditOriginalLabel;
            var originalHostPanel = _renameEditOriginalHostPanel;
            int originalHostIndex = _renameEditOriginalHostIndex;
            string originalPath = _renameOriginalPath;
            string originalDisplayName = _renameOriginalDisplayName;
            string requestedDisplayName = editor.Text?.Trim() ?? string.Empty;

            editor.KeyDown -= RenameEditor_KeyDown;
            editor.LostKeyboardFocus -= RenameEditor_LostKeyboardFocus;
            RestoreLabelIntoHost(originalHostPanel, editor, originalLabel, originalHostIndex);

            originalLabel.Text = originalDisplayName;

            _renameEditBox = null;
            _renameEditItem = null;
            _renameEditOriginalLabel = null;
            _renameEditOriginalHostPanel = null;
            _renameEditOriginalHostIndex = -1;
            _renameOriginalPath = string.Empty;
            _renameOriginalDisplayName = string.Empty;

            if (!commit || string.IsNullOrWhiteSpace(requestedDisplayName))
            {
                return;
            }

            if (string.Equals(requestedDisplayName, originalDisplayName, StringComparison.Ordinal))
            {
                return;
            }

            TryRenamePathFromInlineEditor(originalPath, requestedDisplayName);
        }

        private static void CopyAttachedLayoutProperties(FrameworkElement source, FrameworkElement target)
        {
            Grid.SetRow(target, Grid.GetRow(source));
            Grid.SetColumn(target, Grid.GetColumn(source));
            Grid.SetRowSpan(target, Grid.GetRowSpan(source));
            Grid.SetColumnSpan(target, Grid.GetColumnSpan(source));
            DockPanel.SetDock(target, DockPanel.GetDock(source));

            double left = Canvas.GetLeft(source);
            if (!double.IsNaN(left))
            {
                Canvas.SetLeft(target, left);
            }

            double top = Canvas.GetTop(source);
            if (!double.IsNaN(top))
            {
                Canvas.SetTop(target, top);
            }

            double right = Canvas.GetRight(source);
            if (!double.IsNaN(right))
            {
                Canvas.SetRight(target, right);
            }

            double bottom = Canvas.GetBottom(source);
            if (!double.IsNaN(bottom))
            {
                Canvas.SetBottom(target, bottom);
            }

            target.HorizontalAlignment = source.HorizontalAlignment;
            target.VerticalAlignment = source.VerticalAlignment;
        }

        private static void ConfigureInlineRenameEditorLayout(
            System.Windows.Controls.TextBox editor,
            System.Windows.Controls.Panel hostPanel,
            TextBlock label)
        {
            bool isDetailsNameCell = hostPanel is Grid grid &&
                grid.ColumnDefinitions.Count >= 2 &&
                Grid.GetColumn(label) > 0;
            bool isTileNameCell = hostPanel is StackPanel stackPanel &&
                stackPanel.Orientation == System.Windows.Controls.Orientation.Vertical;

            double labelWidth = label.ActualWidth > 1
                ? label.ActualWidth
                : (label.Width > 0 ? label.Width : 0);
            double hostWidth = hostPanel.ActualWidth > 1
                ? hostPanel.ActualWidth
                : (hostPanel.Width > 0 ? hostPanel.Width : 0);

            double labelHeight = label.ActualHeight > 1 ? label.ActualHeight : 18;
            editor.Height = labelHeight;
            editor.MinHeight = labelHeight;
            editor.MaxHeight = labelHeight;
            editor.VerticalAlignment = label.VerticalAlignment == VerticalAlignment.Stretch
                ? VerticalAlignment.Center
                : label.VerticalAlignment;

            if (isDetailsNameCell)
            {
                double columnWidth = 0;
                if (hostPanel is Grid g && Grid.GetColumn(label) is int col && col >= 0 && col < g.ColumnDefinitions.Count)
                {
                    var colDef = g.ColumnDefinitions[col];
                    columnWidth = colDef.ActualWidth > 1 ? colDef.ActualWidth : colDef.Width.Value;
                }
                double editorWidth = columnWidth > 1
                    ? Math.Max(80, columnWidth - 4)
                    : Math.Max(80, labelWidth + 8);
                editor.Width = editorWidth;
                editor.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                editor.TextAlignment = TextAlignment.Left;
                editor.Padding = new Thickness(2, 0, 2, 0);
                editor.Margin = label.Margin;
                return;
            }

            if (isTileNameCell)
            {
                double tileWidth = hostWidth > 1
                    ? hostWidth
                    : Math.Max(72, labelWidth + 8);
                editor.Width = tileWidth;
                editor.MinWidth = tileWidth;
                editor.MaxWidth = tileWidth;
                editor.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                editor.TextAlignment = TextAlignment.Center;
                editor.Padding = new Thickness(2, 1, 2, 1);
                editor.Margin = new Thickness(0, 2, 0, 0);
                return;
            }

            double fallbackWidth = labelWidth > 1
                ? labelWidth + 8
                : Math.Max(84, hostWidth > 1 ? hostWidth - 4 : 104);
            editor.Width = Math.Max(84, fallbackWidth);
            editor.HorizontalAlignment = label.HorizontalAlignment == System.Windows.HorizontalAlignment.Stretch
                ? System.Windows.HorizontalAlignment.Center
                : label.HorizontalAlignment;
            editor.TextAlignment = label.TextAlignment;
        }

        private static bool ReplaceChildAtLabelSlot(
            System.Windows.Controls.Panel hostPanel,
            UIElement expectedChild,
            int preferredIndex,
            UIElement replacement,
            out int resolvedIndex)
        {
            resolvedIndex = -1;

            if (hostPanel == null || expectedChild == null || replacement == null)
            {
                return false;
            }

            int index = preferredIndex;
            if (index < 0 ||
                index >= hostPanel.Children.Count ||
                !ReferenceEquals(hostPanel.Children[index], expectedChild))
            {
                index = hostPanel.Children.IndexOf(expectedChild);
            }

            if (index < 0)
            {
                return false;
            }

            if (replacement is FrameworkElement replacementElement &&
                replacementElement.Parent is System.Windows.Controls.Panel replacementParent)
            {
                int existingReplacementIndex = replacementParent.Children.IndexOf(replacementElement);
                if (existingReplacementIndex >= 0)
                {
                    replacementParent.Children.RemoveAt(existingReplacementIndex);
                }
            }

            hostPanel.Children.RemoveAt(index);
            hostPanel.Children.Insert(index, replacement);
            resolvedIndex = index;
            return true;
        }

        private static void RestoreLabelIntoHost(
            System.Windows.Controls.Panel hostPanel,
            UIElement editor,
            TextBlock originalLabel,
            int preferredIndex)
        {
            if (hostPanel == null || editor == null || originalLabel == null)
            {
                return;
            }

            int editorIndex = hostPanel.Children.IndexOf(editor);
            if (editorIndex >= 0)
            {
                hostPanel.Children.RemoveAt(editorIndex);

                if (originalLabel.Parent is System.Windows.Controls.Panel otherParent &&
                    !ReferenceEquals(otherParent, hostPanel))
                {
                    int labelIndexInOtherParent = otherParent.Children.IndexOf(originalLabel);
                    if (labelIndexInOtherParent >= 0)
                    {
                        otherParent.Children.RemoveAt(labelIndexInOtherParent);
                    }
                }

                if (originalLabel.Parent == null)
                {
                    int insertIndex = Math.Max(0, Math.Min(editorIndex, hostPanel.Children.Count));
                    hostPanel.Children.Insert(insertIndex, originalLabel);
                }

                return;
            }

            if (originalLabel.Parent == null)
            {
                int insertIndex = Math.Max(0, Math.Min(preferredIndex, hostPanel.Children.Count));
                hostPanel.Children.Insert(insertIndex, originalLabel);
            }
        }

        private bool TryRenamePathFromInlineEditor(string sourcePath, string requestedDisplayName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(requestedDisplayName))
            {
                return false;
            }

            bool isDirectory = Directory.Exists(sourcePath);
            bool isFile = !isDirectory && File.Exists(sourcePath);
            if (!isDirectory && !isFile)
            {
                return false;
            }

            try
            {
                string sourceParent = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sourceParent) || !Directory.Exists(sourceParent))
                {
                    return false;
                }

                string targetName = requestedDisplayName.Trim();
                if (isFile && !showFileExtensions)
                {
                    string sourceExtension = Path.GetExtension(sourcePath);
                    if (!string.IsNullOrWhiteSpace(sourceExtension) &&
                        string.IsNullOrWhiteSpace(Path.GetExtension(targetName)))
                    {
                        targetName += sourceExtension;
                    }
                }

                string targetPath = Path.Combine(sourceParent, targetName);
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (isDirectory)
                {
                    Directory.Move(sourcePath, targetPath);
                }
                else
                {
                    File.Move(sourcePath, targetPath);
                }

                if (PanelType == PanelKind.Folder)
                {
                    if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
                    {
                        NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Renamed, targetPath, sourcePath);
                    }
                }
                else if (PanelType == PanelKind.List)
                {
                    for (int i = 0; i < PinnedItems.Count; i++)
                    {
                        if (string.Equals(PinnedItems[i], sourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            PinnedItems[i] = targetPath;
                        }
                    }

                    LoadList(PinnedItems.ToArray(), saveSettings: false);
                }

                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgRenameError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private static void WritePreferredDropEffect(System.Windows.DataObject dataObject, DragDropEffects effect)
        {
            int preferred = (effect & DragDropEffects.Move) == DragDropEffects.Move
                ? PreferredDropEffectMove
                : PreferredDropEffectCopy;

            var stream = new MemoryStream(BitConverter.GetBytes(preferred));
            dataObject.SetData(PreferredDropEffectFormat, stream);
        }

        private static DragDropEffects ReadPreferredDropEffect(System.Windows.IDataObject dataObject)
        {
            try
            {
                if (!dataObject.GetDataPresent(PreferredDropEffectFormat))
                {
                    return DragDropEffects.None;
                }

                object raw = dataObject.GetData(PreferredDropEffectFormat);
                if (raw is MemoryStream ms)
                {
                    byte[] buffer = ms.ToArray();
                    if (buffer.Length >= 4)
                    {
                        int value = BitConverter.ToInt32(buffer, 0);
                        if ((value & PreferredDropEffectMove) != 0) return DragDropEffects.Move;
                        if ((value & PreferredDropEffectCopy) != 0) return DragDropEffects.Copy;
                    }
                }
                else if (raw is byte[] bytes && bytes.Length >= 4)
                {
                    int value = BitConverter.ToInt32(bytes, 0);
                    if ((value & PreferredDropEffectMove) != 0) return DragDropEffects.Move;
                    if ((value & PreferredDropEffectCopy) != 0) return DragDropEffects.Copy;
                }
            }
            catch
            {
            }

            return DragDropEffects.None;
        }

        private static DragDropEffects ReadPreferredDropEffectFromClipboard()
        {
            try
            {
                var dataObject = System.Windows.Clipboard.GetDataObject();
                return dataObject != null ? ReadPreferredDropEffect(dataObject) : DragDropEffects.None;
            }
            catch (ExternalException)
            {
                return DragDropEffects.None;
            }
        }

        private static bool TryDataObjectHasFormat(System.Windows.IDataObject? dataObject, string format)
        {
            if (dataObject == null || string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            try
            {
                return dataObject.GetDataPresent(format);
            }
            catch (COMException)
            {
                return false;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private static bool TryGetDataObjectValue<T>(
            System.Windows.IDataObject? dataObject,
            string format,
            out T? value)
        {
            value = default;
            if (!TryDataObjectHasFormat(dataObject, format))
            {
                return false;
            }

            try
            {
                if (dataObject!.GetData(format) is T typed)
                {
                    value = typed;
                    return true;
                }
            }
            catch (COMException)
            {
            }
            catch (ExternalException)
            {
            }

            return false;
        }

        private static List<string> ExtractFileDropPaths(System.Windows.IDataObject dataObject)
        {
            if (!TryDataObjectHasFormat(dataObject, DataFormats.FileDrop))
            {
                return new List<string>();
            }

            if (TryGetDataObjectValue<string[]>(dataObject, DataFormats.FileDrop, out var paths) &&
                paths != null)
            {
                return paths
                    .Where(IsFileSystemPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (TryGetDataObjectValue<StringCollection>(dataObject, DataFormats.FileDrop, out var collection) &&
                collection != null)
            {
                return collection.Cast<string>()
                    .Where(IsFileSystemPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new List<string>();
        }

        private bool ShouldDefaultDropToMove(IReadOnlyCollection<string> paths)
        {
            if (paths.Count == 0) return false;
            if (PanelType != PanelKind.Folder) return false;
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            string? targetRoot;
            try
            {
                targetRoot = Path.GetPathRoot(Path.GetFullPath(currentFolderPath));
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                return false;
            }

            foreach (var path in paths)
            {
                try
                {
                    string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(path));
                    if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAllowedEffect(DragDropEffects allowed, DragDropEffects effect)
        {
            return effect != DragDropEffects.None && (allowed & effect) == effect;
        }

        private bool TryGetInternalPanelDragSourceId(System.Windows.IDataObject dataObject, out string sourcePanelId)
        {
            sourcePanelId = string.Empty;
            if (!TryGetDataObjectValue<string>(dataObject, InternalPanelDragSourceFormat, out var id) ||
                string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            sourcePanelId = id;
            return true;
        }

        private static DragDropEffects ResolveDesiredDropEffect(DragDropEffects allowed, DragDropEffects fallbackDefault)
        {
            DragDropKeyStates keyStates = DragDropKeyStates.None;
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                keyStates |= DragDropKeyStates.ControlKey;
            }
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                keyStates |= DragDropKeyStates.ShiftKey;
            }
            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                keyStates |= DragDropKeyStates.AltKey;
            }

            return ResolveDesiredDropEffect(allowed, fallbackDefault, keyStates);
        }

        private static DragDropEffects ResolveDesiredDropEffect(
            DragDropEffects allowed,
            DragDropEffects fallbackDefault,
            DragDropKeyStates keyStates)
        {
            if (allowed == DragDropEffects.None)
            {
                allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            }

            DragDropEffects preferred;

            bool controlPressed = (keyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            bool shiftPressed = (keyStates & DragDropKeyStates.ShiftKey) == DragDropKeyStates.ShiftKey;

            if (controlPressed && shiftPressed)
            {
                preferred = DragDropEffects.Link;
            }
            else if (controlPressed)
            {
                preferred = DragDropEffects.Copy;
            }
            else if (shiftPressed)
            {
                preferred = DragDropEffects.Move;
            }
            else
            {
                preferred = fallbackDefault;
            }

            if (IsAllowedEffect(allowed, preferred)) return preferred;
            if (IsAllowedEffect(allowed, DragDropEffects.Copy)) return DragDropEffects.Copy;
            if (IsAllowedEffect(allowed, DragDropEffects.Move)) return DragDropEffects.Move;
            if (IsAllowedEffect(allowed, DragDropEffects.Link)) return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        private DragDropEffects ResolveIncomingDropEffect(DragEventArgs e, IReadOnlyCollection<string> droppedItems)
        {
            if (droppedItems == null || droppedItems.Count == 0)
            {
                return DragDropEffects.None;
            }

            if (TryGetInternalPanelDragSourceId(e.Data, out string sourcePanelId) &&
                !string.Equals(sourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                // Internal panel-to-panel drags always move.
                return DragDropEffects.Move;
            }

            var fallback = ShouldDefaultDropToMove(droppedItems) ? DragDropEffects.Move : DragDropEffects.Copy;
            var desired = ResolveDesiredDropEffect(e.AllowedEffects, fallback, e.KeyStates);
            if (desired == DragDropEffects.None)
            {
                desired = fallback;
            }

            return desired;
        }

        private bool TryCopySelectionToClipboard(bool cut)
        {
            if (IsTextInputFocused()) return false;

            var paths = GetSelectedFileSystemPaths();
            if (paths.Count == 0) return false;

            var collection = new StringCollection();
            foreach (var path in paths)
            {
                collection.Add(path);
            }

            try
            {
                var dataObject = new System.Windows.DataObject();
                dataObject.SetFileDropList(collection);
                WritePreferredDropEffect(dataObject, cut ? DragDropEffects.Move : DragDropEffects.Copy);
                System.Windows.Clipboard.SetDataObject(dataObject, true);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private bool TryPasteFromClipboard()
        {
            if (IsTextInputFocused()) return false;

            try
            {
                if (!System.Windows.Clipboard.ContainsFileDropList()) return false;

                var fileList = System.Windows.Clipboard.GetFileDropList();
                if (fileList == null || fileList.Count == 0) return false;

                var paths = fileList.Cast<string>()
                    .Where(IsFileSystemPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (paths.Count == 0) return false;

                var preferred = ReadPreferredDropEffectFromClipboard();
                if (preferred == DragDropEffects.None)
                {
                    preferred = DragDropEffects.Copy;
                }

                _ = ImportIncomingFileSystemItemsAsync(paths, preferred);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private static bool ConfirmDeleteAction(bool panelOnly, int count, string singleItemName)
        {
            string message;
            if (count <= 1)
            {
                string key = panelOnly ? "Loc.MsgDeletePanelOnlySingle" : "Loc.MsgDeleteRecycleSingle";
                message = string.Format(MainWindow.GetString(key), singleItemName);
            }
            else
            {
                string key = panelOnly ? "Loc.MsgDeletePanelOnlyMulti" : "Loc.MsgDeleteRecycleMulti";
                message = string.Format(MainWindow.GetString(key), count);
            }

            return System.Windows.MessageBox.Show(
                message,
                MainWindow.GetString("Loc.MsgDeleteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static bool ConfirmPermanentDeleteAction(int count, string singleItemName)
        {
            string message = count <= 1
                ? string.Format(MainWindow.GetString("Loc.MsgDeletePermanentSingle"), singleItemName)
                : string.Format(MainWindow.GetString("Loc.MsgDeletePermanentMulti"), count);

            return System.Windows.MessageBox.Show(
                message,
                MainWindow.GetString("Loc.MsgDeleteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private string GetSelectedItemDisplayName(ListBoxItem item)
        {
            if (TryGetItemNameLabel(item, out var text) &&
                !string.IsNullOrWhiteSpace(text.Text))
            {
                return text.Text.Trim();
            }

            if (item.Tag is string path)
            {
                string fallback = GetPathLeafName(path);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return fallback;
                }
            }

            return MainWindow.GetString("Loc.Untitled");
        }

        private bool IsParentNavigationItem(ListBoxItem item)
        {
            if (PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(currentFolderPath) ||
                item.Tag is not string path ||
                string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (TryGetItemNameLabel(item, out var text) &&
                !string.IsNullOrWhiteSpace(text.Text) &&
                text.Text.TrimStart().StartsWith(ParentNavigationTextPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            return IsParentNavigationPath(path);
        }

        private bool RemoveItemsFromPanel(IEnumerable<ListBoxItem> items)
        {
            bool changed = false;

            foreach (var item in items)
            {
                if (item.Tag is not string path || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (FileList.Items.Contains(item))
                {
                    FileList.Items.Remove(item);
                    changed = true;
                }

                _baseItemPaths.Remove(path);
                _searchInjectedPaths.Remove(path);
                _searchInjectedItems.Remove(item);
                PinnedItems.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }

            if (changed)
            {
                UpdateDropZoneVisibility();
                _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
            }

            return changed;
        }

        private static bool TryMovePathToRecycleBin(string path, out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    VBFileSystem.DeleteDirectory(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.DoNothing);
                    return true;
                }

                if (File.Exists(path))
                {
                    VBFileSystem.DeleteFile(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.DoNothing);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private bool TryHandleDeleteSelection()
        {
            if (_isDeleteOperationRunning)
            {
                return true;
            }

            if (IsTextInputFocused() || FileList == null || FileList.SelectedItems.Count == 0)
            {
                return false;
            }

            var selectedItems = FileList.SelectedItems
                .OfType<ListBoxItem>()
                .Where(item => item.Tag is string)
                .ToList();

            if (selectedItems.Count == 0)
            {
                return false;
            }

            bool anyChanges = false;

            if (PanelType == PanelKind.RecycleBin)
            {
                var recycledPaths = selectedItems
                    .Select(item => item.Tag as string)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>()
                    .Where(path => File.Exists(path) || Directory.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (recycledPaths.Count > 0 &&
                    ConfirmPermanentDeleteAction(recycledPaths.Count, GetSelectedItemDisplayName(selectedItems[0])))
                {
                    var displayNames = selectedItems
                        .Where(item => item.Tag is string)
                        .GroupBy(item => (string)item.Tag!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => GetSelectedItemDisplayName(group.First()), StringComparer.OrdinalIgnoreCase);
                    _ = DeleteRecycleBinSelectionAsync(recycledPaths, displayNames);
                }
            }
            else if (PanelType == PanelKind.List)
            {
                if (RemoveItemsFromPanel(selectedItems))
                {
                    anyChanges = true;
                }
            }
            else
            {
                var realPaths = selectedItems
                    .Where(item =>
                    {
                        if (item.Tag is not string path || string.IsNullOrWhiteSpace(path))
                        {
                            return false;
                        }

                        if (IsParentNavigationItem(item))
                        {
                            return false;
                        }

                        return File.Exists(path) || Directory.Exists(path);
                    })
                    .Select(item => (string)item.Tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var pseudoItems = selectedItems
                    .Where(item =>
                    {
                        if (item.Tag is not string path || string.IsNullOrWhiteSpace(path))
                        {
                            return false;
                        }

                        if (IsParentNavigationItem(item))
                        {
                            return true;
                        }

                        return !File.Exists(path) && !Directory.Exists(path);
                    })
                    .ToList();

                if (realPaths.Count > 0)
                {
                    string singleName = GetDisplayNameForPath(realPaths[0]);
                    if (string.IsNullOrWhiteSpace(singleName))
                    {
                        singleName = GetPathLeafName(realPaths[0]);
                    }

                    if (ConfirmDeleteAction(panelOnly: false, realPaths.Count, singleName))
                    {
                        var displayNames = selectedItems
                            .Where(item => item.Tag is string path &&
                                realPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            .GroupBy(item => (string)item.Tag!, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => GetSelectedItemDisplayName(group.First()), StringComparer.OrdinalIgnoreCase);
                        _ = DeletePathsToRecycleBinAsync(realPaths, displayNames, hadPanelOnlyChanges: false);
                    }
                }

                if (pseudoItems.Count > 0)
                {
                    if (RemoveItemsFromPanel(pseudoItems))
                    {
                        anyChanges = true;
                    }
                }
            }

            if (anyChanges)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }

            return true;
        }

        private async Task DeleteRecycleBinSelectionAsync(
            IReadOnlyList<string> recycledPaths,
            IReadOnlyDictionary<string, string> displayNames)
        {
            SetDeleteOperationState(true);
            bool changed = false;

            try
            {
                var deleteResult = await RunStaFileOperationAsync(() =>
                {
                    bool deletedAny = false;
                    var failures = new List<string>();

                    foreach (string path in recycledPaths)
                    {
                        if (TryDeleteRecycleBinItemPermanently(path, out string? error))
                        {
                            deletedAny = true;
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            string displayName = displayNames.TryGetValue(path, out string? name) &&
                                !string.IsNullOrWhiteSpace(name)
                                ? name
                                : GetPathLeafName(path);
                            failures.Add($"{displayName}: {error}");
                        }
                    }

                    return (DeletedAny: deletedAny, Failures: failures);
                });

                if (deleteResult.DeletedAny)
                {
                    QueueRecycleBinRefresh(immediate: true);
                    changed = true;
                }

                if (deleteResult.Failures.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(MainWindow.GetString("Loc.MsgDeletePermanentError"), string.Join(Environment.NewLine, deleteResult.Failures)),
                        MainWindow.GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgDeletePermanentError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetDeleteOperationState(false);
            }

            if (changed)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
        }

        private async Task DeletePathsToRecycleBinAsync(
            IReadOnlyList<string> realPaths,
            IReadOnlyDictionary<string, string> displayNames,
            bool hadPanelOnlyChanges)
        {
            string folderPathAtStart = currentFolderPath;
            bool refreshCurrentFolder =
                PanelType == PanelKind.Folder &&
                !string.IsNullOrWhiteSpace(folderPathAtStart) &&
                Directory.Exists(folderPathAtStart);

            SetDeleteOperationState(true);
            bool changed = hadPanelOnlyChanges;

            try
            {
                var deleteResult = await RunStaFileOperationAsync(() =>
                {
                    var deletedPaths = new List<string>();
                    var failures = new List<string>();

                    foreach (string path in realPaths)
                    {
                        if (TryMovePathToRecycleBin(path, out string? error))
                        {
                            deletedPaths.Add(path);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            string displayName = displayNames.TryGetValue(path, out string? name) &&
                                !string.IsNullOrWhiteSpace(name)
                                ? name
                                : GetPathLeafName(path);
                            failures.Add($"{displayName}: {error}");
                        }
                    }

                    return (DeletedPaths: deletedPaths, Failures: failures);
                });

                if (deleteResult.DeletedPaths.Count > 0)
                {
                    changed = true;

                    if (refreshCurrentFolder &&
                        PanelType == PanelKind.Folder &&
                        string.Equals(currentFolderPath, folderPathAtStart, StringComparison.OrdinalIgnoreCase))
                    {
                        InvalidateFolderSearchIndex(folderPathAtStart, rebuildInBackground: true, rerunActiveSearch: true);
                        foreach (string deletedPath in deleteResult.DeletedPaths)
                        {
                            ShellPropertyReader.InvalidatePath(deletedPath);
                            ExplorerDetailsColumnProvider.InvalidatePath(deletedPath);
                            EnqueueFolderWatcherChange(FolderWatcherChangeKind.Deleted, deletedPath);
                        }

                        QueueFolderRefreshFromWatcher(immediate: true);
                    }
                }

                if (deleteResult.Failures.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, deleteResult.Failures)),
                        MainWindow.GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetDeleteOperationState(false);
            }

            if (changed)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
        }

        private bool TryHandleSelectAllVisibleItems()
        {
            if (IsTextInputFocused() || FileList == null)
            {
                return false;
            }

            var selectableItems = FileList.Items
                .OfType<ListBoxItem>()
                .Where(item => item.Visibility == Visibility.Visible && !IsParentNavigationItem(item))
                .ToList();

            if (selectableItems.Count == 0)
            {
                return false;
            }

            FileList.SelectedItems.Clear();
            foreach (var item in selectableItems)
            {
                item.IsSelected = true;
            }

            FileList.Focus();
            FileList.ScrollIntoView(selectableItems[0]);
            return true;
        }

        private bool TryHandleOpenSelection()
        {
            if (IsTextInputFocused() ||
                FileList?.SelectedItems.Count != 1 ||
                FileList.SelectedItem is not ListBoxItem selectedItem ||
                IsParentNavigationItem(selectedItem) ||
                selectedItem.Tag is not string path ||
                string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            OpenPanelItemPath(path);
            return true;
        }

        private bool TryHandleRenameSelectionShortcut()
        {
            if (IsTextInputFocused() || !CanRenameSelection())
            {
                return false;
            }

            return TryBeginRenameSelection();
        }

        private bool TryHandleNavigateToParentFolderShortcut()
        {
            if (IsTextInputFocused() ||
                PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(currentFolderPath) ||
                !Directory.Exists(currentFolderPath))
            {
                return false;
            }

            string normalizedCurrentFolder;
            try
            {
                normalizedCurrentFolder = Path.GetFullPath(currentFolderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                normalizedCurrentFolder = currentFolderPath;
            }

            string? parentFolder = Path.GetDirectoryName(normalizedCurrentFolder);
            if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
            {
                return false;
            }

            LoadFolder(parentFolder, saveSettings: false, renamePanelTitle: false);
            return true;
        }

        private bool TryHandleRefreshPanelShortcut()
        {
            if (IsTextInputFocused())
            {
                return false;
            }

            if (PanelType == PanelKind.Folder &&
                !string.IsNullOrWhiteSpace(currentFolderPath) &&
                Directory.Exists(currentFolderPath))
            {
                LoadFolder(currentFolderPath, saveSettings: false, renamePanelTitle: false);
                return true;
            }

            if (PanelType == PanelKind.RecycleBin)
            {
                LoadRecycleBin(saveSettings: false, renamePanelTitle: false);
                return true;
            }

            if (PanelType == PanelKind.List && PinnedItems.Count > 0)
            {
                LoadList(PinnedItems.ToList(), saveSettings: false);
                return true;
            }

            return false;
        }

        private bool TryHandleFocusSearchShortcut()
        {
            if (SearchBox == null)
            {
                return false;
            }

            string normalizedMode = NormalizeSearchVisibilityMode(searchVisibilityMode);
            if (string.Equals(normalizedMode, SearchVisibilityHidden, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ShouldUseCompactSearchPresentation(normalizedMode))
            {
                ExpandCompactSearch(selectAll: true);
                return true;
            }

            FocusSearchBoxDeferred(selectAll: true);
            return true;
        }

        private void DesktopPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys modifiers = Keyboard.Modifiers;

            if (key == Key.Delete && TryHandleDeleteSelection())
            {
                e.Handled = true;
                return;
            }

            if ((key == Key.Enter || key == Key.Return) && TryHandleOpenSelection())
            {
                e.Handled = true;
                return;
            }

            if (key == Key.F2 && TryHandleRenameSelectionShortcut())
            {
                e.Handled = true;
                return;
            }

            if (key == Key.F5 && TryHandleRefreshPanelShortcut())
            {
                e.Handled = true;
                return;
            }

            if (key == Key.Back && TryHandleNavigateToParentFolderShortcut())
            {
                e.Handled = true;
                return;
            }

            if (modifiers == ModifierKeys.Alt &&
                key == Key.Up &&
                TryHandleNavigateToParentFolderShortcut())
            {
                e.Handled = true;
                return;
            }

            if (!modifiers.HasFlag(ModifierKeys.Control) ||
                modifiers.HasFlag(ModifierKeys.Alt))
            {
                return;
            }

            if (key == Key.A && TryHandleSelectAllVisibleItems())
            {
                e.Handled = true;
            }
            else if (key == Key.C && TryCopySelectionToClipboard(cut: false))
            {
                e.Handled = true;
            }
            else if (key == Key.X && TryCopySelectionToClipboard(cut: true))
            {
                e.Handled = true;
            }
            else if (key == Key.V && TryPasteFromClipboard())
            {
                e.Handled = true;
            }
            else if (key == Key.F && TryHandleFocusSearchShortcut())
            {
                e.Handled = true;
            }
        }

        private void StartRubberBandSelection(Point startPoint, bool additive)
        {
            if (FileList == null || SelectionRubberBand == null || SelectionCanvas == null) return;

            _isRubberBandSelecting = true;
            _rubberBandAdditive = additive;
            _rubberBandStartPoint = startPoint;
            _rubberBandSelectionSnapshot.Clear();

            if (additive)
            {
                foreach (var selected in FileList.SelectedItems.Cast<object>())
                {
                    _rubberBandSelectionSnapshot.Add(selected);
                }
            }

            SelectionRubberBand.Visibility = Visibility.Visible;
            SelectionRubberBand.Width = 0;
            SelectionRubberBand.Height = 0;
            Canvas.SetLeft(SelectionRubberBand, startPoint.X);
            Canvas.SetTop(SelectionRubberBand, startPoint.Y);
            FileList.CaptureMouse();
        }

        private void EndRubberBandSelection()
        {
            _isRubberBandSelecting = false;
            _rubberBandAdditive = false;
            _rubberBandSelectionSnapshot.Clear();

            if (FileList != null && FileList.IsMouseCaptured)
            {
                FileList.ReleaseMouseCapture();
            }

            if (SelectionRubberBand != null)
            {
                SelectionRubberBand.Visibility = Visibility.Collapsed;
                SelectionRubberBand.Width = 0;
                SelectionRubberBand.Height = 0;
            }
        }

        private void UpdateRubberBand(Point currentPoint)
        {
            if (SelectionRubberBand == null || SelectionHost == null || FileList == null) return;

            double left = Math.Min(_rubberBandStartPoint.X, currentPoint.X);
            double top = Math.Min(_rubberBandStartPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - _rubberBandStartPoint.X);
            double height = Math.Abs(currentPoint.Y - _rubberBandStartPoint.Y);
            var selectionRect = new Rect(left, top, width, height);

            Canvas.SetLeft(SelectionRubberBand, left);
            Canvas.SetTop(SelectionRubberBand, top);
            SelectionRubberBand.Width = width;
            SelectionRubberBand.Height = height;

            foreach (var dataItem in FileList.Items)
            {
                if (FileList.ItemContainerGenerator.ContainerFromItem(dataItem) is not ListBoxItem itemContainer)
                {
                    continue;
                }

                if (itemContainer.Visibility != Visibility.Visible)
                {
                    continue;
                }

                Rect itemRect = itemContainer.TransformToAncestor(SelectionHost)
                    .TransformBounds(new Rect(new Point(0, 0), itemContainer.RenderSize));
                bool intersects = selectionRect.IntersectsWith(itemRect);
                bool shouldSelect = !IsParentNavigationItem(itemContainer) && (_rubberBandAdditive
                    ? _rubberBandSelectionSnapshot.Contains(dataItem) || intersects
                    : intersects);

                if (itemContainer.IsSelected != shouldSelect)
                {
                    itemContainer.IsSelected = shouldSelect;
                }
            }
        }

        private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);

            if (sender is not WpfListBox listBox || SelectionHost == null) return;

            var clickedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            bool toggleModifier = IsToggleModifierPressed();
            bool rangeModifier = IsRangeModifierPressed();

            if (clickedItem != null)
            {
                if (IsParentNavigationItem(clickedItem))
                {
                    _fileDragStartItem = null;
                    _fileDragStartWithModifiers = false;
                    _lastRenameClickItem = null;
                    _lastRenameClickUtc = DateTime.MinValue;
                    _lastRenameClickWasOnName = false;

                    int requiredClickCount = openItemsOnSingleClick ? 1 : 2;
                    if (e.ChangedButton == MouseButton.Left &&
                        e.ClickCount >= requiredClickCount &&
                        clickedItem.Tag is string parentPath &&
                        !string.IsNullOrWhiteSpace(parentPath))
                    {
                        OpenPanelItemPath(parentPath);
                        e.Handled = true;
                        return;
                    }

                    listBox.Focus();
                    e.Handled = true;
                    return;
                }

                _fileDragStartItem = clickedItem;
                _fileDragStartWithModifiers = toggleModifier || rangeModifier;

                if (!toggleModifier &&
                    !rangeModifier &&
                    TryBeginRenameBySlowSecondClick(clickedItem, e))
                {
                    e.Handled = true;
                    return;
                }

                // ALT toggles selection just like CTRL.
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    clickedItem.IsSelected = !clickedItem.IsSelected;
                    listBox.Focus();
                    e.Handled = true;
                }

                // Preserve multi-selection when dragging from an already selected item.
                // The default ListBox click handling would otherwise collapse the selection
                // to the clicked item before MouseMove can start a multi-item drag.
                if (!toggleModifier &&
                    !rangeModifier &&
                    e.ClickCount == 1 &&
                    clickedItem.IsSelected &&
                    listBox.SelectedItems.Count > 1)
                {
                    listBox.Focus();
                    e.Handled = true;
                    return;
                }

                // Make sure a direct drag starts from the actually clicked item.
                if (!toggleModifier &&
                    !rangeModifier &&
                    !clickedItem.IsSelected)
                {
                    listBox.SelectedItems.Clear();
                    clickedItem.IsSelected = true;
                    listBox.SelectedItem = clickedItem;
                }
                return;
            }

            _fileDragStartItem = null;
            _fileDragStartWithModifiers = false;
            _lastRenameClickItem = null;
            _lastRenameClickUtc = DateTime.MinValue;
            _lastRenameClickWasOnName = false;

            if (!toggleModifier && !rangeModifier)
            {
                listBox.SelectedItems.Clear();
            }

            StartRubberBandSelection(e.GetPosition(SelectionHost), additive: toggleModifier);
            listBox.Focus();
            e.Handled = true;
        }

        private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _fileDragStartItem = null;
            _fileDragStartWithModifiers = false;

            if (_isRubberBandSelecting)
            {
                EndRubberBandSelection();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Left ||
                _renameEditBox != null ||
                IsToggleModifierPressed() ||
                IsRangeModifierPressed() ||
                FileList == null)
            {
                return;
            }

            Point currentPosition = e.GetPosition(null);
            Vector diff = _dragStartPoint - currentPosition;
            if (Math.Abs(diff.X) > 2 || Math.Abs(diff.Y) > 2)
            {
                return;
            }

            var clickedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (!openItemsOnSingleClick || e.ClickCount != 1)
            {
                return;
            }

            if (FileList.SelectedItems.Count != 1)
            {
                return;
            }

            if (clickedItem?.Tag is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                OpenPanelItemPath(path);
                e.Handled = true;
            }
        }

        private void FileList_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isRubberBandSelecting)
            {
                EndRubberBandSelection();
            }
        }

        private void FileList_MouseMove(object sender, MouseEventArgs e)
        {
            if (_renameEditBox != null)
            {
                return;
            }

            if (_isRubberBandSelecting)
            {
                if (SelectionHost != null)
                {
                    UpdateRubberBand(e.GetPosition(SelectionHost));
                }
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (IsToggleModifierPressed() || IsRangeModifierPressed())
                {
                    return;
                }

                Point currentPosition = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosition;

                // Low threshold for snappy drag-start.
                if (Math.Abs(diff.X) > 2 || Math.Abs(diff.Y) > 2)
                {
                    if (sender is WpfListBox listBox)
                    {
                        var dragItems = ResolveDragItems(listBox);
                        var selectedPaths = dragItems
                            .Select(item => item.Tag as string)
                            .Where(IsFileSystemPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Cast<string>()
                            .ToList();
                        if (selectedPaths.Count == 0)
                        {
                            return;
                        }

                        ListBoxItem? sourceItem = dragItems.Count == 1
                            ? dragItems[0]
                            : null;

                        if (sourceItem != null && !sourceItem.IsSelected)
                        {
                            listBox.SelectedItems.Clear();
                            sourceItem.IsSelected = true;
                            listBox.SelectedItem = sourceItem;
                        }

                        var fileDrop = new StringCollection();
                        foreach (var path in selectedPaths)
                        {
                            fileDrop.Add(path);
                        }

                        var dataObject = new System.Windows.DataObject();
                        dataObject.SetFileDropList(fileDrop);
                        dataObject.SetData(InternalPanelDragSourceFormat, PanelId);
                        // Don't force a preferred drop effect for drag-and-drop.
                        // Let the target (e.g. Explorer) choose native default behavior
                        // like move on same volume and copy across volumes.

                        if (sourceItem != null)
                        {
                            dataObject.SetData(
                                InternalListReorderFormat,
                                new InternalListReorderPayload
                                {
                                    SourcePanelId = PanelId,
                                    SourceItem = sourceItem
                                });
                        }

                        if (sourceItem?.Content is UIElement contentElement)
                        {
                            contentElement.Opacity = 0.8;
                        }

                        _internalReorderDropHandled = false;
                        var result = DragDrop.DoDragDrop(
                            listBox,
                            dataObject,
                            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

                        ClearFileListReorderPreviewAcrossPanels();

                        if (sourceItem?.Content is UIElement releasedElement)
                        {
                            releasedElement.Opacity = 1.0;
                        }

                        _fileDragStartItem = null;
                        _fileDragStartWithModifiers = false;

                        if (_internalReorderDropHandled)
                        {
                            _internalReorderDropHandled = false;
                            return;
                        }

                        if ((result & DragDropEffects.Move) == DragDropEffects.Move &&
                            PanelType == PanelKind.Folder &&
                            !string.IsNullOrWhiteSpace(currentFolderPath) &&
                            Directory.Exists(currentFolderPath))
                        {
                            foreach (string path in selectedPaths)
                            {
                                NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Deleted, path);
                            }

                            MainWindow.SaveSettings();
                        }
                    }
                }
            }
        }

        private List<ListBoxItem> ResolveDragItems(WpfListBox listBox)
        {
            var selected = listBox.SelectedItems
                .OfType<ListBoxItem>()
                .Where(item => !IsParentNavigationItem(item))
                .ToList();

            if (_fileDragStartWithModifiers)
            {
                return selected;
            }

            if (_fileDragStartItem != null)
            {
                if (IsParentNavigationItem(_fileDragStartItem))
                {
                    return selected;
                }

                if (selected.Count > 1 && _fileDragStartItem.IsSelected)
                {
                    return selected;
                }

                return new List<ListBoxItem> { _fileDragStartItem };
            }

            return selected;
        }

        private async void FileList_Drop(object sender, DragEventArgs e)
        {
            if (TryGetDataObjectValue<InternalListReorderPayload>(e.Data, InternalListReorderFormat, out var payload) &&
                payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                var sourceItem = payload.SourceItem;
                bool reordered = false;
                if (sourceItem != null && FileList != null)
                {
                    int insertIndex = _fileListReorderInsertIndex;
                    if (insertIndex < 0 &&
                        TryGetFileListReorderInsertIndex(sourceItem, e.GetPosition(FileList), out int resolvedInsertIndex))
                    {
                        insertIndex = resolvedInsertIndex;
                    }

                    reordered = insertIndex >= 0 && TryReorderFileListItem(sourceItem, insertIndex);
                }

                if (reordered)
                {
                    if (PanelType == PanelKind.List)
                    {
                        PinnedItems.Clear();
                        foreach (var entry in FileList?.Items.OfType<ListBoxItem>() ?? Enumerable.Empty<ListBoxItem>())
                        {
                            if (entry.Tag is string path)
                            {
                                PinnedItems.Add(path);
                            }
                        }
                    }

                    ClearFileListReorderPreview();
                    MainWindow.SaveSettings();
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    ClearFileListReorderPreview();
                    e.Effects = DragDropEffects.None;
                }

                _internalReorderDropHandled = true;
                e.Handled = true;
                return;
            }

            if (TryDataObjectHasFormat(e.Data, DataFormats.FileDrop))
            {
                var droppedItems = ExtractFileDropPaths(e.Data);
                e.Handled = true;
                if (droppedItems.Count > 0)
                {
                    var desired = ResolveIncomingDropEffect(e, droppedItems);
                    bool changed = await ImportIncomingFileSystemItemsAsync(droppedItems, desired);
                    e.Effects = changed ? desired : DragDropEffects.None;
                }
            }
        }

        private async Task<bool> ImportIncomingFileSystemItemsAsync(
            IReadOnlyList<string> droppedItems,
            DragDropEffects requestedEffect)
        {
            if (droppedItems == null || droppedItems.Count == 0)
            {
                return false;
            }

            await _incomingFileImportSemaphore.WaitAsync();
            try
            {
                bool wasUninitializedBeforeDrop = PanelType == PanelKind.None &&
                                                  string.IsNullOrWhiteSpace(currentFolderPath) &&
                                                  PinnedItems.Count == 0;

                PanelKind effectiveType = PanelType;
                if (effectiveType == PanelKind.Folder && string.IsNullOrWhiteSpace(currentFolderPath))
                {
                    effectiveType = PanelKind.None;
                }

                if (effectiveType == PanelKind.RecycleBin)
                {
                    IncomingFileImportResult recycleResult = await Task.Run(
                        () => ExecuteRecycleBinImport(droppedItems));

                    if (recycleResult.FolderChanged)
                    {
                        QueueRecycleBinRefresh(immediate: true);
                        MainWindow.SaveSettings();
                        MainWindow.NotifyPanelsChanged();
                    }

                    if (recycleResult.Failures.Count > 0)
                    {
                        System.Windows.MessageBox.Show(
                            string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, recycleResult.Failures)),
                            MainWindow.GetString("Loc.MsgError"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }

                    UpdateDropZoneVisibility();
                    return recycleResult.FolderChanged;
                }

                bool folderChanged = false;
                bool listChanged = false;
                bool preferMove = (requestedEffect & DragDropEffects.Move) == DragDropEffects.Move;

                string? initialFolder = null;
                if (effectiveType == PanelKind.None)
                {
                    initialFolder = droppedItems.FirstOrDefault(Directory.Exists);
                    if (!string.IsNullOrWhiteSpace(initialFolder))
                    {
                        defaultFolderPath = initialFolder;
                        LoadFolder(initialFolder, saveSettings: false, renamePanelTitle: true);
                        string initialFolderName = GetFolderDisplayName(initialFolder);
                        if (ActiveTab != null && !string.IsNullOrWhiteSpace(initialFolderName))
                        {
                            ActiveTab.TabName = initialFolderName;
                            SyncSingleTabHeaderTitle();
                            RebuildTabBar();
                        }
                        effectiveType = PanelKind.Folder;
                        folderChanged = true;
                    }
                    else
                    {
                        LoadList(Array.Empty<string>(), false);
                        effectiveType = PanelKind.List;
                    }
                }

                IncomingFileImportResult result = await Task.Run(() =>
                    ExecuteIncomingFileImport(
                        droppedItems,
                        effectiveType,
                        currentFolderPath,
                        initialFolder,
                        preferMove));

                foreach (string item in result.ListItemsToAdd)
                {
                    listChanged |= AddFileToList(item, true);
                }

                folderChanged |= result.FolderChanged;

                bool anyChanged = folderChanged || listChanged;
                if (anyChanged)
                {
                    bool initializedByThisDrop = wasUninitializedBeforeDrop &&
                                                 ((PanelType == PanelKind.Folder && !string.IsNullOrWhiteSpace(currentFolderPath)) ||
                                                  (PanelType == PanelKind.List && PinnedItems.Count > 0));
                    if (initializedByThisDrop)
                    {
                        ActivateHoverModeForInitializedPanel();
                    }

                    MainWindow.SaveSettings();
                    MainWindow.NotifyPanelsChanged();
                }

                if (result.Failures.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, result.Failures)),
                        MainWindow.GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                UpdateDropZoneVisibility();
                return anyChanged;
            }
            finally
            {
                _incomingFileImportSemaphore.Release();
            }
        }

        private IncomingFileImportResult ExecuteRecycleBinImport(IReadOnlyList<string> droppedItems)
        {
            var result = new IncomingFileImportResult();

            foreach (string path in droppedItems
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TryMovePathToRecycleBin(path, out string? error))
                {
                    result.FolderChanged = true;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    result.Failures.Add($"{GetPathLeafName(path)}: {error}");
                }
            }

            return result;
        }

        private IncomingFileImportResult ExecuteIncomingFileImport(
            IReadOnlyList<string> droppedItems,
            PanelKind effectiveType,
            string? targetFolderPath,
            string? initialFolder,
            bool preferMove)
        {
            var result = new IncomingFileImportResult();

            foreach (string item in droppedItems
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(initialFolder) &&
                    string.Equals(item, initialFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(item))
                {
                    if (effectiveType == PanelKind.Folder &&
                        !string.IsNullOrWhiteSpace(targetFolderPath) &&
                        Directory.Exists(targetFolderPath))
                    {
                        bool changed = TryTransferRecycleBinItemToDirectory(
                            item,
                            targetFolderPath,
                            preferMove,
                            out string? errorMessage);
                        if (!changed)
                        {
                            changed = preferMove
                                ? TryTransferFolderToDirectory(item, targetFolderPath, move: true, out _, out errorMessage)
                                : TryTransferFolderToDirectory(item, targetFolderPath, move: false, out _, out errorMessage);
                        }

                        if (changed)
                        {
                            result.FolderChanged = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(errorMessage))
                        {
                            result.Failures.Add($"{GetPathLeafName(item)}: {errorMessage}");
                        }
                    }
                    else
                    {
                        result.ListItemsToAdd.Add(item);
                    }

                    continue;
                }

                if (!File.Exists(item))
                {
                    continue;
                }

                if (effectiveType == PanelKind.Folder &&
                    !string.IsNullOrWhiteSpace(targetFolderPath) &&
                    Directory.Exists(targetFolderPath))
                {
                    bool changed = TryTransferRecycleBinItemToDirectory(
                        item,
                        targetFolderPath,
                        preferMove,
                        out string? errorMessage);
                    if (!changed)
                    {
                        changed = preferMove
                            ? TryTransferFileToDirectory(item, targetFolderPath, move: true, out _, out errorMessage)
                            : TryTransferFileToDirectory(item, targetFolderPath, move: false, out _, out errorMessage);
                    }

                    if (changed)
                    {
                        result.FolderChanged = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(errorMessage))
                    {
                        result.Failures.Add($"{GetPathLeafName(item)}: {errorMessage}");
                    }
                }
                else
                {
                    result.ListItemsToAdd.Add(item);
                }
            }

            return result;
        }

        private void ActivateHoverModeForInitializedPanel()
        {
            if (!expandOnHover || !isContentVisible || _isCollapseAnimationRunning)
            {
                return;
            }

            // Treat the first content drop as the moment where hover-mode becomes active.
            RebaseHoverRestoreStateFromCurrentBounds();
            _hoverExpanded = true;

            if (!IsMouseOver && !IsCursorWithinPanelBounds())
            {
                RequestHoverCollapseAnimated();
            }
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            // Let tab drag events pass through to TabBar_Drop
            if (TryDataObjectHasFormat(e.Data, TabDragFormat))
            {
                return;
            }

            var droppedItems = ExtractFileDropPaths(e.Data);
            if (droppedItems.Count == 0) return;

            var requestedEffect = ResolveIncomingDropEffect(e, droppedItems);

            e.Handled = true;
            bool changed = await ImportIncomingFileSystemItemsAsync(droppedItems, requestedEffect);
            e.Effects = changed ? requestedEffect : DragDropEffects.None;
        }

        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            if (TryGetDataObjectValue<InternalListReorderPayload>(e.Data, InternalListReorderFormat, out var payload) &&
                payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.Move;
                if (FileList != null &&
                    payload.SourceItem != null &&
                    IsPointWithinElementBounds(FileList, e.GetPosition(FileList), tolerance: 8) &&
                    TryGetFileListReorderInsertIndex(payload.SourceItem, e.GetPosition(FileList), out int insertIndex))
                {
                    ApplyFileListReorderPreview(payload.SourceItem, insertIndex);
                }
                else
                {
                    ClearFileListReorderPreview();
                }

                e.Handled = true;
            }
        }

        private void FileList_DragLeave(object sender, DragEventArgs e)
        {
            if (TryGetDataObjectValue<InternalListReorderPayload>(e.Data, InternalListReorderFormat, out var payload) &&
                payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                // WPF fires DragLeave when entering child elements within the FileList
                // (it's a bubbling event). Only clear the preview when the mouse has
                // actually left the FileList bounds. Window_DragOver also handles clearing
                // when the mouse moves outside FileList within the same window.
                if (FileList != null && IsPointWithinElementBounds(FileList, e.GetPosition(FileList), tolerance: 12))
                {
                    e.Handled = true;
                    return;
                }

                ClearFileListReorderPreview();
                e.Handled = true;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            // For tab drags: set Move effect but don't mark as handled,
            // so the HeaderBar's DragOver can still fire for insert indicator positioning.
            if (TryDataObjectHasFormat(e.Data, TabDragFormat))
            {
                ClearFileListReorderPreview();
                e.Effects = DragDropEffects.Move;
                // Don't set e.Handled — let TabBar_DragOver handle positioning
                return;
            }

            if (TryGetDataObjectValue<InternalListReorderPayload>(e.Data, InternalListReorderFormat, out var payload) &&
                payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                if (FileList != null &&
                    IsPointWithinElementBounds(FileList, e.GetPosition(FileList), tolerance: 8))
                {
                    e.Effects = DragDropEffects.Move;
                    return;
                }

                ClearFileListReorderPreview();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
            else if (TryDataObjectHasFormat(e.Data, DataFormats.FileDrop))
            {
                ClearFileListReorderPreview();
                var droppedItems = ExtractFileDropPaths(e.Data);
                if (droppedItems.Count == 0)
                {
                    e.Effects = DragDropEffects.None;
                }
                else
                {
                    e.Effects = ResolveIncomingDropEffect(e, droppedItems);
                }
            }
            else
            {
                ClearFileListReorderPreview();
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            if (TryGetDataObjectValue<InternalListReorderPayload>(e.Data, InternalListReorderFormat, out var payload) &&
                payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ClearFileListReorderPreview();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            RequestHoverExpandAnimated();
        }

        private void Window_MouseMoveHoverProbe(object sender, MouseEventArgs e)
        {
            if (!IsHoverBehaviorEnabled) return;
            if (isContentVisible) return;
            if (_isCollapseAnimationRunning) return;

            RequestHoverExpandAnimated();
        }

        private bool IsCursorWithinPanelBounds(double tolerance = 0.0)
        {
            Point cursor = GetMouseScreenPositionDip();
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            double left = Left - tolerance;
            double top = Top - tolerance;
            double right = Left + width + tolerance;
            double bottom = Top + height + tolerance;

            return cursor.X >= left &&
                   cursor.X <= right &&
                   cursor.Y >= top &&
                   cursor.Y <= bottom;
        }

        private async void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!IsHoverBehaviorEnabled || !_hoverExpanded) return;

            var leaveCts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _hoverLeaveCts, leaveCts);
            previous?.Cancel();
            previous?.Dispose();

            try
            {
                await Task.Delay(90, leaveCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_hoverLeaveCts, leaveCts))
                {
                    _hoverLeaveCts = null;
                }
                leaveCts.Dispose();
            }

            if (!IsHoverBehaviorEnabled || !_hoverExpanded) return;
            RequestHoverCollapseAnimated();
        }
    }
}
