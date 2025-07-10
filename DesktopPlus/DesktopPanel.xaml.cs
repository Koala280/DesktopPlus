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
using System.Windows.Shapes;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json; // JSON-Handling hinzufügen
using DesktopPlus;
using System.Printing;
using System.Windows.Interop;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        public bool isContentVisible = true; // Default: Content is visible
        public double expandedHeight; // Store the full height before collapse
        public string currentFolderPath = ""; // Speichert den aktuellen Ordnerpfad
        public double collapsedTopPosition;
        private bool wasAdjustedOnExpand = false;
        public double baseTopPosition; // ← immer die echte Wunschposition (manuell gesetzt)
        private System.Windows.Point _dragStartPoint;
        public static bool StartCollapsedByDefault = true;
        public bool ExpandOnHoverSetting = true;
        public bool ShowMinimizeButton = true;


        public DesktopPanel()
        {
            InitializeComponent();
            expandedHeight = this.Height;
            collapsedTopPosition = this.Top;

            MinimizeButton.Visibility = ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;

            this.LocationChanged += (s, e) => MainWindow.SaveSettings();
            this.SizeChanged += (s, e) => MainWindow.SaveSettings();
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
                    MainWindow.SaveSettings(); // Nach Sortierung speichern
                }
            }
        }

        public void ForceCollapseState(bool isCollapsed)
        {
            if (isCollapsed)
            {
                this.Top = baseTopPosition;
                this.Height = 40;
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
                this.Height = 40;
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
                    wasAdjustedOnExpand = true;
                }
                else
                {
                    this.Top = baseTopPosition;
                    wasAdjustedOnExpand = false;
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

        // Burger-Menü zum Umbenennen
        private void BurgerMenu_Click(object sender, RoutedEventArgs e)
        {
            // Einfache Input-Box (Alternative: eigenes WPF-Fenster erstellen!)
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Neuen Panelnamen eingeben:", "Umbenennen", PanelTitle.Text);

            if (!string.IsNullOrWhiteSpace(input))
            {
                PanelTitle.Text = input;
                this.Title = input;
            }
        }

        private void HeaderBar_MouseEnter(object sender, MouseEventArgs e)
        {
            if (ExpandOnHoverSetting && !isContentVisible)
            {
                Collapse_Click(sender, e);
            }
        }

        private void HeaderBar_MouseLeave(object sender, MouseEventArgs e)
        {
            if (ExpandOnHoverSetting && isContentVisible && !IsMouseOver)
            {
                Collapse_Click(sender, e);
            }
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (ExpandOnHoverSetting && isContentVisible)
            {
                Collapse_Click(sender, e);
            }
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

                foreach (var item in droppedItems)
                {
                    if (Directory.Exists(item))
                    {
                        LoadFolder(item);
                    }
                    else if (File.Exists(item))
                    {
                        AddFileToList(item);
                    }
                }
            }
        }

        public void LoadFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            currentFolderPath = folderPath; // 🔥 Ordnerpfad speichern!

            string parentFolderPath = System.IO.Path.GetDirectoryName(folderPath);
            this.Title = $"{System.IO.Path.GetFileName(folderPath)}";

            FileList.Items.Clear();

            if (parentFolderPath != null)
            {
                string parentFolderName = System.IO.Path.GetFileName(parentFolderPath);
                ListBoxItem backItem = new ListBoxItem
                {
                    Content = CreateListBoxItem("↩ " + parentFolderName, parentFolderPath, true),
                    Tag = parentFolderPath
                };
                FileList.Items.Add(backItem);
            }

            foreach (var dir in Directory.GetDirectories(folderPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(System.IO.Path.GetFileName(dir), dir, false), Tag = dir });
            }
            foreach (var file in Directory.GetFiles(folderPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(System.IO.Path.GetFileName(file), file, false), Tag = file });
            }

            MainWindow.SaveSettings(); // 🔥 Speichern nach dem Laden eines Ordners
        }

        private void AddFileToList(string filePath)
        {
            ListBoxItem item = new ListBoxItem
            {
                Content = CreateListBoxItem(System.IO.Path.GetFileName(filePath), filePath, false),
                Tag = filePath
            };
            FileList.Items.Add(item);
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is string path)
            {
                if (Directory.Exists(path))
                {
                    LoadFolder(path);
                }
                else if (File.Exists(path))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { System.Windows.MessageBox.Show($"Fehler beim Öffnen der Datei:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error); }
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text.Trim();

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock text)
                {
                    if (string.IsNullOrWhiteSpace(filter))
                    {
                        item.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        item.Visibility = text.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            // Toggle collapse instead of minimizing to the taskbar
            Collapse_Click(sender, e);
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            PanelSettingsWindow dlg = new PanelSettingsWindow(PanelTitle.Text, ExpandOnHoverSetting, ShowMinimizeButton)
            {
                Owner = this
            };
            if (dlg.ShowDialog() == true)
            {
                PanelTitle.Text = dlg.PanelName;
                this.Title = dlg.PanelName;
                ExpandOnHoverSetting = dlg.ExpandOnHover;
                ShowMinimizeButton = dlg.ShowMinimizeButton;
                MinimizeButton.Visibility = ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;
                MainWindow.SaveSettings();
            }
        }

        private StackPanel CreateListBoxItem(string displayName, string path, bool isBackButton)
        {
            double iconSize = 48 * zoomFactor;
            double textSize = 14 * zoomFactor;
            double panelWidth = 100 * zoomFactor;

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
                Width = panelWidth - 10
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
                            text.FontSize = 12 * zoomFactor;
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