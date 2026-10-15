using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryAddSharpenStep(out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }

        // резкость имеет фиксированную матрицу, снимок элемента морфологии ей не нужен
        ImageEditStep step = new ImageEditStep(ImageOperationType.Sharpen);
        AdvancedSteps.Add(step);
        try {
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // неуспешный результат еще не опубликован, удаляем только новый объект
            AdvancedSteps.Remove(step);
            error = $"Не удалось добавить резкость: {exception.Message}";
            return false;
        }
    }
}
