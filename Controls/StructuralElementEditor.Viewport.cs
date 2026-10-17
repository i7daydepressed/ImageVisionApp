using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
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
        if (brushPointer == e.Pointer) {
            if (IsBrushButtonPressed(e)) {
                Point matrixPoint = e.GetPosition(matrixGrid);
                PaintBrushSegment(previousBrushPoint ?? matrixPoint, matrixPoint);
                previousBrushPoint = matrixPoint;
                e.Handled = true;
            }
            else {
                EndBrushStroke();
            }
        }
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
        Point matrixPoint = new Point(
            viewportPoint.X + matrixScroll.Offset.X,
            viewportPoint.Y + matrixScroll.Offset.Y);
        if (TryGetCellAt(matrixPoint, out int row, out int column)) {
            hoveredRow = row;
            hoveredColumn = column;
        }
    }

    private bool TryGetCellAt(Point matrixPoint, out int row, out int column) { // получаем индексы квадрата из координат матрицы, исключая промежутки
        row = -1;
        column = -1;
        Size size = GetMatrixSize(zoomSlider.Value);
        if (!double.IsFinite(matrixPoint.X) || !double.IsFinite(matrixPoint.Y) ||
            matrixPoint.X < 0 || matrixPoint.Y < 0 ||
            matrixPoint.X >= size.Width || matrixPoint.Y >= size.Height) {
            return false;
        }
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        double stride = (CellSide + CellGap) * scale;
        int candidateColumn = (int)Math.Floor(matrixPoint.X / stride);
        int candidateRow = (int)Math.Floor(matrixPoint.Y / stride);
        // хвост шага принадлежит промежутку между квадратами, а не соседней клетке
        if (matrixPoint.X - candidateColumn * stride >= cellSize ||
            matrixPoint.Y - candidateRow * stride >= cellSize) {
            return false;
        }
        row = candidateRow;
        column = candidateColumn;
        return true;
    }

    private void UpdateCellInfo() {
        int? infoRow = hoveredRow ?? inspectedRow;
        int? infoColumn = hoveredColumn ?? inspectedColumn;
        if (!infoRow.HasValue || !infoColumn.HasValue ||
            infoRow.Value < 0 || infoRow.Value >= cells.GetLength(0) ||
            infoColumn.Value < 0 || infoColumn.Value >= cells.GetLength(1)) {
            cellInfoText.Text = $"Наведите указатель на клетку\n{editModeHint.Text}";
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
        string selected = selectedCells.Contains((row, column)) ? "да" : "нет";
        // смещение относительно опоры станет адресом соседнего пикселя при морфологии
        int offsetX = column - anchorColumn;
        int offsetY = row - anchorRow;
        cellInfoText.Text =
            $"{source}: X = {column + 1}, Y = {row + 1}\n" +
            $"{meaning}\n" +
            $"Выделена: {selected}\n" +
            $"Опорная точка: {anchor}\n" +
            $"От опоры: ΔX = {offsetX:+0;-0;0}, ΔY = {offsetY:+0;-0;0}\n" +
            "X — столбец, вправо; Y — строка, вниз";
    }

    private void TrackMatrixViewportHeight(
        Border matrixFrame,
        Border settingsCard,
        Control matrixHeader,
        Border matrixCard) {
        TopLevel? host = null;
        bool heightQueued = false;
        void ScheduleHeight() { // объединяем изменения размеров и считаем высоту после layout
            if (heightQueued) {
                return;
            }
            heightQueued = true;
            Dispatcher.UIThread.Post(() => {
                heightQueued = false;
                if (host is not null) {
                    UpdateMatrixViewportHeight(matrixFrame, settingsCard, matrixHeader, matrixCard, host);
                }
            }, DispatcherPriority.Loaded);
        }
        EventHandler<SizeChangedEventArgs> hostSizeChanged = (_, _) => ScheduleHeight();
        settingsCard.SizeChanged += (_, _) => ScheduleHeight();
        matrixHeader.SizeChanged += (_, _) => ScheduleHeight();
        AttachedToVisualTree += (_, _) => {
            if (host is not null) {
                host.SizeChanged -= hostSizeChanged;
            }
            host = TopLevel.GetTopLevel(this);
            if (host is not null) {
                host.SizeChanged += hostSizeChanged;
                ScheduleHeight();
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

    private void UpdateMatrixViewportHeight(
        Border matrixFrame,
        Border settingsCard,
        Control matrixHeader,
        Border matrixCard,
        TopLevel host) {
        double windowHeight = host.ClientSize.Height;
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) {
            return;
        }
        double minimumHeight = Math.Clamp(windowHeight * 0.55, 320, 640);
        Thickness padding = matrixCard.Padding;
        Thickness border = matrixCard.BorderThickness;
        double spacing = matrixCard.Child is Grid area ? area.RowSpacing : 0;
        double outsideFrame = padding.Top + padding.Bottom + border.Top + border.Bottom +
            matrixHeader.Bounds.Height + spacing;
        // перенос сведений при наведении не должен менять viewport и снова запускать вписывание
        double informationGrowth = Math.Max(0, cellInfoText.Bounds.Height - cellInfoText.MinHeight);
        // меняем размер области просмотра, а не масштаб или содержимое матрицы
        // заполняем высоту настроек за вычетом легенды, масштаба, рамок и отступов
        double frameHeight = Math.Max(minimumHeight,
            settingsCard.Bounds.Height - informationGrowth - outsideFrame);
        if (double.IsFinite(frameHeight) && Math.Abs(matrixFrame.Height - frameHeight) > 0.5) {
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
        EndBrushStroke();
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
        // https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/native/Avalonia.Native/src/OSX/AvnView.mm#L288-L291
        SetZoom(zoomSlider.Value * factor, GetViewportPoint(e));
        e.Handled = true;
    }
}
