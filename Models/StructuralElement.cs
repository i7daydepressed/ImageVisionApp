using System;

namespace ImageVisionApp.Models;

// хранит форму соседства и точку, которую совмещаем с текущим пикселем
public sealed class StructuralElement {
    private readonly bool[,] cells;

    public int Rows => cells.GetLength(0);
    public int Columns => cells.GetLength(1);
    public int AnchorRow { get; }
    public int AnchorColumn { get; }
    public int ActiveCellCount { get; }

    public StructuralElement(bool[,] cells, int anchorRow, int anchorColumn) {
        ArgumentNullException.ThrowIfNull(cells);

        int rows = cells.GetLength(0);
        int columns = cells.GetLength(1);
        if (rows == 0 || columns == 0) {
            throw new ArgumentException("Матрица должна содержать строки и столбцы", nameof(cells));
        }
        if (anchorRow < 0 || anchorRow >= rows ||
            anchorColumn < 0 || anchorColumn >= columns) {
            throw new ArgumentException("Опорная точка должна находиться внутри матрицы");
        }

        int activeCellCount = 0;
        foreach (bool cell in cells) {
            if (cell) {
                activeCellCount++;
            }
        }
        if (activeCellCount == 0) {
            throw new ArgumentException("Включите хотя бы одну ячейку матрицы", nameof(cells));
        }

        // сохраняем свою копию: редактирование исходного массива не изменит этот элемент
        this.cells = (bool[,])cells.Clone();
        AnchorRow = anchorRow;
        AnchorColumn = anchorColumn;
        ActiveCellCount = activeCellCount;
    }

    public bool IsActive(int row, int column) {
        return cells[row, column];
    }

    // редактор получает черновик, а не доступ к сохраненной матрице
    public bool[,] CopyCells() {
        return (bool[,])cells.Clone();
    }

    public static StructuralElement CreateCross() {
        bool[,] cells = new bool[,] {
            { false, true, false },
            { true, true, true },
            { false, true, false }
        };
        return new StructuralElement(cells, 1, 1);
    }
}
