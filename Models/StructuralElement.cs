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

    // превращаем единицы матрицы в смещения от опоры, строки соответствуют Y, столбцы X
    // для активной cells[row, column]: dx = column - AnchorColumn, dy = row - AnchorRow
    // при наложении элемента на P(x, y) эта позиция находится в (x + dx, y + dy)
    // это геометрия соседства, выбор минимума/максимума и обработка краев задаются операцией
    //
    // пример матрицы 2x3: { 0, 0, 1 }, { 1, 0, 0 }, опора (row=1, column=1)
    // единица (row=0, column=2) дает (+1, -1): для P(100, 50) получаем (101, 49)
    // единица (row=1, column=0) дает (-1, 0): для P(100, 50) получаем (99, 50)
    // нулевая клетка под опорой не входит в список, наличие опоры не делает ее единицей
    //
    // в смещениях остаются адреса соседства, яркость пикселей будет читать сервис операции
    // нули пропускаем, поэтому число смещений равно ActiveCellCount
    // возвращаем новый массив, изменение его записей не изменит форму или опору модели
    public (int X, int Y)[] GetActiveOffsets() {
        (int X, int Y)[] offsets = new (int X, int Y)[ActiveCellCount];
        int index = 0;
        for (int row = 0; row < Rows; row++) {
            for (int column = 0; column < Columns; column++) {
                if (!cells[row, column]) {
                    continue;
                }
                offsets[index] = (column - AnchorColumn, row - AnchorRow);
                index++;
            }
        }
        return offsets;
    }

    // редактор получает черновик, а не доступ к сохраненной матрице
    public bool[,] CopyCells() {
        return (bool[,])cells.Clone();
    }

    // создаем отдельный снимок формы и опоры, у него будет свой закрытый массив
    // в bool[,] false соответствует 0, true соответствует 1, отметки выделения и масштаб сюда не входят
    // конструктор копирует cells, поэтому снимок не делит массив с этим объектом
    // метод CopyCells дает изменяемый черновик, CreateSnapshot возвращает проверенную модель
    public StructuralElement CreateSnapshot() {
        return new StructuralElement(cells, AnchorRow, AnchorColumn);
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
