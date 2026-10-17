using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    private Grid CreateLayout(TextBlock description) {
        Border settingsCard = CreateSettingsCard(description);
        Border matrixCard = CreateMatrixCard(settingsCard);
        Border savedElementsCard = CreateSavedElementsCard();
        Grid layout = new Grid {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 16,
            RowSpacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        // оба редактора создаются один раз, вкладки сохраняют их черновики и обработчики
        settingsTabs = CreateMatrixTabs(settingsCard, CreateFilterSettingsCard());
        matrixTabs = CreateMatrixTabs(matrixCard, CreateFilterMatrixCard());
        settingsTabs.SelectionChanged += (_, e) => {
            if (ReferenceEquals(e.Source, settingsTabs)) {
                SelectMatrixMode(settingsTabs.SelectedIndex);
            }
        };
        matrixTabs.SelectionChanged += (_, e) => {
            if (ReferenceEquals(e.Source, matrixTabs)) {
                SelectMatrixMode(matrixTabs.SelectedIndex);
            }
        };
        layout.Children.Add(settingsTabs);
        Grid.SetColumn(matrixTabs, 1);
        layout.Children.Add(matrixTabs);
        Grid.SetRow(savedElementsCard, 1);
        Grid.SetColumnSpan(savedElementsCard, 2);
        layout.Children.Add(savedElementsCard);
        return layout;
    }

    private Border CreateSettingsCard(TextBlock description) {
        WrapPanel inputs = new WrapPanel { ItemSpacing = 12, LineSpacing = 8 };
        inputs.Children.Add(CreateLabeledInput("Строки (Y)", rowsInput));
        inputs.Children.Add(CreateLabeledInput("Столбцы (X)", columnsInput));
        inputs.Children.Add(CreateLabeledInput("Опорная строка (Y)", anchorRowInput));
        inputs.Children.Add(CreateLabeledInput("Опорный столбец (X)", anchorColumnInput));

        Button applySizeButton = new Button {
            Content = "Изменить размер и опору",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        applySizeButton.Click += (_, _) => { TryApplyDimensions(); };

        Button resetButton = new Button {
            Content = "Сбросить черновик",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        resetButton.Click += (_, _) => SetElement(savedElement);
        Button saveButton = new Button {
            Content = "Применить матрицу",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        saveButton.Click += (_, _) => SaveSelection();
        StackPanel actions = new StackPanel {
            Spacing = 8,
            Children = { saveButton, resetButton }
        };

        StackPanel settings = new StackPanel {
            Spacing = 10,
            Children = {
                new TextBlock {
                    Text = "Структурный элемент",
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock {
                    Text = "Примененная матрица",
                    FontSize = 12
                },
                description,
                inputs,
                applySizeButton,
                CreateEditControls(),
                new TextBlock {
                    Text = "X — столбец вправо, Y — строка вниз; счет с 1.",
                    TextWrapping = TextWrapping.Wrap
                },
                CreateCellInfoPanel(),
                statusText,
                actions
            }
        };
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = settings
        };
    }

    private Border CreateEditControls() { // собираем выбор режима и группу для работы с черновиком из одной панели
        // карточку собираем один раз, поэтому подписки не добавляются при обновлении матрицы
        editModeInput.SelectionChanged += (_, _) => {
            // новый режим начнет следующий жест, текущий проход завершаем
            EndBrushStroke();
            editMode = editModeInput.SelectedIndex switch {
                1 => CellEditMode.SelectCells,
                2 => CellEditMode.SetAnchor,
                _ => CellEditMode.ToggleValue
            };
            // меняем только действие следующего щелчка, текущие значения и отметки не трогаем
            UpdateSelectionControls();
            UpdateCellInfo();
        };
        selectedZeroButton.Click += (_, _) => SetSelectedCells(false);
        selectedOneButton.Click += (_, _) => SetSelectedCells(true);
        invertSelectedButton.Click += (_, _) => InvertSelectedCells();
        clearSelectionButton.Click += (_, _) => ClearCellSelection();
        UpdateSelectionControls();
        return new Border {
            Padding = new Thickness(10),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel {
                Spacing = 8,
                Children = {
                    new TextBlock {
                        Text = "Действие по щелчку",
                        FontWeight = FontWeight.SemiBold
                    },
                    editModeInput,
                    editModeHint,
                    selectionCountText,
                    new WrapPanel {
                        ItemSpacing = 8,
                        LineSpacing = 6,
                        Children = { selectedZeroButton, selectedOneButton }
                    },
                    invertSelectedButton,
                    clearSelectionButton
                }
            }
        };
    }

    private Border CreateCellInfoPanel() {
        return new Border {
            Padding = new Thickness(10),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel {
                Spacing = 6,
                Children = {
                    new TextBlock {
                        Text = "Клетка",
                        FontWeight = FontWeight.SemiBold
                    },
                    cellInfoText
                }
            }
        };
    }

    private WrapPanel CreateZoomControls() {
        Button zoomOutButton = new Button { Content = "−", MinWidth = 32 };
        zoomOutButton.Click += (_, _) => SetZoom(zoomSlider.Value / 1.25);
        Button zoomInButton = new Button { Content = "+", MinWidth = 32 };
        zoomInButton.Click += (_, _) => SetZoom(zoomSlider.Value * 1.25);
        Button actualSizeButton = new Button { Content = "100%" };
        actualSizeButton.Click += (_, _) => SetZoom(100);
        Button fitButton = new Button { Content = "Вместить" };
        fitButton.Click += (_, _) => FitMatrix();
        zoomSlider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty && !updatingZoom) {
                SetZoom(zoomSlider.Value);
            }
        };

        return new WrapPanel {
            ItemSpacing = 8,
            LineSpacing = 6,
            Children = {
                new TextBlock {
                    Text = "Масштаб",
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                zoomOutButton,
                zoomSlider,
                zoomText,
                zoomInButton,
                actualSizeButton,
                fitButton,
                zoomModeText
            }
        };
    }

    private static StackPanel CreateMatrixLegend() { // ставим пояснения рядом с сеткой, чтобы различать опору, отметки и жест ПКМ
        return new StackPanel {
            Spacing = 4,
            Children = {
                CreateLegendRow(Brushes.DeepSkyBlue,
                    "Опора — единственная точка совмещения элемента с обрабатываемым пикселем"),
                CreateLegendRow(Brushes.Red,
                    "Выделение — клетки для групповых 0/1 и инверсии; оно не меняет опору"),
                new TextBlock {
                    Text = "1 включает позицию в соседство, 0 исключает. Желтые номера X/Y показывают наведение.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock {
                    Text = "ПКМ + ведение — инверсия 0/1; каждая клетка один раз за зажатие",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private static Grid CreateLegendRow(IBrush color, string text) { // связываем цвет рамки с пояснением, текст переносится в оставшейся ширине
        Grid row = new Grid {
            ColumnDefinitions = new ColumnDefinitions("18,*"),
            ColumnSpacing = 6
        };
        row.Children.Add(new Border {
            Width = 14,
            Height = 14,
            BorderBrush = color,
            BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center
        });
        TextBlock label = new TextBlock {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private Border CreateMatrixCard(Border settingsCard) {
        WrapPanel zoomControls = CreateZoomControls();
        matrixScroll.Content = matrixGrid;
        // перехватываем сочетание до обычной прокрутки, колесо без модификатора оставляем ScrollViewer
        matrixScroll.AddHandler(
            InputElement.PointerWheelChangedEvent,
            MatrixScroll_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        matrixScroll.AddHandler(
            InputElement.PointerTouchPadGestureMagnifyEvent,
            MatrixScroll_OnTouchPadMagnify,
            RoutingStrategies.Bubble);
        matrixScroll.AddHandler(
            InputElement.PointerPressedEvent,
            MatrixScroll_OnBrushPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        matrixScroll.AddHandler(
            InputElement.PointerReleasedEvent,
            MatrixScroll_OnBrushReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        matrixScroll.PointerCaptureLost += (_, e) => {
            if (brushPointer == e.Pointer) {
                EndBrushStroke();
            }
        };
        matrixScroll.AddHandler(
            InputElement.PointerMovedEvent,
            MatrixScroll_OnPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        matrixScroll.PointerExited += (_, _) => {
            hoverViewportPoint = null;
            UpdateCoordinateView();
        };
        matrixScroll.PropertyChanged += (_, e) => {
            if (e.Property == ScrollViewer.ViewportProperty) {
                previousBrushPoint = null;
                UpdateCoordinateView();
                ScheduleFit();
            }
            else if (e.Property == ScrollViewer.OffsetProperty) {
                previousBrushPoint = null;
                UpdateCoordinateView();
            }
        };
        matrixScroll.AttachedToVisualTree += (_, _) => {
            UpdateCoordinateView();
            ScheduleFit();
        };
        matrixScroll.DetachedFromVisualTree += (_, _) => {
            EndBrushStroke();
            CancelPendingScrollOffset();
            hoverViewportPoint = null;
            UpdateCoordinateView();
        };

        Grid coordinateArea = new Grid {
            ColumnDefinitions = new ColumnDefinitions {
                new ColumnDefinition(new GridLength(RulerWidth)),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star))
            },
            RowDefinitions = new RowDefinitions {
                new RowDefinition(new GridLength(RulerHeight)),
                new RowDefinition(new GridLength(1, GridUnitType.Star))
            }
        };
        Border corner = new Border {
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Child = new TextBlock {
                Text = "X →\nY ↓",
                FontSize = 10,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Border columnStrip = new Border {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Child = columnRuler
        };
        Border rowStrip = new Border {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Child = rowRuler
        };
        coordinateArea.Children.Add(corner);
        Grid.SetColumn(columnStrip, 1);
        coordinateArea.Children.Add(columnStrip);
        Grid.SetRow(rowStrip, 1);
        coordinateArea.Children.Add(rowStrip);
        Grid.SetRow(matrixScroll, 1);
        Grid.SetColumn(matrixScroll, 1);
        coordinateArea.Children.Add(matrixScroll);
        // конечная высота ограничивает поле матрицы даже внутри общего ScrollViewer
        Border matrixFrame = new Border {
            Height = 400,
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.Parse("#262626")),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = coordinateArea
        };
        StackPanel matrixHeader = new StackPanel {
            Spacing = 10,
            Children = { CreateMatrixLegend(), zoomControls }
        };

        Grid matrixArea = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 10
        };
        matrixArea.Children.Add(matrixHeader);
        Grid.SetRow(matrixFrame, 1);
        matrixArea.Children.Add(matrixFrame);
        Border matrixCard = new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = matrixArea
        };
        TrackMatrixViewportHeight(matrixFrame, settingsCard, matrixHeader, matrixCard);
        return matrixCard;
    }

    private static StackPanel CreateLabeledInput(string label, TextBox input) {
        return new StackPanel {
            Width = 110,
            Spacing = 4,
            Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input }
        };
    }
}
