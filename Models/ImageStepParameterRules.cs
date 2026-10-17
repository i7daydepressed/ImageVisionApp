using System;
using System.Globalization;

namespace ImageVisionApp.Models;

// общие ограничения параметров, без состояния очереди и элементов интерфейса
public static class ImageStepParameterRules {
    public static bool CanEdit(ImageOperationType type) {
        return type is ImageOperationType.Brightness or ImageOperationType.Saturation or
            ImageOperationType.Contrast or ImageOperationType.Rotation;
    }

    public static bool TryGetNumericRange(ImageOperationType type, out double minimum, out double? maximum) {
        minimum = 0;
        // null означает отсутствие верхней границы, ползунок ограничивается отдельно
        maximum = null;
        switch (type) {
            case ImageOperationType.Brightness:
            case ImageOperationType.Contrast:
                minimum = -100;
                maximum = 100;
                return true;
            case ImageOperationType.Saturation:
                return true;
            default:
                return false;
        }
    }

    public static bool TryReadValue(
        string? text, double minimum, double? maximum,
        out double value, out string error) {
        // точку и запятую принимаем как десятичный разделитель, разделители тысяч не используем
        string normalizedText = (text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(normalizedText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !double.IsFinite(value)) {
            error = "Введите конечное число, например 12,5 или 12.5";
            return false;
        }
        if (value < minimum || (maximum.HasValue && value > maximum.Value)) {
            error = maximum.HasValue
                ? $"Допустимое значение: от {minimum} до {maximum.Value}"
                : $"Допустимое значение: не меньше {minimum}";
            return false;
        }
        error = "";
        return true;
    }

    public static bool TryValidate(ImageOperationType type, double value, out string error) { // проверяем готовое число перед командой, даже если оно пришло не из поля
        if (!CanEdit(type)) {
            error = "У этого действия нет редактируемого числового параметра";
            return false;
        }
        if (!double.IsFinite(value)) {
            error = "Введите конечное число";
            return false;
        }
        // угол выбирается из трех вариантов, правила числовых коррекций к нему не относятся
        if (type == ImageOperationType.Rotation) {
            bool valid = value == 90 || value == 180 || value == 270;
            error = valid ? "" : "Выберите поворот 90°, 180° или 270°";
            return valid;
        }
        TryGetNumericRange(type, out double minimum, out double? maximum);
        if (value < minimum || (maximum.HasValue && value > maximum.Value)) {
            error = maximum.HasValue
                ? $"Допустимое значение: от {minimum} до {maximum.Value}"
                : $"Допустимое значение: не меньше {minimum}";
            return false;
        }
        error = "";
        return true;
    }
}
