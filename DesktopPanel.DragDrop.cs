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
using DataFormats = System.Windows.DataFormats;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using WpfListBox = System.Windows.Controls.ListBox;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private const string InternalListReorderFormat = "DesktopPlus.InternalListReorder";
        private const string PreferredDropEffectFormat = "Preferred DropEffect";
        private const int PreferredDropEffectCopy = 1;
        private const int PreferredDropEffectMove = 2;
        private bool _isRubberBandSelecting;
        private bool _rubberBandAdditive;
        private Point _rubberBandStartPoint;
        private bool _internalReorderDropHandled;
        private readonly HashSet<object> _rubberBandSelectionSnapshot = new HashSet<object>();

        private sealed class InternalListReorderPayload
        {
            public string SourcePanelId { get; init; } = string.Empty;
            public ListBoxItem? SourceItem { get; init; }
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
                .Select(item => item.Tag as string)
                .Where(IsFileSystemPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
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

        private static List<string> ExtractFileDropPaths(System.Windows.IDataObject dataObject)
        {
            if (!dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                return new List<string>();
            }

            if (dataObject.GetData(DataFormats.FileDrop) is string[] paths)
            {
                return paths
                    .Where(IsFileSystemPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (dataObject.GetData(DataFormats.FileDrop) is StringCollection collection)
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

        private static DragDropEffects ResolveDesiredDropEffect(DragDropEffects allowed, DragDropEffects fallbackDefault)
        {
            if (allowed == DragDropEffects.None)
            {
                allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            }

            var modifiers = Keyboard.Modifiers;
            DragDropEffects preferred;

            if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift))
            {
                preferred = DragDropEffects.Link;
            }
            else if (modifiers.HasFlag(ModifierKeys.Control))
            {
                preferred = DragDropEffects.Copy;
            }
            else if (modifiers.HasFlag(ModifierKeys.Shift))
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

                return ImportIncomingFileSystemItems(paths, preferred);
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private void DesktopPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                return;
            }

            if (e.Key == Key.C && TryCopySelectionToClipboard(cut: false))
            {
                e.Handled = true;
            }
            else if (e.Key == Key.X && TryCopySelectionToClipboard(cut: true))
            {
                e.Handled = true;
            }
            else if (e.Key == Key.V && TryPasteFromClipboard())
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
                bool shouldSelect = _rubberBandAdditive
                    ? _rubberBandSelectionSnapshot.Contains(dataItem) || intersects
                    : intersects;

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
                // ALT toggles selection just like CTRL.
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    clickedItem.IsSelected = !clickedItem.IsSelected;
                    listBox.Focus();
                    e.Handled = true;
                }
                return;
            }

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
            if (_isRubberBandSelecting)
            {
                EndRubberBandSelection();
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
                        var selectedPaths = GetSelectedFileSystemPaths();
                        if (selectedPaths.Count == 0)
                        {
                            return;
                        }

                        ListBoxItem? sourceItem = listBox.SelectedItems.Count == 1
                            ? listBox.SelectedItem as ListBoxItem
                            : null;

                        var fileDrop = new StringCollection();
                        foreach (var path in selectedPaths)
                        {
                            fileDrop.Add(path);
                        }

                        var dataObject = new System.Windows.DataObject();
                        dataObject.SetFileDropList(fileDrop);
                        WritePreferredDropEffect(dataObject, DragDropEffects.Copy);

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

                        if (sourceItem?.Content is StackPanel panel)
                        {
                            panel.Opacity = 0.8;
                        }

                        _internalReorderDropHandled = false;
                        var result = DragDrop.DoDragDrop(
                            listBox,
                            dataObject,
                            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

                        if (sourceItem?.Content is StackPanel releasedPanel)
                        {
                            releasedPanel.Opacity = 1.0;
                        }

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
                            LoadFolder(currentFolderPath, saveSettings: false);
                            MainWindow.SaveSettings();
                        }
                    }
                }
            }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(InternalListReorderFormat) &&
                e.Data.GetData(InternalListReorderFormat) is InternalListReorderPayload payload &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                var sourceItem = payload.SourceItem;
                var target = e.OriginalSource as FrameworkElement;

                while (target != null && !(target.DataContext is ListBoxItem) && target is not ListBoxItem)
                {
                    target = VisualTreeHelper.GetParent(target) as FrameworkElement;
                }

                var targetItem = target as ListBoxItem ?? (target?.DataContext as ListBoxItem);

                if (sourceItem != null && targetItem != null && !ReferenceEquals(sourceItem, targetItem))
                {
                    int targetIndex = FileList.Items.IndexOf(targetItem);

                    FileList.Items.Remove(sourceItem);
                    FileList.Items.Insert(targetIndex, sourceItem);

                    FileList.SelectedItem = sourceItem;
                    if (PanelType == PanelKind.List)
                    {
                        PinnedItems.Clear();
                        foreach (var entry in FileList.Items.OfType<ListBoxItem>())
                        {
                            if (entry.Tag is string path)
                            {
                                PinnedItems.Add(path);
                            }
                        }
                    }
                    MainWindow.SaveSettings();
                    _internalReorderDropHandled = true;
                    e.Effects = DragDropEffects.Move;
                }

                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var droppedItems = ExtractFileDropPaths(e.Data);
                if (droppedItems.Count > 0)
                {
                    var fallback = ShouldDefaultDropToMove(droppedItems) ? DragDropEffects.Move : DragDropEffects.Copy;
                    var desired = ResolveDesiredDropEffect(e.AllowedEffects, fallback);
                    if (desired == DragDropEffects.None)
                    {
                        desired = fallback;
                    }

                    bool changed = ImportIncomingFileSystemItems(droppedItems, desired);
                    e.Effects = changed ? desired : DragDropEffects.None;
                }

                e.Handled = true;
            }
        }

        private bool ImportIncomingFileSystemItems(IReadOnlyList<string> droppedItems, DragDropEffects requestedEffect)
        {
            if (droppedItems == null || droppedItems.Count == 0)
            {
                return false;
            }

            PanelKind effectiveType = PanelType;
            if (effectiveType == PanelKind.Folder && string.IsNullOrWhiteSpace(currentFolderPath))
            {
                effectiveType = PanelKind.None;
            }

            bool listChanged = false;
            bool folderChanged = false;
            bool preferMove = (requestedEffect & DragDropEffects.Move) == DragDropEffects.Move;

            string? initialFolder = null;
            if (effectiveType == PanelKind.None)
            {
                initialFolder = droppedItems.FirstOrDefault(Directory.Exists);
                if (!string.IsNullOrWhiteSpace(initialFolder))
                {
                    defaultFolderPath = initialFolder;
                    LoadFolder(initialFolder, saveSettings: false, renamePanelTitle: true);
                    effectiveType = PanelKind.Folder;
                    folderChanged = true;
                }
                else
                {
                    LoadList(Array.Empty<string>(), false);
                    effectiveType = PanelKind.List;
                }
            }

            foreach (var item in droppedItems)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(initialFolder) &&
                    string.Equals(item, initialFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(item))
                {
                    if (effectiveType == PanelKind.Folder)
                    {
                        if (string.IsNullOrWhiteSpace(currentFolderPath))
                        {
                            LoadFolder(item, saveSettings: false, renamePanelTitle: true);
                            folderChanged = true;
                        }
                        else
                        {
                            bool changed = preferMove
                                ? MoveFolderIntoCurrent(item, refreshAfterChange: false)
                                : CopyFolderIntoCurrent(item, refreshAfterChange: false);
                            folderChanged |= changed;
                        }
                    }
                    else
                    {
                        AddFileToList(item, true);
                        listChanged = true;
                    }
                }
                else if (File.Exists(item))
                {
                    if (effectiveType == PanelKind.Folder)
                    {
                        bool changed = preferMove
                            ? MoveFileIntoCurrent(item, refreshAfterChange: false)
                            : CopyFileIntoCurrent(item, refreshAfterChange: false);
                        folderChanged |= changed;
                    }
                    else
                    {
                        AddFileToList(item, true);
                        listChanged = true;
                    }
                }
            }

            if (folderChanged &&
                !string.IsNullOrWhiteSpace(currentFolderPath) &&
                Directory.Exists(currentFolderPath))
            {
                LoadFolder(currentFolderPath, saveSettings: false);
            }

            if (folderChanged || listChanged)
            {
                MainWindow.SaveSettings();
            }

            UpdateDropZoneVisibility();
            return folderChanged || listChanged;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var droppedItems = ExtractFileDropPaths(e.Data);
            if (droppedItems.Count == 0) return;

            var fallback = ShouldDefaultDropToMove(droppedItems) ? DragDropEffects.Move : DragDropEffects.Copy;
            var requestedEffect = e.Effects != DragDropEffects.None
                ? e.Effects
                : ResolveDesiredDropEffect(e.AllowedEffects, fallback);
            if (requestedEffect == DragDropEffects.None)
            {
                requestedEffect = fallback;
            }

            bool changed = ImportIncomingFileSystemItems(droppedItems, requestedEffect);
            e.Effects = changed ? requestedEffect : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(InternalListReorderFormat) &&
                e.Data.GetData(InternalListReorderFormat) is InternalListReorderPayload payload &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.Move;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var droppedItems = ExtractFileDropPaths(e.Data);
                if (droppedItems.Count == 0)
                {
                    e.Effects = DragDropEffects.None;
                }
                else
                {
                    var fallback = ShouldDefaultDropToMove(droppedItems) ? DragDropEffects.Move : DragDropEffects.Copy;
                    e.Effects = ResolveDesiredDropEffect(e.AllowedEffects, fallback);
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
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
