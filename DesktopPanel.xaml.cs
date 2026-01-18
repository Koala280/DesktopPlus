using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json; // JSON-Handling hinzufügen
using DesktopPlus;
using System.Printing;
using System.Windows.Interop;
using System.Runtime.InteropServices.ComTypes;
using MediaColor = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using System.Threading;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        public bool isContentVisible = true; // Default: Content is visible
        public double expandedHeight; // Store the full height before collapse
        public string currentFolderPath = ""; // Speichert den aktuellen Ordnerpfad
        public double collapsedTopPosition;
        public double baseTopPosition; // ← immer die echte Wunschposition (manuell gesetzt)
        private System.Windows.Point _dragStartPoint;
        public static bool StartCollapsedByDefault = true;
        public static bool ExpandOnHover = true;
        public string assignedPresetName = "";
        public bool showHiddenItems = false;
        public bool expandOnHover = true;
        public bool openFoldersExternally = false;
        public string defaultFolderPath = "";
        private bool _hoverExpanded = false;
        private CancellationTokenSource? _searchCts;
        private MainWindow.AppearanceSettings? _currentAppearance;
        public PanelKind PanelType { get; set; } = PanelKind.None;
        public string PanelId { get; set; } = $"panel:{Guid.NewGuid():N}";
        public List<string> PinnedItems { get; } = new List<string>();


        public DesktopPanel()
        {
            InitializeComponent();
            expandedHeight = this.Height;
            collapsedTopPosition = this.Top;

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

            this.LocationChanged += (s, e) => MainWindow.SaveSettings();
            this.SizeChanged += (s, e) => MainWindow.SaveSettings();
            HeaderBar.MouseEnter += HeaderBar_MouseEnter;
            HeaderBar.MouseLeave += HeaderBar_MouseLeave;
        }

        private void OnAppearanceChanged()
        {
            Dispatcher.Invoke(() => ApplyAppearance(MainWindow.Appearance));
        }

        public void ApplyAppearance(MainWindow.AppearanceSettings appearance)
        {
            if (appearance == null) return;
            _currentAppearance = appearance;

            PanelChrome.CornerRadius = new CornerRadius(appearance.CornerRadius);
            double innerRadius = Math.Max(6, appearance.CornerRadius - 4);
            HeaderBar.CornerRadius = new CornerRadius(innerRadius);
            if (ContentFrame != null)
            {
                ContentFrame.CornerRadius = new CornerRadius(innerRadius);
            }
            if (PanelShadow != null)
            {
                PanelShadow.BlurRadius = Math.Max(0, appearance.ShadowBlur);
                PanelShadow.Opacity = Math.Max(0, Math.Min(1, appearance.ShadowOpacity));
            }

            PanelChrome.Background = MainWindow.BuildBackgroundBrush(appearance, true);
            HeaderBar.Background = CreateBrush(appearance.HeaderColor, 1.0, MediaColor.FromRgb(34, 37, 42));

            var accentBrush = CreateBrush(appearance.AccentColor, 1.0, MediaColor.FromRgb(90, 200, 250));
            PanelTitle.Foreground = accentBrush;
            SearchBox.CaretBrush = accentBrush;
            PanelTitle.FontSize = Math.Max(12, appearance.TitleFontSize);
            SearchBox.FontSize = Math.Max(10, appearance.ItemFontSize - 1);

            ApplyFontFamily(appearance.FontFamily);
            UpdateResourceBrush("PanelText", appearance.TextColor, MediaColor.FromRgb(242, 245, 250));
            UpdateResourceBrush("PanelMuted", appearance.MutedTextColor, MediaColor.FromRgb(167, 176, 192));
            UpdateListItemAppearance();
        }

        private void ApplyFontFamily(string? fontFamily)
        {
            if (string.IsNullOrWhiteSpace(fontFamily)) return;
            try
            {
                FontFamily = new FontFamily(fontFamily);
            }
            catch
            {
                FontFamily = new FontFamily("Segoe UI");
            }
        }

        private void UpdateResourceBrush(string key, string value, MediaColor fallback)
        {
            if (Resources[key] is SolidColorBrush brush)
            {
                brush.Color = CreateBrush(value, 1.0, fallback).Color;
            }
        }

        private void UpdateListItemAppearance()
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance;
            if (appearance == null || FileList == null) return;

            var fileBrush = CreateBrush(appearance.TextColor, 1.0, MediaColor.FromRgb(242, 245, 250));
            var folderBrush = CreateBrush(ResolveFolderColor(appearance), 1.0, MediaColor.FromRgb(110, 139, 255));
            double textSize = Math.Max(8, appearance.ItemFontSize) * zoomFactor;

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is StackPanel panel)
                {
                    var text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                    if (text == null) continue;

                    bool isFolder = IsFolderItem(item.Tag);
                    text.Foreground = isFolder ? folderBrush : fileBrush;
                    text.FontSize = textSize;
                    text.FontFamily = FontFamily;
                }
            }
        }

        private static bool IsFolderItem(object? tag)
        {
            if (tag is string path)
            {
                return Directory.Exists(path);
            }
            return false;
        }

        private static string ResolveFolderColor(MainWindow.AppearanceSettings appearance)
        {
            if (!string.IsNullOrWhiteSpace(appearance.FolderTextColor)) return appearance.FolderTextColor;
            if (!string.IsNullOrWhiteSpace(appearance.AccentColor)) return appearance.AccentColor;
            return "#6E8BFF";
        }

        private SolidColorBrush CreateBrush(string colorValue, double opacity, MediaColor fallback)
        {
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255));

            try
            {
                var parsed = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
                var withOpacity = MediaColor.FromArgb(alpha, parsed.R, parsed.G, parsed.B);
                return new SolidColorBrush(withOpacity);
            }
            catch
            {
                var withOpacity = MediaColor.FromArgb(alpha, fallback.R, fallback.G, fallback.B);
                return new SolidColorBrush(withOpacity);
            }
        }

        private double GetCollapsedHeight()
        {
            double headerHeight = (HeaderBar != null && HeaderBar.ActualHeight > 0) ? HeaderBar.ActualHeight : 52;
            double padding = (PanelChrome != null) ? PanelChrome.Padding.Top + PanelChrome.Padding.Bottom : 12;
            return Math.Max(60, headerHeight + padding);
        }
        private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void FileList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                System.Windows.Point currentPosition = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is System.Windows.Controls.ListBox listBox && listBox.SelectedItem is ListBoxItem sourceItem)
                    {
                        if (sourceItem.Content is StackPanel panel)
                        {
                            panel.Opacity = 0.8; // beim Start sichtbar ändern
                        }

                        DragDrop.DoDragDrop(listBox, sourceItem, System.Windows.DragDropEffects.Move);

                        // 🧼 Opacity zurücksetzen NACH Drag
                        if (sourceItem.Content is StackPanel releasedPanel)
                        {
                            releasedPanel.Opacity = 1.0;
                        }
                    }
                }
            }
        }

        private void FileList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(ListBoxItem)))
            {
                var sourceItem = e.Data.GetData(typeof(ListBoxItem)) as ListBoxItem;
                var target = e.OriginalSource as FrameworkElement;

                while (target != null && !(target.DataContext is ListBoxItem) && !(target is ListBoxItem))
                    target = VisualTreeHelper.GetParent(target) as FrameworkElement;

                var targetItem = target as ListBoxItem ?? (target?.DataContext as ListBoxItem);

                if (sourceItem != null && targetItem != null && !ReferenceEquals(sourceItem, targetItem))
                {
                    int sourceIndex = FileList.Items.IndexOf(sourceItem);
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
                    MainWindow.SaveSettings(); // Nach Sortierung speichern
                }
            }
        }

        public void ForceCollapseState(bool isCollapsed)
        {
            if (isCollapsed)
            {
                this.Top = baseTopPosition;
                this.Height = GetCollapsedHeight();
                ContentContainer.Visibility = Visibility.Collapsed;
                isContentVisible = false;
            }
            else
            {
                double screenBottom = SystemParameters.WorkArea.Bottom;
                if (baseTopPosition + expandedHeight > screenBottom)
                    this.Top = Math.Max(0, screenBottom - expandedHeight);
                else
                    this.Top = baseTopPosition;

                this.Height = expandedHeight;
                ContentContainer.Visibility = Visibility.Visible;
                isContentVisible = true;
            }
        }

        private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
                baseTopPosition = this.Top; // ← speichert neue "manuelle" Position
                collapsedTopPosition = baseTopPosition;
                MainWindow.SaveSettings();
            }
        }

        private void Collapse_Click(object sender, RoutedEventArgs e)
        {
            if (isContentVisible)
            {
                expandedHeight = this.Height;
                this.Top = baseTopPosition;
                this.Height = GetCollapsedHeight();
                ContentContainer.Visibility = Visibility.Collapsed;
                isContentVisible = false;
            }
            else
            {
                double newHeight = expandedHeight;
                double screenBottom = SystemParameters.WorkArea.Bottom;

                // Bei Bedarf visuell nach oben verschieben
                if (baseTopPosition + newHeight > screenBottom)
                {
                    this.Top = Math.Max(0, screenBottom - newHeight);
                }
                else
                {
                    this.Top = baseTopPosition;
                }

                this.Height = newHeight;
                ContentContainer.Visibility = Visibility.Visible;
                isContentVisible = true;
            }
            MainWindow.SaveSettings();
        }

        // Schließen des Fensters
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // Öffnet Panel-Settings
        private void BurgerMenu_Click(object sender, RoutedEventArgs e)
        {
            var settings = new PanelSettings(this);
            settings.Owner = this;
            settings.ShowDialog();
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;

                // Zoom-Faktor anpassen (0.1 Schritte, min. 0.7x, max. 2.0x)
                zoomFactor += (e.Delta > 0) ? 0.1 : -0.1;
                zoomFactor = Math.Max(0.7, Math.Min(2.0, zoomFactor));

                // Zoom auf Listenelemente anwenden
                ApplyZoom();
            }
        }


        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] droppedItems = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
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
                        LoadFolder(initialFolder);
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
                                LoadFolder(item);
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
            }
        }

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void HeaderBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!expandOnHover) return;
            if (!isContentVisible)
            {
                ForceCollapseState(false);
                _hoverExpanded = true;
            }
        }

        private void HeaderBar_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_hoverExpanded && expandOnHover)
            {
                ForceCollapseState(true);
                _hoverExpanded = false;
            }
        }

        private void MoveFolderIntoCurrent(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return;

            string folderName = System.IO.Path.GetFileName(sourcePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName)) return;

            string targetPath = System.IO.Path.Combine(currentFolderPath, folderName);

            // identischer Pfad? Dann abbrechen
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return;

            int counter = 1;
            string baseName = folderName;
            while (Directory.Exists(targetPath))
            {
                targetPath = System.IO.Path.Combine(currentFolderPath, $"{baseName}_{counter++}");
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

            string fileName = System.IO.Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) return;

            string targetPath = System.IO.Path.Combine(currentFolderPath, fileName);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return;

            string baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string extension = System.IO.Path.GetExtension(fileName);
            int counter = 1;

            while (File.Exists(targetPath))
            {
                targetPath = System.IO.Path.Combine(currentFolderPath, $"{baseName}_{counter++}{extension}");
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

        public void LoadFolder(string folderPath, bool saveSettings = true)
        {
            if (!Directory.Exists(folderPath)) return;

            PanelType = PanelKind.Folder;
            currentFolderPath = folderPath; // 🔥 Ordnerpfad speichern!
            PinnedItems.Clear();

            string parentFolderPath = System.IO.Path.GetDirectoryName(folderPath);
            this.Title = $"{System.IO.Path.GetFileName(folderPath)}";

            FileList.Items.Clear();

            if (parentFolderPath != null)
            {
                string parentFolderName = System.IO.Path.GetFileName(parentFolderPath);
                ListBoxItem backItem = new ListBoxItem
                {
                    Content = CreateListBoxItem("↩ " + parentFolderName, parentFolderPath, true, _currentAppearance),
                    Tag = parentFolderPath
                };
                FileList.Items.Add(backItem);
            }

            foreach (var dir in Directory.GetDirectories(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(System.IO.Path.GetFileName(dir), dir, false, _currentAppearance), Tag = dir });
            }
            foreach (var file in Directory.GetFiles(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(System.IO.Path.GetFileName(file), file, false, _currentAppearance), Tag = file });
            }

            if (saveSettings)
            {
                MainWindow.SaveSettings(); // 🔥 Speichern nach dem Laden eines Ordners
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

            string displayName = System.IO.Path.GetFileName(filePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
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

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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

                foreach (var path in results)
                {
                    if (existing.Contains(path)) continue;
                    var name = System.IO.Path.GetFileName(path);
                    FileList.Items.Add(new ListBoxItem
                    {
                        Content = CreateListBoxItem(name, path, false, _currentAppearance),
                        Tag = path
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
                    var name = System.IO.Path.GetFileName(entry);
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

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private StackPanel CreateListBoxItem(string displayName, string path, bool isBackButton, MainWindow.AppearanceSettings? appearance = null)
        {
            var activeAppearance = appearance ?? _currentAppearance ?? MainWindow.Appearance;
            double iconSize = 48 * zoomFactor;
            double textSize = Math.Max(8, activeAppearance.ItemFontSize) * zoomFactor;
            double panelWidth = 100 * zoomFactor;
            bool isFolder = isBackButton || Directory.Exists(path);
            var textBrush = CreateBrush(
                isFolder ? ResolveFolderColor(activeAppearance) : activeAppearance.TextColor,
                1.0,
                MediaColor.FromRgb(242, 245, 250));

            StackPanel panel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Width = panelWidth,
                Margin = new Thickness(5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
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

            TextBlock text = new TextBlock
            {
                Text = displayName,
                FontSize = textSize,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = panelWidth - 10,
                Foreground = textBrush,
                Opacity = 0.92
            };

            panel.Children.Add(icon);
            panel.Children.Add(text);
            panel.PreviewMouseLeftButtonDown += (s, e) =>
            {
                panel.Opacity = 0.8;
            };

            panel.MouseLeftButtonUp += (s, e) =>
            {
                panel.Opacity = 1.0;
            };
            return panel;
        }


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

        private ImageSource LoadExplorerStyleIcon(string filePath, int size = 256)
        {
            try
            {
                Guid iidImageFactory = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(filePath, null, iidImageFactory, out IShellItemImageFactory imageFactory);

                SIZE iconSize = new SIZE(size, size);

                imageFactory.GetImage(iconSize, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);

                if (hBitmap != IntPtr.Zero)
                {
                    var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(size, size));

                    DeleteObject(hBitmap); // Speicher freigeben
                    return bitmapSource;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon konnte nicht geladen werden: {ex.Message}");
            }

            return null;
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);


        public double zoomFactor = 1.0; // Standard-Zoom-Wert

        public void SetZoom(double newZoom)
        {
            zoomFactor = Math.Max(0.7, Math.Min(1.5, newZoom));
            ApplyZoom();
            MainWindow.SaveSettings(); // Zoom speichern
        }

        private void ApplyZoom()
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance;
            double baseTextSize = appearance != null ? Math.Max(8, appearance.ItemFontSize) : 12;
            foreach (var item in FileList.Items)
            {
                if (item is ListBoxItem listBoxItem && listBoxItem.Content is StackPanel panel)
                {
                    panel.Width = 90 * zoomFactor;

                    foreach (var child in panel.Children)
                    {
                        if (child is System.Windows.Controls.Image icon)
                        {
                            icon.Width = 48 * zoomFactor;
                            icon.Height = 48 * zoomFactor;
                        }
                        else if (child is TextBlock text)
                        {
                            text.FontSize = baseTextSize * zoomFactor;
                            text.Width = 85 * zoomFactor;

                            // Wichtig: entferne MaxHeight, falls irgendwo gesetzt
                            text.MaxHeight = double.PositiveInfinity;
                        }
                    }
                }
            }
        }


        private void ScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Wenn STRG gedrückt wird → Zoom
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;
                zoomFactor += (e.Delta > 0) ? 0.1 : -0.1;
                zoomFactor = Math.Max(0.7, Math.Min(2.0, zoomFactor)); // Min. 0.7x, max. 2.0x
                ApplyZoom();
            }
            else
            {
                // Normales Scrollen im ScrollViewer
                if (sender is ScrollViewer scrollViewer)
                {
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3));
                }
            }
        }
    }
}
