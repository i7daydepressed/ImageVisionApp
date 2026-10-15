using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryUpdateCustomFilterKernel(
        ImageEditStep? step, FilterKernel? kernel, out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }
        if (step is null || !AdvancedSteps.Contains(step)) {
            error = "Выбранный шаг больше не находится в очереди";
            return false;
        }
        if (step.Type != ImageOperationType.CustomFilter) {
            error = "Этот шаг не использует пользовательское ядро";
            return false;
        }
        if (kernel is null || step.KernelSnapshot is not FilterKernel previousSnapshot) {
            error = "Ядро шага или редактора отсутствует";
            return false;
        }
        try {
            FilterKernel nextSnapshot = kernel.CreateSnapshot();
            step.SetKernelSnapshot(nextSnapshot);
            // меняем только выбранный шаг, очередь пересчитывается один раз
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // прежний результат остается на экране, возвращаем точный объект старого снимка
            step.SetKernelSnapshot(previousSnapshot);
            error = $"Не удалось изменить ядро шага: {exception.Message}";
            return false;
        }
    }
}
