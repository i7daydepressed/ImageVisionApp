using System;

namespace ImageVisionApp.Models;

// числовые веса линейного фильтра, опора, делитель и смещение хранятся одним снимком
public sealed class FilterKernel {
    private readonly double[,] weights;

    public int Rows => weights.GetLength(0);
    public int Columns => weights.GetLength(1);
    public int AnchorRow { get; }
    public int AnchorColumn { get; }
    public double Divisor { get; }
    public double Offset { get; }

    public FilterKernel(
        double[,] weights, int anchorRow, int anchorColumn,
        double divisor = 1, double offset = 0) {
        ArgumentNullException.ThrowIfNull(weights);
        int rows = weights.GetLength(0);
        int columns = weights.GetLength(1);
        if (rows == 0 || columns == 0) {
            throw new ArgumentException("Матрица должна содержать строки и столбцы", nameof(weights));
        }
        if (anchorRow < 0 || anchorRow >= rows ||
            anchorColumn < 0 || anchorColumn >= columns) {
            throw new ArgumentException("Опорная точка должна находиться внутри матрицы");
        }
        foreach (double weight in weights) {
            if (!double.IsFinite(weight)) {
                throw new ArgumentException("Коэффициенты должны быть конечными числами", nameof(weights));
            }
        }
        if (!double.IsFinite(divisor) || divisor == 0) {
            throw new ArgumentOutOfRangeException(nameof(divisor), "Делитель должен быть конечным числом, отличным от нуля");
        }
        if (!double.IsFinite(offset)) {
            throw new ArgumentOutOfRangeException(nameof(offset), "Смещение должно быть конечным числом");
        }

        // исходный массив и черновик редактора не получают доступ к сохраненным весам
        this.weights = (double[,])weights.Clone();
        AnchorRow = anchorRow;
        AnchorColumn = anchorColumn;
        Divisor = divisor;
        Offset = offset;
    }

    public double GetWeight(int row, int column) {
        return weights[row, column];
    }

    public double[,] CopyWeights() {
        return (double[,])weights.Clone();
    }

    public FilterKernel CreateSnapshot() {
        return new FilterKernel(weights, AnchorRow, AnchorColumn, Divisor, Offset);
    }

    public static FilterKernel CreateIdentity() {
        return new FilterKernel(new double[,] { { 1 } }, 0, 0);
    }
}
