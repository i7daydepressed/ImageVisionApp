using System;
using Avalonia;
using Avalonia.Input;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    private void MatrixScroll_OnBrushPressed(object? sender, PointerPressedEventArgs e) { // начинаем проход только от ПКМ на видимой клетке и захватываем указатель
        if (e.GetCurrentPoint(matrixScroll).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed) {
            return;
        }
        Point viewportPoint = GetViewportPoint(e);
        Point matrixPoint = e.GetPosition(matrixGrid);
        if (viewportPoint.X < 0 || viewportPoint.Y < 0 ||
            viewportPoint.X >= matrixScroll.Viewport.Width ||
            viewportPoint.Y >= matrixScroll.Viewport.Height ||
            !TryGetCellAt(matrixPoint, out _, out _)) {
            return;
        }
        EndBrushStroke();
        brushPointer = e.Pointer;
        brushPointer.Capture(matrixScroll);
        previousBrushPoint = matrixPoint;
        hoverViewportPoint = viewportPoint;
        PaintBrushSegment(matrixPoint, matrixPoint);
        UpdateCoordinateView();
        e.Handled = true;
    }

    private void MatrixScroll_OnBrushReleased(object? sender, PointerReleasedEventArgs e) { // дорисовываем последний участок и завершаем проход при отпускании ПКМ
        if (brushPointer != e.Pointer || e.GetCurrentPoint(matrixScroll).Properties.IsRightButtonPressed) {
            return;
        }
        Point matrixPoint = e.GetPosition(matrixGrid);
        PaintBrushSegment(previousBrushPoint ?? matrixPoint, matrixPoint);
        EndBrushStroke();
        hoverViewportPoint = GetViewportPoint(e);
        UpdateCoordinateView();
        e.Handled = true;
    }

    private void EndBrushStroke() { // очищаем проход и освобождаем только собственный захват указателя
        IPointer? pointer = brushPointer;
        // сначала сбрасываем состояние, чтобы PointerCaptureLost не завершал проход повторно
        brushPointer = null;
        previousBrushPoint = null;
        invertedDuringStroke.Clear();
        if (pointer is not null && pointer.Captured == matrixScroll) {
            pointer.Capture(null);
        }
    }

    private void PaintBrushSegment(Point from, Point to) { // инвертируем пересеченные клетки видимой части пути без пропусков между событиями
        // вход — две точки в координатах матрицы, сначала обрезаем путь по видимой области
        // шаг содержит квадрат 36s и промежуток 4s, DDA переходит по границам шагов вдоль отрезка
        // каждый кандидат проверяем по квадрату, набор прохода защищает от повторной инверсии
        Size matrixSize = GetMatrixSize(zoomSlider.Value);
        Rect viewport = new Rect(matrixScroll.Offset.X, matrixScroll.Offset.Y,
            matrixScroll.Viewport.Width, matrixScroll.Viewport.Height);
        Rect visibleMatrix = viewport.Intersect(new Rect(0, 0, matrixSize.Width, matrixSize.Height));
        if (!TryClipBrushSegment(from, to, visibleMatrix, out Point start, out Point end)) {
            return;
        }
        double scale = zoomSlider.Value / 100;
        double stride = (CellSide + CellGap) * scale;
        int rows = cells.GetLength(0);
        int columns = cells.GetLength(1);
        int column = Math.Clamp((int)Math.Floor(start.X / stride), 0, columns - 1);
        int row = Math.Clamp((int)Math.Floor(start.Y / stride), 0, rows - 1);
        int endColumn = Math.Clamp((int)Math.Floor(end.X / stride), 0, columns - 1);
        int endRow = Math.Clamp((int)Math.Floor(end.Y / stride), 0, rows - 1);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        int stepX = Math.Sign(dx);
        int stepY = Math.Sign(dy);
        double deltaX = stepX == 0 ? double.PositiveInfinity : stride / Math.Abs(dx);
        double deltaY = stepY == 0 ? double.PositiveInfinity : stride / Math.Abs(dy);
        double boundaryX = (column + (stepX > 0 ? 1 : 0)) * stride;
        double boundaryY = (row + (stepY > 0 ? 1 : 0)) * stride;
        double nextX = stepX == 0 ? double.PositiveInfinity : (boundaryX - start.X) / dx;
        double nextY = stepY == 0 ? double.PositiveInfinity : (boundaryY - start.Y) / dy;
        // идем по пересечениям отрезка с шагами сетки, а не только по точкам событий мыши
        while (row >= 0 && row < rows && column >= 0 && column < columns) {
            TryInvertStrokeCell(row, column, start, end, visibleMatrix, stride, CellSide * scale);
            if (row == endRow && column == endColumn) {
                break;
            }
            if (nextX < nextY) {
                column += stepX;
                nextX += deltaX;
            }
            else if (nextY < nextX) {
                row += stepY;
                nextY += deltaY;
            }
            else {
                column += stepX;
                row += stepY;
                nextX += deltaX;
                nextY += deltaY;
            }
        }
    }

    private void TryInvertStrokeCell(int row, int column, Point from, Point to, Rect visibleMatrix, double stride, double cellSize) { // проверяем пересечение квадрата и допускаем одну инверсию за проход
        Rect cellBounds = new Rect(column * stride, row * stride, cellSize, cellSize);
        if (!TryClipBrushSegment(from, to, cellBounds, out Point start, out Point end)) {
            return;
        }
        Point middle = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        // проверка середины пересечения исключает линии вдоль правой/нижней границы квадрата
        if (middle.X < visibleMatrix.X || middle.X >= visibleMatrix.Right ||
            middle.Y < visibleMatrix.Y || middle.Y >= visibleMatrix.Bottom ||
            !TryGetCellAt(middle, out int hitRow, out int hitColumn) ||
            hitRow != row || hitColumn != column || !invertedDuringStroke.Add((row, column))) {
            return;
        }
        cells[row, column] = !cells[row, column];
        inspectedRow = row;
        inspectedColumn = column;
        RefreshCellVisual(row, column);
        statusText.Text = "";
    }

    private static bool TryClipBrushSegment(Point from, Point to, Rect bounds, out Point start, out Point end) { // оставляем часть отрезка внутри прямоугольника для точного обхода клеток
        start = from;
        end = to;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            !double.IsFinite(from.X) || !double.IsFinite(from.Y) ||
            !double.IsFinite(to.X) || !double.IsFinite(to.Y)) {
            return false;
        }
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        // параметр t идет от 0 до 1, enter и leave ограничивают видимую часть from + t * (to - from)
        double enter = 0;
        double leave = 1;
        if (!ClipBrushEdge(-dx, from.X - bounds.X, ref enter, ref leave) ||
            !ClipBrushEdge(dx, bounds.Right - from.X, ref enter, ref leave) ||
            !ClipBrushEdge(-dy, from.Y - bounds.Y, ref enter, ref leave) ||
            !ClipBrushEdge(dy, bounds.Bottom - from.Y, ref enter, ref leave)) {
            return false;
        }
        start = new Point(from.X + enter * dx, from.Y + enter * dy);
        end = new Point(from.X + leave * dx, from.Y + leave * dy);
        return true;
    }

    private static bool ClipBrushEdge(double direction, double distance, ref double enter, ref double leave) { // сужаем интервал параметра отрезка по одной стороне прямоугольника
        // параллельный отрезок проходит проверку только с внутренней стороны границы
        if (direction == 0) {
            return distance >= 0;
        }
        double position = distance / direction;
        if (direction < 0) {
            if (position > leave) {
                return false;
            }
            enter = Math.Max(enter, position);
        }
        else {
            if (position < enter) {
                return false;
            }
            leave = Math.Min(leave, position);
        }
        return true;
    }
}
