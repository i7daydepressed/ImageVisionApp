using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryAddErosionStep(out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }

        // конструктор копирует примененный элемент, черновик редактора сюда не попадает
        ImageEditStep step = new ImageEditStep(
            ImageOperationType.Erosion,
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
            error = $"Не удалось добавить эрозию: {exception.Message}";
            return false;
        }
    }
}
