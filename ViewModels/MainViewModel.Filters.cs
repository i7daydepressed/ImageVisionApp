using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryAddSharpenStep(out string error) {
        return TryAddFilterStep(ImageOperationType.Sharpen, out error);
    }

    public bool TryAddMotionBlurStep(out string error) {
        return TryAddFilterStep(ImageOperationType.MotionBlur, out error);
    }

    public bool TryAddEmbossStep(out string error) {
        return TryAddFilterStep(ImageOperationType.Emboss, out error);
    }

    private bool TryAddFilterStep(ImageOperationType type, out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }

        // фильтры имеют фиксированные матрицы, снимок элемента морфологии не нужен
        ImageEditStep step = new ImageEditStep(type);
        AdvancedSteps.Add(step);
        try {
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // неуспешный результат еще не опубликован, удаляем только новый объект
            AdvancedSteps.Remove(step);
            string operationName = type switch {
                ImageOperationType.Sharpen => "резкость",
                ImageOperationType.MotionBlur => "размытие в движении",
                ImageOperationType.Emboss => "тиснение",
                _ => "фильтр"
            };
            error = $"Не удалось добавить {operationName}: {exception.Message}";
            return false;
        }
    }
}
