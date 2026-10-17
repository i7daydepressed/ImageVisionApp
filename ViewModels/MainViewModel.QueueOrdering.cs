using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    // меняет положение шага и заново применяет очередь в новом порядке
    public void MoveAdvancedStep(ImageEditStep? step, int offset) {
        if (step is null || (offset != -1 && offset != 1)) {
            return;
        }

        int oldIndex = AdvancedSteps.IndexOf(step);
        int newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= AdvancedSteps.Count) {
            return;
        }

        if (!TryMoveAdvancedStepToPosition(step, newIndex + 1, out string error)) {
            StatusMessage = error;
        }
    }

    // номер начинается с 1, коллекция хранит индексы с 0
    // Move переносит тот же объект вместе с параметром и снимком матрицы
    public bool TryMoveAdvancedStepToPosition(
        ImageEditStep? step, int position, out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }
        int oldIndex = step is null ? -1 : AdvancedSteps.IndexOf(step);
        if (oldIndex < 0) {
            error = "Выбранный шаг больше не находится в очереди";
            return false;
        }

        int newIndex = Math.Clamp(position, 1, AdvancedSteps.Count) - 1;
        if (newIndex == oldIndex) {
            return true;
        }

        try {
            AdvancedSteps.Move(oldIndex, newIndex);
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // неуспешный результат еще не показан, возвращаем порядок без второго пересчета
            AdvancedSteps.Move(newIndex, oldIndex);
            error = $"Не удалось переместить шаг: {exception.Message}";
            return false;
        }
    }
}
