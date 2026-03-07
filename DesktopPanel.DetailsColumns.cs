using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace DesktopPlus
{
    public partial class DesktopPanel
    {
        private static readonly string[] DetailsContextMenuColumnKeys =
        {
            MetadataName,
            MetadataModified,
            MetadataType,
            MetadataSize,
            MetadataCreated,
            MetadataDimensions,
            MetadataAuthors,
            MetadataCategories,
            MetadataTags,
            MetadataTitle
        };

        public DetailColumnSelectionState CreateDetailColumnSelectionState()
        {
            return new DetailColumnSelectionState
            {
                ShowType = showMetadataType,
                ShowSize = showMetadataSize,
                ShowCreated = showMetadataCreated,
                ShowModified = showMetadataModified,
                ShowDimensions = showMetadataDimensions,
                ShowAuthors = showMetadataAuthors,
                ShowCategories = showMetadataCategories,
                ShowTags = showMetadataTags,
                ShowTitle = showMetadataTitle,
                MetadataOrder = NormalizeMetadataOrder(metadataOrder),
                MetadataWidths = NormalizeMetadataWidths(metadataWidths)
            };
        }

        public void ApplyDetailColumnSelectionState(DetailColumnSelectionState? state, bool persistSettings = true)
        {
            if (state == null)
            {
                return;
            }

            ApplyViewSettings(
                viewMode,
                state.ShowType,
                state.ShowSize,
                state.ShowCreated,
                state.ShowModified,
                state.ShowDimensions,
                state.ShowAuthors,
                state.ShowCategories,
                state.ShowTags,
                state.ShowTitle,
                metadataOrderOverride: state.MetadataOrder,
                metadataWidthsOverride: state.MetadataWidths,
                persistSettings: persistSettings);
        }

        private bool OpenDetailsColumnPickerDialog(Window? owner = null)
        {
            var dialog = new DetailColumnsWindow(CreateDetailColumnSelectionState())
            {
                Owner = owner ?? this
            };

            if (dialog.ShowDialog() != true || dialog.ResultState == null)
            {
                return false;
            }

            ApplyDetailColumnSelectionState(dialog.ResultState);
            return true;
        }

        private bool IsDetailsMetadataColumnVisible(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataType;
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataSize;
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataCreated;
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataModified;
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataDimensions;
            }

            if (string.Equals(metadataKey, MetadataAuthors, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataAuthors;
            }

            if (string.Equals(metadataKey, MetadataCategories, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataCategories;
            }

            if (string.Equals(metadataKey, MetadataTags, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataTags;
            }

            if (string.Equals(metadataKey, MetadataTitle, StringComparison.OrdinalIgnoreCase))
            {
                return showMetadataTitle;
            }

            return false;
        }

        private void SetDetailsMetadataColumnVisible(string metadataKey, bool isVisible)
        {
            bool changed = false;

            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase) &&
                showMetadataType != isVisible)
            {
                showMetadataType = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase) &&
                showMetadataSize != isVisible)
            {
                showMetadataSize = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase) &&
                showMetadataCreated != isVisible)
            {
                showMetadataCreated = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase) &&
                showMetadataModified != isVisible)
            {
                showMetadataModified = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase) &&
                showMetadataDimensions != isVisible)
            {
                showMetadataDimensions = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataAuthors, StringComparison.OrdinalIgnoreCase) &&
                showMetadataAuthors != isVisible)
            {
                showMetadataAuthors = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataCategories, StringComparison.OrdinalIgnoreCase) &&
                showMetadataCategories != isVisible)
            {
                showMetadataCategories = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataTags, StringComparison.OrdinalIgnoreCase) &&
                showMetadataTags != isVisible)
            {
                showMetadataTags = isVisible;
                changed = true;
            }
            else if (string.Equals(metadataKey, MetadataTitle, StringComparison.OrdinalIgnoreCase) &&
                showMetadataTitle != isVisible)
            {
                showMetadataTitle = isVisible;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            RebuildListItemVisuals(sortItems: false);
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        private double GetStoredDetailsColumnWidth(string metadataKey)
        {
            metadataWidths = NormalizeMetadataWidths(metadataWidths);
            return metadataWidths.TryGetValue(metadataKey, out double width) && width > 0
                ? width
                : GetDefaultMetadataWidth(metadataKey);
        }

        private void SetStoredDetailsColumnWidth(string metadataKey, double width)
        {
            string normalizedKey = string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase)
                ? MetadataName
                : NormalizeMetadataKey(metadataKey);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                return;
            }

            metadataWidths = NormalizeMetadataWidths(metadataWidths);
            metadataWidths[normalizedKey] = Math.Max(GetMinimumDetailsColumnWidth(normalizedKey), width);
        }

        private static double GetMinimumDetailsColumnWidth(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase))
            {
                return 170;
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return 74;
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return 88;
            }

            return 96;
        }

        private List<string> GetVisibleDetailsMetadataColumns()
        {
            return NormalizeMetadataOrder(metadataOrder)
                .Where(key =>
                    !string.Equals(key, MetadataName, StringComparison.OrdinalIgnoreCase) &&
                    IsDetailsMetadataColumnVisible(key))
                .ToList();
        }

        private List<string> GetVisibleDetailsColumns()
        {
            return NormalizeMetadataOrder(metadataOrder)
                .Where(key =>
                    string.Equals(key, MetadataName, StringComparison.OrdinalIgnoreCase) ||
                    IsDetailsMetadataColumnVisible(key))
                .ToList();
        }

        private Dictionary<string, double> GetActualDetailsColumnWidths(double totalWidth)
        {
            var visibleColumns = GetVisibleDetailsColumns();
            double minimumRequiredWidth = visibleColumns.Sum(GetMinimumDetailsColumnWidth);
            double availableWidth = Math.Max(minimumRequiredWidth, totalWidth);
            var actualWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var preferredWidths = visibleColumns.ToDictionary(
                key => key,
                key => GetStoredDetailsColumnWidth(key),
                StringComparer.OrdinalIgnoreCase);

            double preferredTotal = preferredWidths.Values.Sum();

            if (preferredTotal > availableWidth && preferredWidths.Count > 0)
            {
                var remainingKeys = new List<string>(preferredWidths.Keys);
                double remainingBudget = availableWidth;

                while (remainingKeys.Count > 0)
                {
                    double remainingPreferred = remainingKeys.Sum(key => preferredWidths[key]);
                    bool clampedAny = false;

                    foreach (string key in remainingKeys.ToList())
                    {
                        double scaledWidth = remainingPreferred <= 0
                            ? 0
                            : preferredWidths[key] * (remainingBudget / remainingPreferred);
                        double minWidth = GetMinimumDetailsColumnWidth(key);
                        if (scaledWidth + 0.5 >= minWidth)
                        {
                            continue;
                        }

                        actualWidths[key] = minWidth;
                        remainingBudget -= minWidth;
                        remainingKeys.Remove(key);
                        clampedAny = true;
                    }

                    if (!clampedAny)
                    {
                        foreach (string key in remainingKeys)
                        {
                            double scaledWidth = remainingPreferred <= 0
                                ? GetMinimumDetailsColumnWidth(key)
                                : preferredWidths[key] * (remainingBudget / remainingPreferred);
                            actualWidths[key] = Math.Max(GetMinimumDetailsColumnWidth(key), scaledWidth);
                        }

                        remainingKeys.Clear();
                    }
                }
            }
            else
            {
                foreach (var pair in preferredWidths)
                {
                    actualWidths[pair.Key] = pair.Value;
                }
            }

            if (visibleColumns.Count > 0 && actualWidths.TryGetValue(MetadataName, out double nameWidth))
            {
                double remainingWidth = availableWidth - actualWidths.Values.Sum();
                actualWidths[MetadataName] = Math.Max(GetMinimumDetailsColumnWidth(MetadataName), nameWidth + remainingWidth);
            }

            return actualWidths;
        }

        private string GetDetailsColumnLabelText(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.DetailColumnName");
            }

            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaType");
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaSize");
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaCreated");
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaModified");
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaDimensions");
            }

            if (string.Equals(metadataKey, MetadataAuthors, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaAuthors");
            }

            if (string.Equals(metadataKey, MetadataCategories, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaCategories");
            }

            if (string.Equals(metadataKey, MetadataTags, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaTags");
            }

            if (string.Equals(metadataKey, MetadataTitle, StringComparison.OrdinalIgnoreCase))
            {
                return MainWindow.GetString("Loc.PanelSettingsMetaTitle");
            }

            return metadataKey;
        }

        private string GetDetailsShellMetadataText(string metadataKey, string path)
        {
            string value = ShellPropertyReader.GetValue(path, metadataKey);
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private string GetDetailsColumnValueText(string metadataKey, string displayName, string path, bool isFolder, bool isBackButton)
        {
            if (string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }

            if (isBackButton)
            {
                return string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase)
                    ? GetItemTypeText(path, isBackButton: true, isFolder)
                    : "-";
            }

            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return GetItemTypeText(path, isBackButton: false, isFolder);
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return GetSizeText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return GetCreatedText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return GetModifiedText(path, isFolder);
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return GetDimensionsText(path, isFolder);
            }

            if (isFolder)
            {
                return "-";
            }

            if (string.Equals(metadataKey, MetadataAuthors, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(metadataKey, MetadataCategories, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(metadataKey, MetadataTags, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(metadataKey, MetadataTitle, StringComparison.OrdinalIgnoreCase))
            {
                return GetDetailsShellMetadataText(metadataKey, path);
            }

            return "-";
        }

        private string GetComparableDetailsText(string metadataKey, string path, bool isFolder)
        {
            string displayName = GetDisplayNameForPath(path);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = GetPathLeafName(path);
            }

            string value = GetDetailsColumnValueText(metadataKey, displayName, path, isFolder, isBackButton: false);
            return string.Equals(value, "-", StringComparison.Ordinal) ? string.Empty : value;
        }

        private DetailsSortColumn MapDetailsColumnToSortColumn(string metadataKey)
        {
            if (string.Equals(metadataKey, MetadataType, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Type;
            }

            if (string.Equals(metadataKey, MetadataSize, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Size;
            }

            if (string.Equals(metadataKey, MetadataCreated, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Created;
            }

            if (string.Equals(metadataKey, MetadataModified, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Modified;
            }

            if (string.Equals(metadataKey, MetadataDimensions, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Dimensions;
            }

            if (string.Equals(metadataKey, MetadataAuthors, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Authors;
            }

            if (string.Equals(metadataKey, MetadataCategories, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Categories;
            }

            if (string.Equals(metadataKey, MetadataTags, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Tags;
            }

            if (string.Equals(metadataKey, MetadataTitle, StringComparison.OrdinalIgnoreCase))
            {
                return DetailsSortColumn.Title;
            }

            return DetailsSortColumn.Name;
        }

        private bool IsDetailsColumnSorted(string metadataKey)
        {
            return _detailsSortActive &&
                _detailsSortColumn == MapDetailsColumnToSortColumn(metadataKey);
        }

        private void ApplyDetailsHeaderSort(string metadataKey)
        {
            DetailsSortColumn clickedColumn = MapDetailsColumnToSortColumn(metadataKey);
            if (!_detailsSortActive)
            {
                _detailsSortActive = true;
                _detailsSortColumn = clickedColumn;
                _detailsSortAscending = true;
            }
            else if (_detailsSortColumn != clickedColumn)
            {
                _detailsSortColumn = clickedColumn;
                _detailsSortAscending = true;
            }
            else if (_detailsSortAscending)
            {
                _detailsSortAscending = false;
            }
            else
            {
                _detailsSortActive = false;
                _detailsSortColumn = DetailsSortColumn.Name;
                _detailsSortAscending = true;
                RestoreDefaultDetailsOrderInPlace();

                bool hadActiveSearchRequest = _searchCts != null &&
                    PanelType == PanelKind.Folder &&
                    !string.IsNullOrWhiteSpace(SearchBox?.Text);
                if (hadActiveSearchRequest)
                {
                    _deferSortUntilSearchComplete = false;
                }

                RebuildListItemVisuals(sortItems: false);
                return;
            }

            bool hasActiveSearchRequest = _searchCts != null &&
                PanelType == PanelKind.Folder &&
                !string.IsNullOrWhiteSpace(SearchBox?.Text);
            if (hasActiveSearchRequest)
            {
                _deferSortUntilSearchComplete = true;
                RefreshParentNavigationItemVisual();
                RefreshDetailsHeader();
                return;
            }

            SortCurrentFolderItemsInPlace();
            RebuildListItemVisuals(sortItems: false);
        }

        private void RefreshDetailsHeader()
        {
            if (DetailsHeaderBorder == null || DetailsHeaderGrid == null)
            {
                return;
            }

            ClearDetailsHeaderDropPreview();
            bool isDetailsMode = string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase);
            bool hasEmbeddedParentNavigation = TryGetEmbeddedParentNavigationPath(out string parentNavigationPath);
            UpdateDetailsHeaderNavigationButton(hasEmbeddedParentNavigation, parentNavigationPath);

            if (!isDetailsMode || FileList == null)
            {
                SyncParentNavigationItemVisibility();
                DetailsHeaderBorder.Visibility = Visibility.Collapsed;
                DetailsHeaderGrid.Children.Clear();
                DetailsHeaderGrid.ColumnDefinitions.Clear();
                DetailsHeaderGrid.RowDefinitions.Clear();
                return;
            }

            var appearance = _currentAppearance ?? MainWindow.Appearance ?? new AppearanceSettings();
            Brush labelBrush = CreateBrush(
                appearance.MutedTextColor,
                1.0,
                Color.FromRgb(167, 176, 192));
            Brush accentBrush = CreateBrush(
                appearance.TextColor,
                1.0,
                Color.FromRgb(242, 245, 250));
            Brush separatorBrush = TryFindResource("PanelBorder") as Brush ??
                new SolidColorBrush(Color.FromArgb(0x55, 0x66, 0x70, 0x80));

            double totalWidth = GetDetailsItemWidth();
            var actualWidths = GetActualDetailsColumnWidths(totalWidth);
            var visibleColumns = GetVisibleDetailsColumns();
            SyncParentNavigationItemVisibility();
            DetailsHeaderBorder.Visibility = Visibility.Visible;
            DetailsHeaderGrid.Width = totalWidth;
            DetailsHeaderGrid.Children.Clear();
            DetailsHeaderGrid.ColumnDefinitions.Clear();
            DetailsHeaderGrid.RowDefinitions.Clear();
            DetailsHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < visibleColumns.Count; i++)
            {
                string key = visibleColumns[i];
                DetailsHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(actualWidths.TryGetValue(key, out double width) ? width : GetStoredDetailsColumnWidth(key))
                });

                Border cell = CreateDetailsHeaderCell(
                    key,
                    showRightDivider: i < visibleColumns.Count - 1,
                    labelBrush,
                    accentBrush,
                    separatorBrush);
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, i);
                DetailsHeaderGrid.Children.Add(cell);
            }
        }

        private bool TryGetEmbeddedParentNavigationPath(out string parentPath)
        {
            parentPath = string.Empty;

            string normalizedViewMode = NormalizeViewMode(viewMode);
            bool useHeaderBackButton =
                string.Equals(normalizedViewMode, ViewModeDetails, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedViewMode, ViewModeIcons, StringComparison.OrdinalIgnoreCase);

            if (!useHeaderBackButton ||
                PanelType != PanelKind.Folder ||
                !showParentNavigationItem ||
                string.IsNullOrWhiteSpace(currentFolderPath))
            {
                return false;
            }

            try
            {
                parentPath = Path.GetDirectoryName(
                    currentFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
            }
            catch
            {
                parentPath = Path.GetDirectoryName(currentFolderPath) ?? string.Empty;
            }

            return !string.IsNullOrWhiteSpace(parentPath);
        }

        private bool ShouldShowParentNavigationListItem()
        {
            return showParentNavigationItem && !TryGetEmbeddedParentNavigationPath(out _);
        }

        private void SyncParentNavigationItemVisibility()
        {
            if (FileList == null)
            {
                return;
            }

            var backItem = FileList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(IsParentNavigationItem);
            if (backItem == null)
            {
                return;
            }

            Visibility targetVisibility = ShouldShowParentNavigationListItem()
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (backItem.Visibility != targetVisibility)
            {
                backItem.Visibility = targetVisibility;
            }
        }

        private void UpdateDetailsHeaderNavigationButton(bool isVisible, string parentPath)
        {
            if (HeaderBackButton == null)
            {
                return;
            }

            if (!isVisible || string.IsNullOrWhiteSpace(parentPath))
            {
                HeaderBackButton.Tag = null;
                HeaderBackButton.Visibility = Visibility.Collapsed;
                return;
            }

            HeaderBackButton.Tag = parentPath;
            HeaderBackButton.Visibility = Visibility.Visible;
        }

        private void HeaderBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element ||
                element.Tag is not string parentPath ||
                string.IsNullOrWhiteSpace(parentPath))
            {
                return;
            }

            LoadFolder(parentPath);
            e.Handled = true;
        }

        private Border CreateDetailsHeaderCell(
            string metadataKey,
            bool showRightDivider,
            Brush labelBrush,
            Brush accentBrush,
            Brush separatorBrush,
            bool hideLabel = false)
        {
            bool isSorted = IsDetailsColumnSorted(metadataKey);
            bool isNameColumn = string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase);
            bool suppressNameHeader = hideLabel && isNameColumn;

            var cell = new Border
            {
                Tag = metadataKey,
                Background = Brushes.Transparent,
                BorderBrush = separatorBrush,
                BorderThickness = showRightDivider ? new Thickness(0, 0, 1, 0) : new Thickness(0),
                Padding = isNameColumn
                    ? new Thickness(12, 0, 10, 0)
                    : new Thickness(10, 0, 10, 0),
                Cursor = suppressNameHeader ? Cursors.Arrow : GetDetailsHeaderDefaultCursor(metadataKey),
                AllowDrop = true,
                RenderTransform = new TranslateTransform()
            };

            var label = new TextBlock
            {
                Text = suppressNameHeader ? string.Empty : GetDetailsColumnLabelText(metadataKey),
                Foreground = isSorted ? accentBrush : labelBrush,
                FontSize = 12,
                FontWeight = isSorted ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var wrapper = new Grid();
            wrapper.Children.Add(label);

            if (isSorted && !suppressNameHeader)
            {
                var chevron = new System.Windows.Shapes.Path
                {
                    Data = GetMetadataSortChevronGeometry(_detailsSortAscending),
                    Width = 7,
                    Height = 4,
                    Stretch = Stretch.Fill,
                    Fill = accentBrush,
                    SnapsToDevicePixels = true,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -1, 0, 0),
                    IsHitTestVisible = false
                };
                wrapper.Children.Add(chevron);
            }

            cell.Child = wrapper;
            cell.PreviewMouseLeftButtonDown += DetailsHeaderCell_PreviewMouseLeftButtonDown;
            cell.PreviewMouseMove += DetailsHeaderCell_PreviewMouseMove;
            cell.PreviewMouseLeftButtonUp += DetailsHeaderCell_PreviewMouseLeftButtonUp;
            cell.MouseRightButtonUp += DetailsHeaderCell_MouseRightButtonUp;
            return cell;
        }

        private void DetailsHeaderCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border cell || cell.Tag is not string metadataKey)
            {
                return;
            }

            if (TryBeginDetailsHeaderResize(cell, metadataKey, e))
            {
                e.Handled = true;
                return;
            }

            _detailsHeaderDragSource = cell;
            _detailsHeaderDragKey = metadataKey;
            _detailsHeaderDragStartPoint = e.GetPosition(DetailsHeaderGrid);
            e.Handled = true;
        }

        private void DetailsHeaderCell_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Border hoverCell && hoverCell.Tag is string hoverMetadataKey && e.LeftButton != MouseButtonState.Pressed)
            {
                hoverCell.Cursor = TryGetDetailsHeaderResizePair(
                    hoverCell,
                    e.GetPosition(hoverCell),
                    out _,
                    out _)
                        ? Cursors.SizeWE
                        : GetDetailsHeaderDefaultCursor(hoverMetadataKey);
            }

            if (_detailsHeaderResizing)
            {
                e.Handled = true;
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed ||
                _detailsHeaderDragSource == null ||
                string.IsNullOrWhiteSpace(_detailsHeaderDragKey) ||
                DetailsHeaderGrid == null)
            {
                return;
            }

            Point current = e.GetPosition(DetailsHeaderGrid);
            if (!_detailsHeaderDragging)
            {
                if (Math.Abs(current.X - _detailsHeaderDragStartPoint.X) < DetailsHeaderDragThreshold &&
                    Math.Abs(current.Y - _detailsHeaderDragStartPoint.Y) < DetailsHeaderDragThreshold)
                {
                    return;
                }

                _detailsHeaderDragging = true;
                _detailsHeaderDragSource.Opacity = 0.72;
                System.Windows.Controls.Panel.SetZIndex(_detailsHeaderDragSource, 3);
                Mouse.Capture(DetailsHeaderGrid, CaptureMode.SubTree);
            }

            UpdateDetailsHeaderDragPreview(current);
            e.Handled = true;
        }

        private void UpdateDetailsHeaderDragPreview(Point mousePositionInGrid)
        {
            if (_detailsHeaderDragSource == null || DetailsHeaderGrid == null)
            {
                return;
            }

            var reorderableCells = GetReorderableDetailsHeaderCells();
            int sourceIndex = reorderableCells.IndexOf(_detailsHeaderDragSource);
            if (sourceIndex < 0)
            {
                return;
            }

            double sourceWidth = _detailsHeaderDragSource.ActualWidth;
            double mouseX = mousePositionInGrid.X;

            // Switch the preview as soon as the pointer enters another column.
            Border? targetCell = null;
            bool insertAfter = false;

            double runningX = 0;
            for (int i = 0; i < reorderableCells.Count; i++)
            {
                double cellWidth = reorderableCells[i].ActualWidth;
                double cellEnd = runningX + cellWidth;

                if (mouseX < cellEnd || i == reorderableCells.Count - 1)
                {
                    targetCell = reorderableCells[i];
                    if (i > sourceIndex)
                    {
                        insertAfter = true;
                    }
                    else if (i < sourceIndex)
                    {
                        insertAfter = false;
                    }
                    else
                    {
                        insertAfter = mouseX >= runningX + (cellWidth * 0.5);
                    }
                    break;
                }

                runningX = cellEnd;
            }

            if (targetCell != null)
            {
                ApplyDetailsHeaderDropPreview(targetCell, insertAfter);
            }
        }

        private void DetailsHeaderCell_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_detailsHeaderResizing)
            {
                e.Handled = true;
                return;
            }

            if (_detailsHeaderDragging)
            {
                FinalizeDetailsHeaderDrag();
                e.Handled = true;
                return;
            }

            if (sender is not Border cell || cell.Tag is not string metadataKey)
            {
                return;
            }

            _detailsHeaderDragSource = null;
            _detailsHeaderDragKey = null;

            if (ShouldSuppressDetailsHeaderPrimaryAction(metadataKey))
            {
                e.Handled = true;
                return;
            }

            ApplyDetailsHeaderSort(metadataKey);
            e.Handled = true;
        }

        private void FinalizeDetailsHeaderDrag()
        {
            if (DetailsHeaderGrid != null)
            {
                Mouse.Capture(null);
            }

            if (_detailsHeaderDropTarget != null &&
                _detailsHeaderDragSource != null &&
                _detailsHeaderDragKey != null &&
                _detailsHeaderDropTarget.Tag is string targetKey &&
                !string.Equals(targetKey, _detailsHeaderDragKey, StringComparison.OrdinalIgnoreCase))
            {
                string sourceKey = _detailsHeaderDragKey;
                bool insertAfter = _detailsHeaderDropInsertAfter;

                var reordered = NormalizeMetadataOrder(metadataOrder).ToList();
                int sourceIdx = reordered.FindIndex(key => string.Equals(key, sourceKey, StringComparison.OrdinalIgnoreCase));
                int targetIdx = reordered.FindIndex(key => string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase));
                if (sourceIdx >= 0 && targetIdx >= 0 && sourceIdx != targetIdx)
                {
                    reordered.RemoveAt(sourceIdx);
                    if (sourceIdx < targetIdx)
                    {
                        targetIdx--;
                    }

                    if (insertAfter)
                    {
                        targetIdx++;
                    }

                    targetIdx = Math.Max(0, Math.Min(targetIdx, reordered.Count));
                    reordered.Insert(targetIdx, sourceKey);
                    metadataOrder = NormalizeMetadataOrder(reordered);
                    MainWindow.SaveSettings();
                    MainWindow.NotifyPanelsChanged();
                }
            }

            ClearDetailsHeaderDropPreview();
            if (_detailsHeaderDragSource != null)
            {
                _detailsHeaderDragSource.Opacity = 1;
                System.Windows.Controls.Panel.SetZIndex(_detailsHeaderDragSource, 0);
            }

            _detailsHeaderDragging = false;
            _detailsHeaderDragSource = null;
            _detailsHeaderDragKey = null;

            RebuildListItemVisuals(sortItems: false);
        }

        private void DetailsHeaderCell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border cell || cell.Tag is not string metadataKey)
            {
                return;
            }

            _detailsHeaderContextColumnKey = metadataKey;
            ContextMenu menu = BuildDetailsHeaderContextMenu(metadataKey);
            cell.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private bool ShouldSuppressDetailsHeaderPrimaryAction(string metadataKey)
        {
            return string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase) &&
                TryGetEmbeddedParentNavigationPath(out _);
        }

        private bool TryBeginDetailsHeaderResize(Border cell, string metadataKey, MouseButtonEventArgs e)
        {
            if (!TryGetDetailsHeaderResizePair(
                    cell,
                    e.GetPosition(cell),
                    out string leftKey,
                    out string rightKey) ||
                DetailsHeaderGrid == null)
            {
                return false;
            }

            if (e.ClickCount >= 2)
            {
                AutoSizeDetailsDivider(leftKey, rightKey);
                return true;
            }
            var displayedWidths = GetDisplayedDetailsColumnWidths();
            if (!displayedWidths.TryGetValue(leftKey, out double leftWidth) ||
                !displayedWidths.TryGetValue(rightKey, out double rightWidth))
            {
                return false;
            }

            _detailsHeaderResizing = true;
            _detailsHeaderResizeLeftKey = leftKey;
            _detailsHeaderResizeRightKey = rightKey;
            _detailsHeaderResizeStartPoint = e.GetPosition(DetailsHeaderGrid);
            _detailsHeaderResizeStartLeftWidth = leftWidth;
            _detailsHeaderResizeStartRightWidth = rightWidth;
            Mouse.Capture(DetailsHeaderGrid, CaptureMode.SubTree);
            return true;
        }

        private bool CanResizeDetailsHeaderColumn(string metadataKey)
        {
            var visibleColumns = GetVisibleDetailsColumns();
            int index = visibleColumns.FindIndex(key => string.Equals(key, metadataKey, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index < visibleColumns.Count - 1;
        }

        private static bool IsDetailsHeaderResizeGripHit(Border cell, Point pointInCell)
        {
            return cell.ActualWidth > 0 &&
                pointInCell.X >= Math.Max(0, cell.ActualWidth - DetailsHeaderResizeHitWidth);
        }

        private bool TryGetDetailsHeaderResizePair(
            Border cell,
            Point pointInCell,
            out string leftKey,
            out string rightKey)
        {
            leftKey = string.Empty;
            rightKey = string.Empty;

            if (cell.Tag is not string metadataKey)
            {
                return false;
            }

            var visibleColumns = GetVisibleDetailsColumns();
            int currentIndex = visibleColumns.FindIndex(key =>
                string.Equals(key, metadataKey, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
            {
                return false;
            }

            bool canUseLeftDivider = currentIndex > 0 &&
                pointInCell.X <= DetailsHeaderResizeHitWidth;
            bool canUseRightDivider = currentIndex < visibleColumns.Count - 1 &&
                pointInCell.X >= Math.Max(0, cell.ActualWidth - DetailsHeaderResizeHitWidth);
            if (!canUseLeftDivider && !canUseRightDivider)
            {
                return false;
            }

            if (canUseLeftDivider && canUseRightDivider)
            {
                double distanceToLeft = pointInCell.X;
                double distanceToRight = Math.Max(0, cell.ActualWidth - pointInCell.X);
                canUseRightDivider = distanceToRight < distanceToLeft;
                canUseLeftDivider = !canUseRightDivider;
            }

            if (canUseLeftDivider)
            {
                leftKey = visibleColumns[currentIndex - 1];
                rightKey = visibleColumns[currentIndex];
                return true;
            }

            leftKey = visibleColumns[currentIndex];
            rightKey = visibleColumns[currentIndex + 1];
            return true;
        }

        private System.Windows.Input.Cursor GetDetailsHeaderDefaultCursor(string metadataKey)
        {
            return Cursors.Hand;
        }

        private void ApplyDetailsHeaderDropPreview(Border cell, bool insertAfter)
        {
            if (ReferenceEquals(_detailsHeaderDropTarget, cell) &&
                _detailsHeaderDropInsertAfter == insertAfter)
            {
                return;
            }

            ClearDetailsHeaderDropPreview();
            _detailsHeaderDropTarget = cell;
            _detailsHeaderDropInsertAfter = insertAfter;

            var reorderableCells = GetReorderableDetailsHeaderCells();
            if (_detailsHeaderDragSource != null)
            {
                System.Windows.Controls.Panel.SetZIndex(_detailsHeaderDragSource, 3);
            }

            int sourceIndex = _detailsHeaderDragSource == null
                ? -1
                : reorderableCells.IndexOf(_detailsHeaderDragSource);
            int targetIndex = reorderableCells.IndexOf(cell);
            if (sourceIndex >= 0 && targetIndex >= 0 && _detailsHeaderDragSource != null)
            {
                int previewInsertIndex = targetIndex + (insertAfter ? 1 : 0);
                double sourceWidth = _detailsHeaderDragSource.ActualWidth > 0
                    ? _detailsHeaderDragSource.ActualWidth
                    : cell.ActualWidth;

                if (previewInsertIndex > sourceIndex + 1)
                {
                    double sourceOffset = 0;
                    for (int i = sourceIndex + 1; i < previewInsertIndex && i < reorderableCells.Count; i++)
                    {
                        Border impactedCell = reorderableCells[i];
                        sourceOffset += impactedCell.ActualWidth;
                        ApplyDetailsHeaderCellOffset(impactedCell, -sourceWidth);
                    }

                    ApplyDetailsHeaderCellOffset(_detailsHeaderDragSource, sourceOffset);
                }
                else if (previewInsertIndex < sourceIndex)
                {
                    double sourceOffset = 0;
                    for (int i = previewInsertIndex; i < sourceIndex && i >= 0; i++)
                    {
                        Border impactedCell = reorderableCells[i];
                        sourceOffset += impactedCell.ActualWidth;
                        ApplyDetailsHeaderCellOffset(impactedCell, sourceWidth);
                    }

                    ApplyDetailsHeaderCellOffset(_detailsHeaderDragSource, -sourceOffset);
                }
                else
                {
                    ApplyDetailsHeaderCellOffset(_detailsHeaderDragSource, 0);
                }
            }

            Border? indicator = GetDetailsHeaderDropIndicator(cell);
            if (indicator != null)
            {
                indicator.HorizontalAlignment = insertAfter
                    ? System.Windows.HorizontalAlignment.Right
                    : System.Windows.HorizontalAlignment.Left;
                indicator.Margin = insertAfter
                    ? new Thickness(0, 5, -1, 5)
                    : new Thickness(-1, 5, 0, 5);
                indicator.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd
                    },
                    HandoffBehavior.SnapshotAndReplace);
            }
        }

        private void ClearDetailsHeaderDropPreview()
        {
            foreach (Border headerCell in GetAllDetailsHeaderCells())
            {
                Border? indicator = GetDetailsHeaderDropIndicator(headerCell);
                if (indicator != null)
                {
                    indicator.BeginAnimation(
                        UIElement.OpacityProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(90))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                            FillBehavior = FillBehavior.Stop
                        },
                        HandoffBehavior.SnapshotAndReplace);
                    indicator.Opacity = 0;
                }

                ApplyDetailsHeaderCellOffset(headerCell, 0);
                System.Windows.Controls.Panel.SetZIndex(headerCell, ReferenceEquals(headerCell, _detailsHeaderDragSource) ? 3 : 0);
            }

            if (_detailsHeaderDragSource != null && !_detailsHeaderDragging)
            {
                System.Windows.Controls.Panel.SetZIndex(_detailsHeaderDragSource, 0);
            }

            _detailsHeaderDropTarget = null;
            _detailsHeaderDropInsertAfter = false;
        }

        private List<Border> GetAllDetailsHeaderCells()
        {
            return DetailsHeaderGrid?.Children
                .OfType<Border>()
                .Where(cell => cell.Tag is string)
                .OrderBy(cell => Grid.GetColumn(cell))
                .ToList() ?? new List<Border>();
        }

        private List<Border> GetReorderableDetailsHeaderCells()
        {
            return GetAllDetailsHeaderCells()
                .Where(cell => cell.Tag is string)
                .ToList();
        }

        private static void ApplyDetailsHeaderCellOffset(Border cell, double targetOffset)
        {
            TranslateTransform? transform = GetDetailsHeaderCellTransform(cell);
            if (transform == null)
            {
                return;
            }

            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetOffset, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private Dictionary<string, double> GetDisplayedDetailsColumnWidths()
        {
            var visibleColumns = GetVisibleDetailsColumns();
            var displayedWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (DetailsHeaderGrid == null || DetailsHeaderGrid.ColumnDefinitions.Count != visibleColumns.Count)
            {
                return GetActualDetailsColumnWidths(GetDetailsItemWidth());
            }

            for (int i = 0; i < visibleColumns.Count; i++)
            {
                ColumnDefinition definition = DetailsHeaderGrid.ColumnDefinitions[i];
                double width = definition.ActualWidth > 0
                    ? definition.ActualWidth
                    : definition.Width.Value;
                displayedWidths[visibleColumns[i]] = Math.Max(GetMinimumDetailsColumnWidth(visibleColumns[i]), width);
            }

            return displayedWidths;
        }

        private void ApplyDetailsColumnWidthsToVisuals(Dictionary<string, double> widths, double totalWidth)
        {
            var visibleColumns = GetVisibleDetailsColumns();
            if (DetailsHeaderGrid != null && DetailsHeaderGrid.ColumnDefinitions.Count == visibleColumns.Count)
            {
                DetailsHeaderGrid.Width = totalWidth;
                for (int i = 0; i < visibleColumns.Count; i++)
                {
                    if (widths.TryGetValue(visibleColumns[i], out double width))
                    {
                        DetailsHeaderGrid.ColumnDefinitions[i].Width = new GridLength(width);
                    }
                }
            }

            foreach (ListBoxItem item in FileList?.Items.OfType<ListBoxItem>() ?? Enumerable.Empty<ListBoxItem>())
            {
                if (item.Content is not Grid row || row.ColumnDefinitions.Count != visibleColumns.Count)
                {
                    continue;
                }

                row.Width = totalWidth;
                for (int i = 0; i < visibleColumns.Count; i++)
                {
                    if (widths.TryGetValue(visibleColumns[i], out double width))
                    {
                        row.ColumnDefinitions[i].Width = new GridLength(width);
                    }
                }
            }
        }

        private void DetailsHeaderGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_detailsHeaderResizing ||
                DetailsHeaderGrid == null ||
                string.IsNullOrWhiteSpace(_detailsHeaderResizeLeftKey) ||
                string.IsNullOrWhiteSpace(_detailsHeaderResizeRightKey))
            {
                return;
            }

            Point current = e.GetPosition(DetailsHeaderGrid);
            double deltaX = current.X - _detailsHeaderResizeStartPoint.X;
            double pairWidth = _detailsHeaderResizeStartLeftWidth + _detailsHeaderResizeStartRightWidth;
            double minLeft = GetMinimumDetailsColumnWidth(_detailsHeaderResizeLeftKey);
            double minRight = GetMinimumDetailsColumnWidth(_detailsHeaderResizeRightKey);
            double targetLeft = Math.Max(minLeft, Math.Min(pairWidth - minRight, _detailsHeaderResizeStartLeftWidth + deltaX));
            double targetRight = Math.Max(minRight, pairWidth - targetLeft);

            var widths = GetDisplayedDetailsColumnWidths();
            widths[_detailsHeaderResizeLeftKey] = targetLeft;
            widths[_detailsHeaderResizeRightKey] = targetRight;
            ApplyDetailsColumnWidthsToVisuals(widths, GetDetailsItemWidth());
            e.Handled = true;
        }

        private void DetailsHeaderGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!FinalizeDetailsHeaderResize())
            {
                return;
            }

            e.Handled = true;
        }

        private void DetailsHeaderGrid_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FinalizeDetailsHeaderResize();
        }

        private bool FinalizeDetailsHeaderResize()
        {
            if (!_detailsHeaderResizing)
            {
                return false;
            }

            string? leftKey = _detailsHeaderResizeLeftKey;
            string? rightKey = _detailsHeaderResizeRightKey;
            _detailsHeaderResizing = false;
            _detailsHeaderResizeLeftKey = null;
            _detailsHeaderResizeRightKey = null;
            _detailsHeaderResizeStartLeftWidth = 0;
            _detailsHeaderResizeStartRightWidth = 0;

            if (Mouse.Captured == DetailsHeaderGrid)
            {
                Mouse.Capture(null);
            }

            if (!string.IsNullOrWhiteSpace(leftKey) &&
                !string.IsNullOrWhiteSpace(rightKey))
            {
                var widths = GetDisplayedDetailsColumnWidths();
                if (widths.TryGetValue(leftKey, out double leftWidth))
                {
                    SetStoredDetailsColumnWidth(leftKey, leftWidth);
                }

                if (widths.TryGetValue(rightKey, out double rightWidth))
                {
                    SetStoredDetailsColumnWidth(rightKey, rightWidth);
                }

                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
            return true;
        }

        private static Border? GetDetailsHeaderDropIndicator(Border cell)
        {
            if (cell.Child is not System.Windows.Controls.Panel panel)
            {
                return null;
            }

            return panel.Children
                .OfType<Border>()
                .FirstOrDefault(child => string.Equals(child.Tag as string, DetailsHeaderDropIndicatorTag, StringComparison.Ordinal));
        }

        private static TranslateTransform? GetDetailsHeaderCellTransform(Border cell)
        {
            return cell.RenderTransform as TranslateTransform;
        }

        private ContextMenu BuildDetailsHeaderContextMenu(string? clickedColumnKey)
        {
            var menu = new ContextMenu
            {
                Style = TryFindResource("PanelContextMenuStyle") as Style
            };

            var fitColumnItem = new MenuItem
            {
                Header = MainWindow.GetString("Loc.DetailColumnsAutoFitColumn"),
                IsEnabled = !string.IsNullOrWhiteSpace(clickedColumnKey),
                Style = TryFindResource("PanelContextMenuItemStyle") as Style
            };
            fitColumnItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(clickedColumnKey))
                {
                    AutoSizeDetailsColumn(clickedColumnKey);
                }
            };
            menu.Items.Add(fitColumnItem);

            var fitAllItem = new MenuItem
            {
                Header = MainWindow.GetString("Loc.DetailColumnsAutoFitAll"),
                Style = TryFindResource("PanelContextMenuItemStyle") as Style
            };
            fitAllItem.Click += (_, _) => AutoSizeAllDetailsColumns();
            menu.Items.Add(fitAllItem);
            menu.Items.Add(new Separator
            {
                Style = TryFindResource("PanelContextMenuSeparatorStyle") as Style
            });

            foreach (string key in DetailsContextMenuColumnKeys)
            {
                bool isName = string.Equals(key, MetadataName, StringComparison.OrdinalIgnoreCase);
                bool isVisible = isName || IsDetailsMetadataColumnVisible(key);
                var toggleItem = new MenuItem
                {
                    Header = GetDetailsColumnLabelText(key),
                    IsCheckable = true,
                    IsChecked = isVisible,
                    IsEnabled = !isName,
                    Style = TryFindResource("PanelContextMenuItemStyle") as Style
                };
                toggleItem.Click += (_, _) => SetDetailsMetadataColumnVisible(key, toggleItem.IsChecked);
                menu.Items.Add(toggleItem);
            }

            menu.Items.Add(new Separator
            {
                Style = TryFindResource("PanelContextMenuSeparatorStyle") as Style
            });

            var moreItem = new MenuItem
            {
                Header = MainWindow.GetString("Loc.DetailColumnsMore"),
                Style = TryFindResource("PanelContextMenuItemStyle") as Style
            };
            moreItem.Click += (_, _) => OpenDetailsColumnPickerDialog();
            menu.Items.Add(moreItem);

            return menu;
        }

        private bool AutoSizeDetailsColumn(string metadataKey)
        {
            if (FileList == null ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            double targetWidth = ComputeAutoSizeDetailsColumnWidth(metadataKey);
            SetStoredDetailsColumnWidth(metadataKey, targetWidth);
            RebuildListItemVisuals(sortItems: false);
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
            return true;
        }

        private bool AutoSizeDetailsDivider(string leftKey, string rightKey)
        {
            if (FileList == null ||
                string.IsNullOrWhiteSpace(leftKey) ||
                string.IsNullOrWhiteSpace(rightKey) ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var widths = GetDisplayedDetailsColumnWidths();
            if (!widths.TryGetValue(leftKey, out double currentLeftWidth) ||
                !widths.TryGetValue(rightKey, out double currentRightWidth))
            {
                return false;
            }

            double pairWidth = currentLeftWidth + currentRightWidth;
            double minLeft = GetMinimumDetailsColumnWidth(leftKey);
            double minRight = GetMinimumDetailsColumnWidth(rightKey);
            double fittedLeft = ComputeAutoSizeDetailsColumnWidth(leftKey);
            double targetLeft = Math.Max(minLeft, Math.Min(pairWidth - minRight, fittedLeft));
            double targetRight = Math.Max(minRight, pairWidth - targetLeft);

            widths[leftKey] = targetLeft;
            widths[rightKey] = targetRight;
            ApplyDetailsColumnWidthsToVisuals(widths, GetDetailsItemWidth());
            SetStoredDetailsColumnWidth(leftKey, targetLeft);
            SetStoredDetailsColumnWidth(rightKey, targetRight);
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
            return true;
        }

        private void AutoSizeAllDetailsColumns()
        {
            if (FileList == null ||
                !string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (string key in GetVisibleDetailsColumns())
            {
                SetStoredDetailsColumnWidth(key, ComputeAutoSizeDetailsColumnWidth(key));
            }

            RebuildListItemVisuals(sortItems: false);
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        private double ComputeAutoSizeDetailsColumnWidth(string metadataKey)
        {
            double fontSize = Math.Max(8, (_currentAppearance ?? MainWindow.Appearance).ItemFontSize) * zoomFactor;
            double valueFontSize = string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase)
                ? fontSize
                : Math.Max(8, fontSize - 1);
            double maxWidth = MeasureDetailsTextWidth(GetDetailsColumnLabelText(metadataKey), 12, FontWeights.SemiBold) + 26;
            int sampledCount = 0;

            foreach (ListBoxItem item in FileList?.Items.OfType<ListBoxItem>() ?? Enumerable.Empty<ListBoxItem>())
            {
                if (item.Tag is not string path || string.IsNullOrWhiteSpace(path) || item.Visibility != Visibility.Visible)
                {
                    continue;
                }

                bool isBackButton = IsParentNavigationItem(item);
                bool isFolder = isBackButton || Directory.Exists(path);
                string displayName = isBackButton ? BuildParentNavigationDisplayName(path) : GetDisplayNameForPath(path);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = GetPathLeafName(path);
                }

                string value = GetDetailsColumnValueText(metadataKey, displayName, path, isFolder, isBackButton);
                maxWidth = Math.Max(maxWidth, MeasureDetailsTextWidth(value, valueFontSize, FontWeights.Normal));

                sampledCount++;
                if (sampledCount >= 240)
                {
                    break;
                }
            }

            double padding = string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase)
                ? Math.Max(48, 30 * zoomFactor) + 34
                : 26;
            double unclampedWidth = maxWidth + padding;
            double minWidth = GetMinimumDetailsColumnWidth(metadataKey);
            double maxAllowedWidth = string.Equals(metadataKey, MetadataName, StringComparison.OrdinalIgnoreCase)
                ? 620
                : 360;
            return Math.Max(minWidth, Math.Min(maxAllowedWidth, unclampedWidth));
        }

        private double MeasureDetailsTextWidth(string text, double fontSize, FontWeight fontWeight)
        {
            string value = string.IsNullOrWhiteSpace(text) ? "-" : text;
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                value,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
                fontSize,
                Brushes.White,
                pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private FrameworkElement CreateDetailsNameCell(
            string displayName,
            string path,
            bool isFolder,
            double iconSize,
            double textSize,
            Brush nameBrush)
        {
            var container = new Grid();
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(28, iconSize + 8)) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new System.Windows.Controls.Image
            {
                Source = LoadExplorerStyleIcon(path, isFolder, (int)Math.Max(48, iconSize * 2)),
                Width = iconSize,
                Height = iconSize,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            TextBlock nameText = CreateItemNameTextBlock(
                displayName,
                textSize,
                nameBrush,
                TextAlignment.Left,
                new Thickness(0, 0, 0, 0));
            nameText.VerticalAlignment = VerticalAlignment.Center;
            nameText.TextWrapping = TextWrapping.NoWrap;
            nameText.TextTrimming = TextTrimming.CharacterEllipsis;

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameText, 1);
            container.Children.Add(icon);
            container.Children.Add(nameText);
            return container;
        }
    }
}
