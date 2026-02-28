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
        private string _renameOriginalPath = string.Empty;
        private string _renameOriginalDisplayName = string.Empty;
        private ListBoxItem? _lastRenameClickItem;
        private DateTime _lastRenameClickUtc = DateTime.MinValue;
        private Point _lastRenameClickPosition;

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

        private void RememberRenameClickCandidate(ListBoxItem? item, Point position)
        {
            _lastRenameClickItem = item;
            _lastRenameClickUtc = DateTime.UtcNow;
            _lastRenameClickPosition = position;
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

            bool selectedSingleItem = FileList.SelectedItems.Count == 1 &&
                                      ReferenceEquals(FileList.SelectedItem, clickedItem);

            bool sameItem = ReferenceEquals(_lastRenameClickItem, clickedItem);
            bool nearPreviousClick = sameItem &&
                                     (clickPos - _lastRenameClickPosition).Length <= 8;
            double elapsedMs = (now - _lastRenameClickUtc).TotalMilliseconds;

            int minDelayMs = Math.Max(420, System.Windows.Forms.SystemInformation.DoubleClickTime + 40);
            const int maxDelayMs = 2200;

            RememberRenameClickCandidate(clickedItem, clickPos);

            if (!selectedSingleItem ||
                !sameItem ||
                !nearPreviousClick ||
                elapsedMs < minDelayMs ||
                elapsedMs > maxDelayMs)
            {
                return false;
            }

            if (!TryGetRenameItemContext(clickedItem, out _, out _, out _))
            {
                return false;
            }

            if (!BeginInlineRename(clickedItem))
            {
                return false;
            }

            _lastRenameClickItem = null;
            _lastRenameClickUtc = DateTime.MinValue;
            return true;
        }

        private bool CanRenameSelection()
        {
            if (FileList?.SelectedItems.Count != 1 || FileList.SelectedItem is not ListBoxItem item)
            {
                return false;
            }

            return TryGetRenameItemContext(item, out _, out _, out _);
        }

        private bool TryBeginRenameSelection()
        {
            if (FileList?.SelectedItems.Count != 1 || FileList.SelectedItem is not ListBoxItem item)
            {
                return false;
            }

            return BeginInlineRename(item);
        }

        private bool TryGetRenameItemContext(ListBoxItem item, out string path, out StackPanel panel, out TextBlock label)
        {
            path = string.Empty;
            panel = null!;
            label = null!;

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

            if (item.Content is not StackPanel contentPanel)
            {
                return false;
            }

            var textLabel = contentPanel.Children.OfType<TextBlock>().FirstOrDefault();
            if (textLabel == null)
            {
                return false;
            }

            path = itemPath;
            panel = contentPanel;
            label = textLabel;
            return true;
        }

        private bool BeginInlineRename(ListBoxItem item)
        {
            if (_renameEditBox != null)
            {
                return false;
            }

            if (!TryGetRenameItemContext(item, out string path, out StackPanel panel, out TextBlock label))
            {
                return false;
            }

            int labelIndex = panel.Children.IndexOf(label);
            if (labelIndex < 0)
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
                FontSize = label.FontSize,
                FontFamily = label.FontFamily,
                Foreground = label.Foreground,
                Width = label.Width > 0 ? label.Width : 80,
                MinWidth = 64,
                Margin = label.Margin,
                Padding = new Thickness(3, 1, 3, 1),
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2A, 0x24, 0x2A, 0x34)),
                BorderBrush = TryFindResource("PanelBorder") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DimGray,
                CaretBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 245, 250))
            };

            editor.KeyDown += RenameEditor_KeyDown;
            editor.LostKeyboardFocus += RenameEditor_LostKeyboardFocus;

            panel.Children[labelIndex] = editor;

            _renameEditItem = item;
            _renameEditBox = editor;
            _renameEditOriginalLabel = label;
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
            if (_renameEditBox == null || _renameEditItem == null || _renameEditOriginalLabel == null)
            {
                return;
            }

            var editor = _renameEditBox;
            var item = _renameEditItem;
            var originalLabel = _renameEditOriginalLabel;
            string originalPath = _renameOriginalPath;
            string originalDisplayName = _renameOriginalDisplayName;
            string requestedDisplayName = editor.Text?.Trim() ?? string.Empty;

            editor.KeyDown -= RenameEditor_KeyDown;
            editor.LostKeyboardFocus -= RenameEditor_LostKeyboardFocus;

            if (item.Content is StackPanel panel)
            {
                int editorIndex = panel.Children.IndexOf(editor);
                if (editorIndex >= 0)
                {
                    panel.Children[editorIndex] = originalLabel;
                }
            }

            originalLabel.Text = originalDisplayName;

            _renameEditBox = null;
            _renameEditItem = null;
            _renameEditOriginalLabel = null;
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
                        LoadFolder(currentFolderPath, saveSettings: false);
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

        private static string GetSelectedItemDisplayName(ListBoxItem item)
        {
            if (item.Content is StackPanel panel)
            {
                var text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                if (text != null && !string.IsNullOrWhiteSpace(text.Text))
                {
                    return text.Text.Trim();
                }
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

            if (item.Content is StackPanel panel)
            {
                var text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                if (text != null && !string.IsNullOrWhiteSpace(text.Text) &&
                    text.Text.TrimStart().StartsWith("↩", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string? parentPath = Path.GetDirectoryName(currentFolderPath);
            return !string.IsNullOrWhiteSpace(parentPath) &&
                   string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase);
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

            if (PanelType == PanelKind.List)
            {
                string singleName = GetSelectedItemDisplayName(selectedItems[0]);
                if (!ConfirmDeleteAction(panelOnly: true, selectedItems.Count, singleName))
                {
                    return true;
                }

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
                        var failures = new List<string>();
                        bool deletedAny = false;

                        foreach (var path in realPaths)
                        {
                            if (TryMovePathToRecycleBin(path, out string? error))
                            {
                                deletedAny = true;
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(error))
                            {
                                string displayName = GetDisplayNameForPath(path);
                                if (string.IsNullOrWhiteSpace(displayName))
                                {
                                    displayName = GetPathLeafName(path);
                                }

                                failures.Add($"{displayName}: {error}");
                            }
                        }

                        if (deletedAny)
                        {
                            if (PanelType == PanelKind.Folder &&
                                !string.IsNullOrWhiteSpace(currentFolderPath) &&
                                Directory.Exists(currentFolderPath))
                            {
                                LoadFolder(currentFolderPath, saveSettings: false);
                            }

                            anyChanges = true;
                        }

                        if (failures.Count > 0)
                        {
                            System.Windows.MessageBox.Show(
                                string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, failures)),
                                MainWindow.GetString("Loc.MsgError"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }
                }

                if (pseudoItems.Count > 0)
                {
                    string singleName = GetSelectedItemDisplayName(pseudoItems[0]);
                    if (ConfirmDeleteAction(panelOnly: true, pseudoItems.Count, singleName) &&
                        RemoveItemsFromPanel(pseudoItems))
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

        private void DesktopPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Delete && TryHandleDeleteSelection())
            {
                e.Handled = true;
                return;
            }

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
                return;
            }

            _lastRenameClickItem = null;
            _lastRenameClickUtc = DateTime.MinValue;

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

            bool wasUninitializedBeforeDrop = PanelType == PanelKind.None &&
                                              string.IsNullOrWhiteSpace(currentFolderPath) &&
                                              PinnedItems.Count == 0;

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
                bool initializedByThisDrop = wasUninitializedBeforeDrop &&
                                             ((PanelType == PanelKind.Folder && !string.IsNullOrWhiteSpace(currentFolderPath)) ||
                                              (PanelType == PanelKind.List && PinnedItems.Count > 0));
                if (initializedByThisDrop)
                {
                    ActivateHoverModeForInitializedPanel();
                }

                MainWindow.SaveSettings();
            }

            UpdateDropZoneVisibility();
            return folderChanged || listChanged;
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
