using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryAddErosionStep(out string error) {
        return TryAddMorphologyStep(ImageOperationType.Erosion, out error);
    }

    public bool TryAddDilationStep(out string error) {
        return TryAddMorphologyStep(ImageOperationType.Dilation, out error);
    }

    private bool TryAddMorphologyStep(ImageOperationType type, out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }

        // конструктор копирует примененный элемент, черновик редактора сюда не попадает
        ImageEditStep step = new ImageEditStep(
            type,
            structuralElement: SelectedStructuralElement);
        AdvancedSteps.Add(step);
        try {
            // пересчитываем всю очередь от исходника, затем готовим изображение и гистограмму
            // существующий UpdateModifiedImage заменяет показанное состояние после подготовки
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // удаляем только добавленный объект, прежние шаги и показанный результат сохраняются
            // второй пересчет не нужен: неуспешный результат еще не был показан
            AdvancedSteps.Remove(step);
            string operationName = type == ImageOperationType.Erosion ? "эрозию" : "дилатацию";
            error = $"Не удалось добавить {operationName}: {exception.Message}";
            return false;
        }
    }
}
