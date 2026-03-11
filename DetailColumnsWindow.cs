using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using RelativeSource = System.Windows.Data.RelativeSource;
using RelativeSourceMode = System.Windows.Data.RelativeSourceMode;

namespace DesktopPlus
{
    public sealed class DetailColumnsWindow : Window
    {
        private readonly DetailColumnSelectionState _initialState;
        private readonly Dictionary<string, CheckBox> _togglesByKey =
            new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyList<DetailColumnOption> _availableColumns;
        private readonly StackPanel _columnsListHost = new StackPanel();

        public DetailColumnSelectionState? ResultState { get; private set; }

        public DetailColumnsWindow(
            DetailColumnSelectionState state,
            IReadOnlyList<DetailColumnOption> availableColumns)
        {
            _initialState = state ?? new DetailColumnSelectionState();
            _availableColumns = availableColumns ?? Array.Empty<DetailColumnOption>();

            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("PanelSettingsResources.xaml", UriKind.Relative)
            });

            Title = MainWindow.GetString("Loc.DetailColumnsDialogTitle");
            Width = 400;
            MinHeight = 360;
            MaxHeight = 680;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(26, 29, 35));
            Foreground = new SolidColorBrush(Color.FromRgb(242, 245, 250));
            FontFamily = new FontFamily("Segoe UI");
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(12),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            AppIconLoader.TryApplyWindowIcon(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            var contentHost = BuildContentHost();
            Grid.SetRow(contentHost, 1);
            root.Children.Add(contentHost);

            Content = root;

            PopulateColumnList();
        }

        private UIElement BuildTitleBar()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 36, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 49, 61)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Padding = new Thickness(8, 0, 8, 0)
            };
            border.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
            WindowChrome.SetIsHitTestVisibleInChrome(border, true);

            var grid = new Grid { Height = 36 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconImage = new Image
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(2, 0, 6, 0),
                Source = Icon
            };
            titleStack.Children.Add(iconImage);

            var titleText = new TextBlock
            {
                Text = Title,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleText);

            grid.Children.Add(titleStack);

            var closeButton = new Button
            {
                Style = TryFindResource("TitleBarCloseButton") as Style,
                ToolTip = MainWindow.GetString("Loc.PanelCloseTooltip")
            };
            closeButton.Click += (_, _) => Close();
            WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);

            closeButton.Content = new Path
            {
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 0 0 L 10 10 M 10 0 L 0 10"),
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform
            };
            ((Path)closeButton.Content).SetBinding(Path.StrokeProperty, new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });

            Grid.SetColumn(closeButton, 2);
            grid.Children.Add(closeButton);

            border.Child = grid;
            return border;
        }

        private UIElement BuildContentHost()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 12, 12)
            };
            border.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            var contentGrid = new Grid
            {
                Margin = new Thickness(18)
            };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                Text = MainWindow.GetString("Loc.DetailColumnsDialogHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetRow(intro, 0);
            contentGrid.Children.Add(intro);

            var listCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 36, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 49, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 10)
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 330
            };
            scrollViewer.Style = TryFindResource("ThinScrollViewer") as Style;
            scrollViewer.Content = _columnsListHost;
            listCard.Child = scrollViewer;

            Grid.SetRow(listCard, 1);
            contentGrid.Children.Add(listCard);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = MainWindow.GetString("Loc.Cancel"),
                Width = 118,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Style = TryFindResource("GhostButton") as Style
            };
            cancelButton.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(cancelButton);

            var okButton = new Button
            {
                Content = MainWindow.GetString("Loc.Apply"),
                Width = 118,
                Height = 34,
                IsDefault = true,
                Style = TryFindResource("PrimaryButton") as Style
            };
            okButton.Click += (_, _) =>
            {
                ResultState = BuildResultState();
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(okButton);

            Grid.SetRow(buttons, 2);
            contentGrid.Children.Add(buttons);

            border.Child = contentGrid;
            return border;
        }

        private void PopulateColumnList()
        {
            _columnsListHost.Children.Clear();
            _togglesByKey.Clear();

            foreach (DetailColumnOption option in _availableColumns)
            {
                var toggle = CreateColumnToggle(option.Label, option.IsChecked, option.IsEnabled);
                _columnsListHost.Children.Add(toggle);
                _togglesByKey[option.Key] = toggle;
            }
        }

        private DetailColumnSelectionState BuildResultState()
        {
            var selectedExplorerKeys = _availableColumns
                .Where(option =>
                    ExplorerDetailsColumnProvider.IsExplorerMetadataKey(option.Key) &&
                    _togglesByKey.TryGetValue(option.Key, out CheckBox? toggle) &&
                    toggle.IsChecked == true)
                .Select(option => option.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var reorderedMetadata = DesktopPanel.NormalizeMetadataOrder(_initialState.MetadataOrder)
                .Where(key => !ExplorerDetailsColumnProvider.IsExplorerMetadataKey(key))
                .ToList();

            foreach (string key in _initialState.MetadataOrder
                .Where(ExplorerDetailsColumnProvider.IsExplorerMetadataKey)
                .Where(selectedExplorerKeys.Contains))
            {
                if (!reorderedMetadata.Any(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)))
                {
                    reorderedMetadata.Add(key);
                }
            }

            foreach (string key in _availableColumns
                .Where(option =>
                    ExplorerDetailsColumnProvider.IsExplorerMetadataKey(option.Key) &&
                    selectedExplorerKeys.Contains(option.Key))
                .Select(option => option.Key))
            {
                if (!reorderedMetadata.Any(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)))
                {
                    reorderedMetadata.Add(key);
                }
            }

            return new DetailColumnSelectionState
            {
                ShowType = IsChecked(DesktopPanel.MetadataType),
                ShowSize = IsChecked(DesktopPanel.MetadataSize),
                ShowCreated = IsChecked(DesktopPanel.MetadataCreated),
                ShowModified = IsChecked(DesktopPanel.MetadataModified),
                ShowDimensions = IsChecked(DesktopPanel.MetadataDimensions),
                ShowAuthors = IsChecked(DesktopPanel.MetadataAuthors),
                ShowCategories = IsChecked(DesktopPanel.MetadataCategories),
                ShowTags = IsChecked(DesktopPanel.MetadataTags),
                ShowTitle = IsChecked(DesktopPanel.MetadataTitle),
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(reorderedMetadata),
                MetadataWidths = DesktopPanel.NormalizeMetadataWidths(_initialState.MetadataWidths)
            };
        }

        private CheckBox CreateColumnToggle(string label, bool isChecked, bool isEnabled = true)
        {
            return new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 13,
                Style = TryFindResource("AccentCheckBox") as Style
            };
        }

        private bool IsChecked(string key)
        {
            return _togglesByKey.TryGetValue(key, out CheckBox? toggle) && toggle.IsChecked == true;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }

    public sealed class DetailColumnOption
    {
        public DetailColumnOption(string key, string label, bool isChecked, bool isEnabled)
        {
            Key = key;
            Label = label;
            IsChecked = isChecked;
            IsEnabled = isEnabled;
        }

        public string Key { get; }
        public string Label { get; }
        public bool IsChecked { get; }
        public bool IsEnabled { get; }
    }
}
