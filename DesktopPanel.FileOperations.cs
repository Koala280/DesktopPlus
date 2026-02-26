using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private void MoveFolderIntoCurrent(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return;

            string folderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName)) return;

            string targetPath = Path.Combine(currentFolderPath, folderName);

            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return;

            int counter = 1;
            string baseName = folderName;
            while (Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}");
            }

            try
            {
                Directory.Move(sourcePath, targetPath);
                LoadFolder(currentFolderPath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFolderError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void MoveFileIntoCurrent(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return;

            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) return;

            string targetPath = Path.Combine(currentFolderPath, fileName);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;

            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}{extension}");
            }

            try
            {
                File.Move(sourcePath, targetPath);
                LoadFolder(currentFolderPath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFileError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool ShouldShowPath(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if (!showHiddenItems && (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)))
                {
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static string GetFolderDisplayName(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            string trimmedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string folderName = Path.GetFileName(trimmedPath);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }

            try
            {
                string fallback = new DirectoryInfo(folderPath).Name;
                return string.IsNullOrWhiteSpace(fallback) ? folderPath : fallback;
            }
            catch
            {
                return folderPath;
            }
        }

        private void SetPanelTitleFromFolderPath(string folderPath)
        {
            string folderName = GetFolderDisplayName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return;
            }

            Title = folderName;
            if (PanelTitle != null)
            {
                PanelTitle.Text = folderName;
            }
        }

        public void LoadFolder(string folderPath, bool saveSettings = true, bool renamePanelTitle = false)
        {
            if (!Directory.Exists(folderPath)) return;

            PanelType = PanelKind.Folder;
            currentFolderPath = folderPath;
            PinnedItems.Clear();

            string? parentFolderPath = Path.GetDirectoryName(folderPath);
            this.Title = $"{GetFolderDisplayName(folderPath)}";
            if (renamePanelTitle)
            {
                SetPanelTitleFromFolderPath(folderPath);
            }

            FileList.Items.Clear();

            if (parentFolderPath != null)
            {
                string parentFolderName = Path.GetFileName(parentFolderPath);
                ListBoxItem backItem = new ListBoxItem
                {
                    Content = CreateListBoxItem("↩ " + parentFolderName, parentFolderPath, true, _currentAppearance),
                    Tag = parentFolderPath
                };
                FileList.Items.Add(backItem);
            }

            foreach (var dir in Directory.GetDirectories(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(Path.GetFileName(dir), dir, false, _currentAppearance), Tag = dir });
            }
            foreach (var file in Directory.GetFiles(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(Path.GetFileName(file), file, false, _currentAppearance), Tag = file });
            }

            Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void LoadList(IEnumerable<string> items, bool saveSettings = true)
        {
            PanelType = PanelKind.List;
            currentFolderPath = "";
            FileList.Items.Clear();
            PinnedItems.Clear();

            foreach (var item in items)
            {
                AddFileToList(item, true);
            }

            UpdateDropZoneVisibility();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void ClearPanelItems()
        {
            PanelType = PanelKind.None;
            currentFolderPath = "";
            PinnedItems.Clear();
            FileList.Items.Clear();
            UpdateDropZoneVisibility();
        }

        private void AddFileToList(string filePath, bool trackItem)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            if (trackItem)
            {
                if (PinnedItems.Any(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                if (PanelType == PanelKind.None)
                {
                    PanelType = PanelKind.List;
                }
                PinnedItems.Add(filePath);
            }

            string displayName = Path.GetFileName(filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = filePath;
            }

            ListBoxItem item = new ListBoxItem
            {
                Content = CreateListBoxItem(displayName, filePath, false, _currentAppearance),
                Tag = filePath
            };
            FileList.Items.Add(item);
        }

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is string path)
            {
                if (PanelType == PanelKind.List)
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        string msgKey = Directory.Exists(path) ? "Loc.MsgOpenFolderError" : "Loc.MsgOpenFileError";
                        System.Windows.MessageBox.Show(
                            string.Format(MainWindow.GetString(msgKey), ex.Message),
                            MainWindow.GetString("Loc.MsgError"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    return;
                }

                if (Directory.Exists(path))
                {
                    if (openFoldersExternally)
                    {
                        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show(
                                string.Format(MainWindow.GetString("Loc.MsgOpenFolderError"), ex.Message),
                                MainWindow.GetString("Loc.MsgError"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        LoadFolder(path);
                    }
                }
                else if (File.Exists(path))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            string.Format(MainWindow.GetString("Loc.MsgOpenFileError"), ex.Message),
                            MainWindow.GetString("Loc.MsgError"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text.Trim();
            _searchCts?.Cancel();

            if (string.IsNullOrWhiteSpace(filter))
            {
                if (PanelType == PanelKind.List)
                {
                    foreach (var item in FileList.Items.OfType<ListBoxItem>())
                    {
                        item.Visibility = Visibility.Visible;
                    }
                    return;
                }

                LoadFolder(currentFolderPath);
                return;
            }

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock text)
                {
                    item.Visibility = text.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath))
                return;

            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                var results = await Task.Run(() => EnumerateMatches(currentFolderPath, filter, token), token);
                if (token.IsCancellationRequested) return;

                var existing = new HashSet<string>(FileList.Items.OfType<ListBoxItem>()
                    .Select(i => i.Tag as string ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var foundPath in results)
                {
                    if (existing.Contains(foundPath)) continue;
                    var name = Path.GetFileName(foundPath);
                    FileList.Items.Add(new ListBoxItem
                    {
                        Content = CreateListBoxItem(name, foundPath, false, _currentAppearance),
                        Tag = foundPath
                    });
                }
            }
            catch (OperationCanceledException) { }
        }

        private List<string> EnumerateMatches(string root, string filter, CancellationToken token)
        {
            var list = new List<string>();
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false
                };

                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (!ShouldShowPath(entry)) continue;
                    var name = Path.GetFileName(entry);
                    if (name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(entry);
                        if (list.Count >= 200) break;
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
