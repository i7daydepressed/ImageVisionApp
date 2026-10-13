using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    // меняем параметр существующего объекта, его место и остальные шаги остаются прежними
    public bool TryUpdateAdvancedStepParameter(ImageEditStep? step, double value, out string error) {
        error = "";
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }
        if (step is null || !AdvancedSteps.Contains(step)) {
            error = "Выбранный шаг больше не находится в очереди";
            return false;
        }
        if (!ImageStepParameterRules.TryValidate(step.Type, value, out error)) {
            return false;
        }
        if (step.Parameter == value) {
            return true;
        }
        double previousValue = step.Parameter;
        try {
            // уведомление Parameter обновит название, обработку вызываем только после подтверждения
            step.Parameter = value;
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // подготовка изображения и гистограммы не меняет предпросмотр до успешного результата
            step.Parameter = previousValue;
            error = $"Не удалось изменить шаг: {exception.Message}";
            return false;
        }
    }
}
