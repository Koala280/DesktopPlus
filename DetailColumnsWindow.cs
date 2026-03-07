using System;
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
        private readonly CheckBox _typeToggle;
        private readonly CheckBox _sizeToggle;
        private readonly CheckBox _createdToggle;
        private readonly CheckBox _modifiedToggle;
        private readonly CheckBox _dimensionsToggle;
        private readonly CheckBox _authorsToggle;
        private readonly CheckBox _categoriesToggle;
        private readonly CheckBox _tagsToggle;
        private readonly CheckBox _titleToggle;
        private readonly StackPanel _columnsListHost = new StackPanel();

        public DetailColumnSelectionState? ResultState { get; private set; }

        public DetailColumnsWindow(DetailColumnSelectionState state)
        {
            _initialState = state ?? new DetailColumnSelectionState();

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

            _modifiedToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaModified"), _initialState.ShowModified);
            _typeToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaType"), _initialState.ShowType);
            _sizeToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaSize"), _initialState.ShowSize);
            _createdToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaCreated"), _initialState.ShowCreated);
            _dimensionsToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaDimensions"), _initialState.ShowDimensions);
            _authorsToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaAuthors"), _initialState.ShowAuthors);
            _categoriesToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaCategories"), _initialState.ShowCategories);
            _tagsToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaTags"), _initialState.ShowTags);
            _titleToggle = CreateColumnToggle(MainWindow.GetString("Loc.PanelSettingsMetaTitle"), _initialState.ShowTitle);

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
            _columnsListHost.Children.Add(CreateColumnToggle(
                MainWindow.GetString("Loc.DetailColumnName"),
                isChecked: true,
                isEnabled: false));

            _columnsListHost.Children.Add(_modifiedToggle);
            _columnsListHost.Children.Add(_typeToggle);
            _columnsListHost.Children.Add(_sizeToggle);
            _columnsListHost.Children.Add(_createdToggle);
            _columnsListHost.Children.Add(_dimensionsToggle);
            _columnsListHost.Children.Add(_authorsToggle);
            _columnsListHost.Children.Add(_categoriesToggle);
            _columnsListHost.Children.Add(_tagsToggle);
            _columnsListHost.Children.Add(_titleToggle);
        }

        private DetailColumnSelectionState BuildResultState()
        {
            return new DetailColumnSelectionState
            {
                ShowType = _typeToggle.IsChecked == true,
                ShowSize = _sizeToggle.IsChecked == true,
                ShowCreated = _createdToggle.IsChecked == true,
                ShowModified = _modifiedToggle.IsChecked == true,
                ShowDimensions = _dimensionsToggle.IsChecked == true,
                ShowAuthors = _authorsToggle.IsChecked == true,
                ShowCategories = _categoriesToggle.IsChecked == true,
                ShowTags = _tagsToggle.IsChecked == true,
                ShowTitle = _titleToggle.IsChecked == true,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(_initialState.MetadataOrder),
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

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
