using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private bool _isRubberBandSelecting;
        private bool _rubberBandAdditive;
        private Point _rubberBandStartPoint;
        private readonly HashSet<object> _rubberBandSelectionSnapshot = new HashSet<object>();

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
                    if (sender is WpfListBox listBox &&
                        listBox.SelectedItems.Count == 1 &&
                        listBox.SelectedItem is ListBoxItem sourceItem)
                    {
                        if (sourceItem.Content is StackPanel panel)
                        {
                            panel.Opacity = 0.8;
                        }

                        DragDrop.DoDragDrop(listBox, sourceItem, DragDropEffects.Move);

                        if (sourceItem.Content is StackPanel releasedPanel)
                        {
                            releasedPanel.Opacity = 1.0;
                        }
                    }
                }
            }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(ListBoxItem)))
            {
                var sourceItem = e.Data.GetData(typeof(ListBoxItem)) as ListBoxItem;
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
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] droppedItems = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (droppedItems.Length == 0) return;

                PanelKind effectiveType = PanelType;
                if (effectiveType == PanelKind.Folder && string.IsNullOrWhiteSpace(currentFolderPath))
                {
                    effectiveType = PanelKind.None;
                }

                bool listChanged = false;
                string? initialFolder = null;
                if (effectiveType == PanelKind.None)
                {
                    initialFolder = droppedItems.FirstOrDefault(Directory.Exists);
                    if (!string.IsNullOrWhiteSpace(initialFolder))
                    {
                        defaultFolderPath = initialFolder;
                        LoadFolder(initialFolder, renamePanelTitle: true);
                        effectiveType = PanelKind.Folder;
                    }
                    else
                    {
                        LoadList(Array.Empty<string>(), false);
                        effectiveType = PanelKind.List;
                    }
                }

                foreach (var item in droppedItems)
                {
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
                                LoadFolder(item, renamePanelTitle: true);
                            }
                            else
                            {
                                MoveFolderIntoCurrent(item);
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
                            MoveFileIntoCurrent(item);
                        }
                        else
                        {
                            AddFileToList(item, true);
                            listChanged = true;
                        }
                    }
                }

                if (listChanged)
                {
                    MainWindow.SaveSettings();
                }

                UpdateDropZoneVisibility();
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
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
