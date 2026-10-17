using System;
using System.Globalization;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

// разбирает текст черновика без доступа к очереди, изображению и контролам
public static class FilterKernelInput {
    public static bool TryReadPositiveInteger(string? text, out int value) {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    public static bool TryReadNumber(string? text, out double value) {
        string normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        // точка и запятая обозначают дробную часть, разделители тысяч не поддерживаются
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    public static bool TryCreate(
        string[,] weights, string? anchorRow, string? anchorColumn,
        string? divisorText, string? offsetText,
        out FilterKernel? kernel, out string error) {
        ArgumentNullException.ThrowIfNull(weights);
        kernel = null;
        error = string.Empty;
        int rows = weights.GetLength(0);
        int columns = weights.GetLength(1);
        if (rows == 0 || columns == 0) {
            error = "Матрица должна содержать строки и столбцы";
            return false;
        }
        if (!TryReadPositiveInteger(anchorRow, out int y) || y > rows ||
            !TryReadPositiveInteger(anchorColumn, out int x) || x > columns) {
            error = "Опора X/Y должна находиться внутри матрицы, координаты начинаются с 1";
            return false;
        }
        if (!TryReadNumber(divisorText, out double divisor) || divisor == 0) {
            error = "Делитель должен быть конечным числом, отличным от нуля";
            return false;
        }
        if (!TryReadNumber(offsetText, out double offset)) {
            error = "Смещение должно быть конечным числом";
            return false;
        }
        double[,] values = new double[rows, columns];
        for (int row = 0; row < rows; row++) {
            for (int column = 0; column < columns; column++) {
                if (!TryReadNumber(weights[row, column], out double value)) {
                    error = $"Коэффициент X={column + 1}, Y={row + 1} должен быть конечным числом";
                    return false;
                }
                values[row, column] = value;
            }
        }
        // интерфейс показывает опору от единицы, модель хранит индексы от нуля
        kernel = new FilterKernel(values, y - 1, x - 1, divisor, offset);
        return true;
    }
}
