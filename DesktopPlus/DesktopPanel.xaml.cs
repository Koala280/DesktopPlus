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

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private bool isContentVisible = true; // Default: Content is visible
        private double expandedHeight; // Store the full height before collapse
        public string currentFolderPath = ""; // Speichert den aktuellen Ordnerpfad
        private double collapsedTopPosition;
        private bool wasAdjustedOnExpand = false;

        public DesktopPanel()
        {
            InitializeComponent();
            expandedHeight = this.Height; // Store initial height
        }

        private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Collapse_Click(object sender, RoutedEventArgs e)
        {
            if (isContentVisible)
            {
                // Fenster wird eingeklappt → zurück zur alten Position
                if (wasAdjustedOnExpand)
                {
                    this.Top = collapsedTopPosition;
                }

                expandedHeight = this.Height;
                this.Height = 40;
                ContentContainer.Visibility = Visibility.Collapsed;
                isContentVisible = false;
            }
            else
            {
                // Fenster wird ausgeklappt → evtl. nach oben verschieben

                collapsedTopPosition = this.Top; // 🔥 Hier merken, wo es vorher war

                double newHeight = expandedHeight;
                double screenBottom = SystemParameters.WorkArea.Bottom; // berücksichtigt Taskleiste

                if (this.Top + newHeight > screenBottom)
                {
                    this.Top = Math.Max(0, screenBottom - newHeight);
                    wasAdjustedOnExpand = true;
                }
                else
                {
                    wasAdjustedOnExpand = false;
                }

                this.Height = newHeight;
                ContentContainer.Visibility = Visibility.Visible;
                isContentVisible = true;
            }
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
                Source = GetFileIcon(path, isBackButton),
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
                MaxHeight = textSize * 2.4  // für 2 Zeilen
            };

            panel.Children.Add(icon);
            panel.Children.Add(text);
            return panel;
        }

        private ImageSource GetFileIcon(string path, bool isBackButton)
        {
            if (isBackButton || Directory.Exists(path))
            {
                // Spezielles Ordner-Icon laden
                return GetStockIcon(0x3); // 0x3 = Ordner-Icon von Windows
            }

            // Prüfen, ob die Datei existiert
            if (!File.Exists(path))
                return null;

            using (Icon sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path))
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    sysIcon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
        }

        // Windows-API-Aufruf zum Abrufen von System-Icons
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

        // Methode zum Abrufen des Windows-Standardordnersymbols
        private ImageSource GetStockIcon(int iconType)
        {
            SHSTOCKICONINFO sii = new SHSTOCKICONINFO();
            sii.cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO));

            int result = SHGetStockIconInfo(iconType, 0x100, ref sii); // 0x100 = Large Icon

            if (result == 0 && sii.hIcon != IntPtr.Zero)
            {
                ImageSource imgSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    sii.hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // HICON-Handle freigeben, um Speicherlecks zu vermeiden
                DestroyIcon(sii.hIcon);

                return imgSource;
            }
            return null;
        }

        [DllImport("User32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public double zoomFactor = 1.0; // Standard-Zoom-Wert

        public void SetZoom(double newZoom)
        {
            zoomFactor = Math.Max(0.7, Math.Min(1.5, newZoom));
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            foreach (var item in FileList.Items)
            {
                if (item is ListBoxItem listBoxItem && listBoxItem.Content is StackPanel panel)
                {
                    panel.Width = 90 * zoomFactor;
                    panel.Height = (48 * zoomFactor) + 25;

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