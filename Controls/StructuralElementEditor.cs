using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed class StructuralElementEditor : UserControl {
    private const double CellSide = 36;
    private const double CellGap = 4;
    private const double RulerWidth = 44;
    private const double RulerHeight = 28;
    private static readonly IBrush RulerHoverBrush =
        new SolidColorBrush(Color.Parse("#FFC857"));
    private Point? hoverViewportPoint;
    private int? hoveredRow;
    private int? hoveredColumn;
    private int? inspectedRow;
    private int? inspectedColumn;
    private readonly List<Border> rowMarkers = new List<Border>();
    private readonly List<Border> columnMarkers = new List<Border>();
    private readonly Canvas columnRuler = new Canvas {
        Height = RulerHeight,
        Width = 0,
        ClipToBounds = true,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly Canvas rowRuler = new Canvas {
        Width = RulerWidth,
        Height = 0,
        ClipToBounds = true,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock cellInfoText = new TextBlock {
        Text = "Наведите указатель на клетку",
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 112
    };
    private bool[,] cells = new bool[1, 1];
    private int anchorRow;
    private int anchorColumn;
    private StructuralElement savedElement;
    private bool fitToViewport;
    private bool updatingZoom;
    private bool fitQueued;
    private double appliedZoomPercent = 100;
    private Vector? pendingScrollOffset;
    private int scrollOffsetRevision;

    private readonly TextBox rowsInput = new TextBox { Width = 110 };
    private readonly TextBox columnsInput = new TextBox { Width = 110 };
    private readonly TextBox anchorRowInput = new TextBox { Width = 110 };
    private readonly TextBox anchorColumnInput = new TextBox { Width = 110 };
    private readonly Grid matrixGrid = new Grid {
        RowSpacing = CellGap,
        ColumnSpacing = CellGap,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly ScrollViewer matrixScroll = new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsScrollChainingEnabled = false,
        Background = Brushes.Transparent
    };
    private readonly Slider zoomSlider = new Slider {
        Width = 160,
        Minimum = 1,
        Maximum = 300,
        Value = 100,
        TickFrequency = 10,
        IsSnapToTickEnabled = false
    };
    private readonly TextBlock zoomText = new TextBlock {
        Text = "100%",
        Width = 64,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock zoomModeText = new TextBlock {
        Text = "Ручной",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock statusText = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.OrangeRed
    };

    public event Action<StructuralElement>? SelectionApplied;

    public StructuralElementEditor(
        StructuralElement initialElement,
        TextBlock description) {
        savedElement = initialElement;
        Content = CreateLayout(description);
        SetElement(initialElement);
    }

    public void SetElement(StructuralElement element) {
        savedElement = element;
        // меняем только черновик, поэтому сброс оставит прежний элемент
        cells = element.CopyCells();
        anchorRow = element.AnchorRow;
        anchorColumn = element.AnchorColumn;
        rowsInput.Text = element.Rows.ToString();
        columnsInput.Text = element.Columns.ToString();
        // в интерфейсе координаты начинаются с 1, в массиве с 0
        anchorRowInput.Text = (anchorRow + 1).ToString();
        anchorColumnInput.Text = (anchorColumn + 1).ToString();
        statusText.Text = "";
        hoverViewportPoint = null;
        hoveredRow = null;
        hoveredColumn = null;
        inspectedRow = null;
        inspectedColumn = null;
        CancelPendingScrollOffset();
        RenderCells();
        if (fitToViewport) {
            ScheduleFit();
        }
        else {
            QueueScrollOffset(new Vector(0, 0));
        }
    }

    private Grid CreateLayout(TextBlock description) {
        Border settingsCard = CreateSettingsCard(description);
        Border matrixCard = CreateMatrixCard();
        Grid layout = new Grid {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            ColumnSpacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        layout.Children.Add(settingsCard);
        Grid.SetColumn(matrixCard, 1);
        layout.Children.Add(matrixCard);
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
                new TextBlock {
                    Text = "0 — сосед исключен, 1 — включен.\nКрасная рамка — опорная точка.\nX — столбец, Y — строка; счет с 1.",
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

    private Border CreateMatrixCard() {
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
                UpdateCoordinateView();
                ScheduleFit();
            }
            else if (e.Property == ScrollViewer.OffsetProperty) {
                UpdateCoordinateView();
            }
        };
        matrixScroll.AttachedToVisualTree += (_, _) => {
            UpdateCoordinateView();
            ScheduleFit();
        };
        matrixScroll.DetachedFromVisualTree += (_, _) => {
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
        TrackMatrixViewportHeight(matrixFrame);

        Grid matrixArea = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 10
        };
        matrixArea.Children.Add(zoomControls);
        Grid.SetRow(matrixFrame, 1);
        matrixArea.Children.Add(matrixFrame);
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = matrixArea
        };
    }

    private void BuildRulers() {
        rowRuler.Children.Clear();
        columnRuler.Children.Clear();
        rowMarkers.Clear();
        columnMarkers.Clear();
        for (int row = 0; row < cells.GetLength(0); row++) {
            Border marker = CreateRulerMarker(row + 1);
            rowMarkers.Add(marker);
            rowRuler.Children.Add(marker);
        }
        for (int column = 0; column < cells.GetLength(1); column++) {
            Border marker = CreateRulerMarker(column + 1);
            columnMarkers.Add(marker);
            columnRuler.Children.Add(marker);
        }
    }

    private static Border CreateRulerMarker(int coordinate) {
        return new Border {
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock {
                Text = coordinate.ToString(),
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void UpdateRulers() {
        columnRuler.Width = Math.Max(0, matrixScroll.Viewport.Width);
        rowRuler.Height = Math.Max(0, matrixScroll.Viewport.Height);
        PositionRulerMarkers(columnMarkers, true, hoveredColumn);
        PositionRulerMarkers(rowMarkers, false, hoveredRow);
    }

    private void PositionRulerMarkers(
        List<Border> markers,
        bool horizontal,
        int? hoveredIndex) {
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        double stride = (CellSide + CellGap) * scale;
        double offset = horizontal ? matrixScroll.Offset.X : matrixScroll.Offset.Y;
        double viewport = horizontal ? matrixScroll.Viewport.Width : matrixScroll.Viewport.Height;
        double minimumSpacing = horizontal ? 32 : 22;
        double markerSpan = Math.Max(cellSize, minimumSpacing);
        int labelStep = Math.Max(1, (int)Math.Ceiling(minimumSpacing / stride));
        double? hoveredPosition = hoveredIndex.HasValue
            ? Math.Clamp(hoveredIndex.Value * stride - offset + (cellSize - markerSpan) / 2,
                0, Math.Max(0, viewport - markerSpan))
            : null;
        double lastPosition = double.NegativeInfinity;
        for (int index = 0; index < markers.Count; index++) {
            Border marker = markers[index];
            bool isHovered = index == hoveredIndex;
            double cellStart = index * stride - offset;
            double centeredPosition = cellStart + (cellSize - markerSpan) / 2;
            double position = Math.Clamp(centeredPosition, 0, Math.Max(0, viewport - markerSpan));
            bool intersectsViewport = viewport > 0 && cellStart < viewport && cellStart + cellSize > 0;
            bool nearHovered = hoveredPosition.HasValue && !isHovered &&
                Math.Abs(position - hoveredPosition.Value) < markerSpan;
            bool visible = intersectsViewport && (isHovered || index % labelStep == 0) && !nearHovered;
            // на краях подписи сдвигаем внутрь полосы, соседние обычные номера не накладываем
            if (visible && !isHovered && position - lastPosition < markerSpan) {
                visible = false;
            }
            marker.IsVisible = visible;
            if (visible) {
                lastPosition = position;
            }
            marker.Width = horizontal ? markerSpan : RulerWidth;
            marker.Height = horizontal ? RulerHeight : markerSpan;
            Canvas.SetLeft(marker, horizontal ? position : 0);
            Canvas.SetTop(marker, horizontal ? 0 : position);
            marker.Background = isHovered
                ? RulerHoverBrush
                : Brushes.Transparent;
            if (marker.Child is TextBlock number) {
                number.Foreground = isHovered ? Brushes.Black : Brushes.White;
                number.FontWeight = isHovered ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }
    }

    private void MatrixScroll_OnPointerMoved(object? sender, PointerEventArgs e) {
        hoverViewportPoint = GetViewportPoint(e);
        UpdateCoordinateView();
    }

    private void UpdateCoordinateView() {
        RefreshHoveredCell();
        UpdateRulers();
        UpdateCellInfo();
    }

    private void RefreshHoveredCell() {
        hoveredRow = null;
        hoveredColumn = null;
        if (hoverViewportPoint is not Point viewportPoint ||
            viewportPoint.X < 0 || viewportPoint.Y < 0 ||
            viewportPoint.X >= matrixScroll.Viewport.Width ||
            viewportPoint.Y >= matrixScroll.Viewport.Height) {
            return;
        }
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        double stride = (CellSide + CellGap) * scale;
        double x = viewportPoint.X + matrixScroll.Offset.X;
        double y = viewportPoint.Y + matrixScroll.Offset.Y;
        Size matrixSize = GetMatrixSize(zoomSlider.Value);
        if (x < 0 || y < 0 || x >= matrixSize.Width || y >= matrixSize.Height) {
            return;
        }
        int column = (int)Math.Floor(x / stride);
        int row = (int)Math.Floor(y / stride);
        // хвост шага принадлежит промежутку между квадратами, а не соседней клетке
        if (x - column * stride >= cellSize || y - row * stride >= cellSize) {
            return;
        }
        hoveredRow = row;
        hoveredColumn = column;
    }

    private void UpdateCellInfo() {
        int? infoRow = hoveredRow ?? inspectedRow;
        int? infoColumn = hoveredColumn ?? inspectedColumn;
        if (!infoRow.HasValue || !infoColumn.HasValue ||
            infoRow.Value < 0 || infoRow.Value >= cells.GetLength(0) ||
            infoColumn.Value < 0 || infoColumn.Value >= cells.GetLength(1)) {
            cellInfoText.Text = "Наведите указатель на клетку\nЩелчок переключает 0/1 и запоминает ее координаты";
            return;
        }
        int row = infoRow.Value;
        int column = infoColumn.Value;
        bool isActive = cells[row, column];
        bool isAnchor = row == anchorRow && column == anchorColumn;
        string source = hoveredRow.HasValue ? "Под указателем" : "Последний щелчок";
        string meaning = isActive
            ? "1 — позиция входит в соседство"
            : "0 — позиция исключена из соседства";
        string anchor = isAnchor ? "да" : "нет";
        // смещение относительно опоры станет адресом соседнего пикселя при морфологии
        int offsetX = column - anchorColumn;
        int offsetY = row - anchorRow;
        cellInfoText.Text =
            $"{source}: X = {column + 1}, Y = {row + 1}\n" +
            $"{meaning}\n" +
            $"Опорная точка: {anchor}\n" +
            $"От опоры: ΔX = {offsetX:+0;-0;0}, ΔY = {offsetY:+0;-0;0}\n" +
            "X — столбец, вправо; Y — строка, вниз";
    }

    private void TrackMatrixViewportHeight(Border matrixFrame) {
        TopLevel? host = null;
        EventHandler<SizeChangedEventArgs> hostSizeChanged = (_, _) => {
            if (host is not null) {
                UpdateMatrixViewportHeight(matrixFrame, host);
            }
        };

        AttachedToVisualTree += (_, _) => {
            if (host is not null) {
                host.SizeChanged -= hostSizeChanged;
            }
            host = TopLevel.GetTopLevel(this);
            if (host is not null) {
                host.SizeChanged += hostSizeChanged;
                UpdateMatrixViewportHeight(matrixFrame, host);
            }
        };
        DetachedFromVisualTree += (_, _) => {
            // при уходе со страницы окно больше не должно удерживать редактор своим событием
            if (host is not null) {
                host.SizeChanged -= hostSizeChanged;
            }
            host = null;
        };
    }

    private static void UpdateMatrixViewportHeight(Border matrixFrame, TopLevel host) {
        double windowHeight = host.ClientSize.Height;
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) {
            return;
        }
        // меняем размер области просмотра, а не масштаб или содержимое матрицы
        double frameHeight = Math.Clamp(windowHeight * 0.55, 320, 640);
        if (Math.Abs(matrixFrame.Height - frameHeight) > 0.5) {
            matrixFrame.Height = frameHeight;
        }
    }

    private void SetZoom(double percent, Point? viewportPoint = null) {
        if (!double.IsFinite(percent)) {
            return;
        }
        fitToViewport = false;
        zoomModeText.Text = "Ручной";

        double nextPercent = Math.Clamp(percent, zoomSlider.Minimum, zoomSlider.Maximum);
        double previousPercent = appliedZoomPercent;
        Size viewport = matrixScroll.Viewport;
        Vector oldOffset = pendingScrollOffset ?? matrixScroll.Offset;
        Size oldMatrixSize = GetMatrixSize(previousPercent);
        oldOffset = new Vector(
            Math.Clamp(oldOffset.X, 0, Math.Max(0, oldMatrixSize.Width - viewport.Width)),
            Math.Clamp(oldOffset.Y, 0, Math.Max(0, oldMatrixSize.Height - viewport.Height)));
        Point requestedPoint = viewportPoint ??
            new Point(viewport.Width / 2, viewport.Height / 2);
        Point focalPoint = new Point(
            Math.Clamp(requestedPoint.X, 0, Math.Max(0, viewport.Width)),
            Math.Clamp(requestedPoint.Y, 0, Math.Max(0, viewport.Height)));
        double contentX = Math.Clamp(oldOffset.X + focalPoint.X, 0, oldMatrixSize.Width);
        double contentY = Math.Clamp(oldOffset.Y + focalPoint.Y, 0, oldMatrixSize.Height);
        double ratio = nextPercent / previousPercent;
        Vector nextOffset = new Vector(
            contentX * ratio - focalPoint.X,
            contentY * ratio - focalPoint.Y);

        UpdateZoomValue(nextPercent);
        QueueScrollOffset(nextOffset);
    }

    private Point GetViewportPoint(PointerEventArgs e) {
        Point matrixPoint = e.GetPosition(matrixGrid);
        // вычитаем фактическую прокрутку: получаем положение указателя внутри видимой области
        return new Point(
            matrixPoint.X - matrixScroll.Offset.X,
            matrixPoint.Y - matrixScroll.Offset.Y);
    }

    private void QueueScrollOffset(Vector requestedOffset) {
        pendingScrollOffset = requestedOffset;
        int revision = ++scrollOffsetRevision;
        // новое значение Extent появляется после layout, поэтому не зажимаем offset по старой сетке
        Dispatcher.UIThread.Post(() => {
            if (revision != scrollOffsetRevision) {
                return;
            }
            pendingScrollOffset = null;
            if (TopLevel.GetTopLevel(this) is null) {
                return;
            }
            double maximumX = Math.Max(0, matrixScroll.Extent.Width - matrixScroll.Viewport.Width);
            double maximumY = Math.Max(0, matrixScroll.Extent.Height - matrixScroll.Viewport.Height);
            matrixScroll.Offset = new Vector(
                Math.Clamp(requestedOffset.X, 0, maximumX),
                Math.Clamp(requestedOffset.Y, 0, maximumY));
        }, DispatcherPriority.Loaded);
    }

    private void CancelPendingScrollOffset() {
        scrollOffsetRevision++;
        pendingScrollOffset = null;
    }

    private Size GetMatrixSize(double percent) {
        double scale = percent / 100;
        int rows = cells.GetLength(0);
        int columns = cells.GetLength(1);
        return new Size(
            (columns * CellSide + (columns - 1) * CellGap) * scale,
            (rows * CellSide + (rows - 1) * CellGap) * scale);
    }

    private void UpdateZoomValue(double percent) {
        double nextPercent = Math.Clamp(percent, zoomSlider.Minimum, zoomSlider.Maximum);
        if (Math.Abs(nextPercent - appliedZoomPercent) < 0.000001) {
            return;
        }
        updatingZoom = true;
        try {
            zoomSlider.Value = nextPercent;
        }
        finally {
            updatingZoom = false;
        }
        ApplyZoom();
    }

    private void ApplyZoom() {
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        Size matrixSize = GetMatrixSize(zoomSlider.Value);
        zoomText.Text = $"{zoomSlider.Value:0.##}%";
        matrixGrid.RowSpacing = CellGap * scale;
        matrixGrid.ColumnSpacing = CellGap * scale;
        matrixGrid.Width = matrixSize.Width;
        matrixGrid.Height = matrixSize.Height;
        foreach (RowDefinition row in matrixGrid.RowDefinitions) {
            row.Height = new GridLength(cellSize);
        }
        foreach (ColumnDefinition column in matrixGrid.ColumnDefinitions) {
            column.Width = new GridLength(cellSize);
        }
        // обновляем размер уже созданных кнопок, состав матрицы и черновик не меняем
        foreach (Control control in matrixGrid.Children) {
            if (control is Button button) {
                button.Width = cellSize;
                button.Height = cellSize;
                UpdateCellButton(button, Grid.GetRow(button), Grid.GetColumn(button));
            }
        }
        appliedZoomPercent = zoomSlider.Value;
        UpdateCoordinateView();
    }

    private void FitMatrix() {
        fitToViewport = true;
        zoomModeText.Text = "Авто";
        CancelPendingScrollOffset();
        ScheduleFit();
    }

    private void ScheduleFit() {
        if (!fitToViewport || fitQueued) {
            return;
        }
        fitQueued = true;
        Dispatcher.UIThread.Post(() => {
            fitQueued = false;
            if (!fitToViewport || TopLevel.GetTopLevel(this) is null) {
                return;
            }
            Size viewport = matrixScroll.Viewport;
            if (viewport.Width <= 2 || viewport.Height <= 2) {
                return;
            }
            // считаем габариты при 100%, а не берем размеры уже увеличенной матрицы
            Size originalSize = GetMatrixSize(100);
            double scale = Math.Min(
                (viewport.Width - 2) / originalSize.Width,
                (viewport.Height - 2) / originalSize.Height);
            UpdateZoomValue(scale * 100);
            QueueScrollOffset(new Vector(0, 0));
        }, DispatcherPriority.Loaded);
    }

    private void MatrixScroll_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0) {
            return;
        }
        SetZoom(
            e.Delta.Y > 0 ? zoomSlider.Value * 1.25 : zoomSlider.Value / 1.25,
            GetViewportPoint(e));
        e.Handled = true;
    }

    private void MatrixScroll_OnTouchPadMagnify(object? sender, PointerDeltaEventArgs e) {
        double delta = e.Delta.Y;
        double factor = 1 + delta;
        if (!double.IsFinite(delta) || delta == 0 || factor <= 0) {
            return;
        }
        // macOS передает приращение magnification, например 0.1 означает увеличение на 10%
        // https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/native/Avalonia.Native/src/OSX/AvnView.mm#L271-L274
        SetZoom(zoomSlider.Value * factor, GetViewportPoint(e));
        e.Handled = true;
    }
    private static StackPanel CreateLabeledInput(string label, TextBox input) {
        return new StackPanel {
            Width = 110,
            Spacing = 4,
            Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input }
        };
    }

    private bool TryApplyDimensions() {
        if (!int.TryParse(rowsInput.Text, out int rows) || rows <= 0 ||
            !int.TryParse(columnsInput.Text, out int columns) || columns <= 0) {
            statusText.Text = "Число строк и столбцов должно быть целым и больше 0";
            return false;
        }
        if (!int.TryParse(anchorRowInput.Text, out int selectedRow) ||
            !int.TryParse(anchorColumnInput.Text, out int selectedColumn) ||
            selectedRow < 1 || selectedRow > rows ||
            selectedColumn < 1 || selectedColumn > columns) {
            statusText.Text = "Укажите опорную точку внутри выбранного размера матрицы";
            return false;
        }

        bool dimensionsChanged = rows != cells.GetLength(0) || columns != cells.GetLength(1);
        if (dimensionsChanged) {
            bool[,] resizedCells = new bool[rows, columns];
            int preservedRows = Math.Min(rows, cells.GetLength(0));
            int preservedColumns = Math.Min(columns, cells.GetLength(1));
            // сохраняем пересечение старой и новой матриц, новые ячейки остаются выключенными
            for (int row = 0; row < preservedRows; row++) {
                for (int column = 0; column < preservedColumns; column++) {
                    resizedCells[row, column] = cells[row, column];
                }
            }
            cells = resizedCells;
        }

        anchorRow = selectedRow - 1;
        anchorColumn = selectedColumn - 1;
        RenderCells();
        if (dimensionsChanged) {
            FitMatrix();
        }
        statusText.Text = "";
        return true;
    }

    private void RenderCells() {
        matrixGrid.Children.Clear();
        matrixGrid.RowDefinitions.Clear();
        matrixGrid.ColumnDefinitions.Clear();
        double scale = zoomSlider.Value / 100;
        Size matrixSize = GetMatrixSize(zoomSlider.Value);
        matrixGrid.Width = matrixSize.Width;
        matrixGrid.Height = matrixSize.Height;
        appliedZoomPercent = zoomSlider.Value;
        double cellSize = CellSide * scale;
        matrixGrid.RowSpacing = CellGap * scale;
        matrixGrid.ColumnSpacing = CellGap * scale;
        for (int row = 0; row < cells.GetLength(0); row++) {
            matrixGrid.RowDefinitions.Add(new RowDefinition(new GridLength(cellSize)));
        }
        for (int column = 0; column < cells.GetLength(1); column++) {
            matrixGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cellSize)));
        }

        for (int row = 0; row < cells.GetLength(0); row++) {
            for (int column = 0; column < cells.GetLength(1); column++) {
                // каждая кнопка запоминает координаты своей ячейки, а не переменные цикла
                int cellRow = row;
                int cellColumn = column;
                Button cellButton = new Button {
                    Width = cellSize,
                    Height = cellSize,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch
                };
                UpdateCellButton(cellButton, cellRow, cellColumn);
                cellButton.Click += (_, _) => {
                    cells[cellRow, cellColumn] = !cells[cellRow, cellColumn];
                    inspectedRow = cellRow;
                    inspectedColumn = cellColumn;
                    UpdateCellButton(cellButton, cellRow, cellColumn);
                    UpdateCellInfo();
                    statusText.Text = "";
                };
                Grid.SetRow(cellButton, row);
                Grid.SetColumn(cellButton, column);
                matrixGrid.Children.Add(cellButton);
            }
        }
        if (inspectedRow.HasValue && inspectedColumn.HasValue &&
            (inspectedRow.Value >= cells.GetLength(0) || inspectedColumn.Value >= cells.GetLength(1))) {
            inspectedRow = null;
            inspectedColumn = null;
        }
        BuildRulers();
        UpdateCoordinateView();
    }

    private void UpdateCellButton(Button button, int row, int column) {
        bool isActive = cells[row, column];
        bool isAnchor = row == anchorRow && column == anchorColumn;
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        Border visual;
        TextBlock number;
        if (button.Content is Border existingVisual && existingVisual.Child is TextBlock existingNumber) {
            visual = existingVisual;
            number = existingNumber;
        }
        else {
            number = new TextBlock {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold
            };
            // собственная заливка внутри кнопки сохраняет цвет ячейки при наведении и нажатии
            visual = new Border { Child = number, CornerRadius = new CornerRadius(0) };
            button.Content = visual;
        }

        visual.Background = isActive ? Brushes.Black : Brushes.White;
        visual.BorderBrush = isAnchor ? Brushes.Red : Brushes.Gray;
        visual.BorderThickness = new Thickness(
            isAnchor ? Math.Min(2, cellSize / 4) : Math.Min(1, cellSize / 8));
        number.Text = isActive ? "1" : "0";
        number.Foreground = isActive ? Brushes.White : Brushes.Black;
        number.FontSize = Math.Min(40, 16 * scale);
        // маленькие ячейки показывают форму элемента, цифры возвращаются при приближении
        number.IsVisible = cellSize >= 12;
    }

    private void SaveSelection() {
        // учитываем числа в полях, даже если пользователь не нажал отдельную кнопку применения
        if (!TryApplyDimensions()) {
            return;
        }
        try {
            StructuralElement selectedElement =
                new StructuralElement(cells, anchorRow, anchorColumn);
            SelectionApplied?.Invoke(selectedElement);
            savedElement = selectedElement;
            statusText.Text = "";
        }
        catch (ArgumentException exception) {
            statusText.Text = exception.Message;
        }
    }
}
