using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool TryUpdateMorphologyStepElement(
        ImageEditStep? step, StructuralElement? element, out string error) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }
        if (step is null || !AdvancedSteps.Contains(step)) {
            error = "Выбранный шаг больше не находится в очереди";
            return false;
        }
        if (step.Type is not (ImageOperationType.Erosion or ImageOperationType.Dilation or
                              ImageOperationType.Opening or ImageOperationType.Closing or
                              ImageOperationType.MorphologicalGradient or ImageOperationType.BlackHat)) {
            error = "Этот шаг не использует структурный элемент";
            return false;
        }
        if (element is null || step.ElementSnapshot is not StructuralElement previousSnapshot) {
            error = "Матрица шага или выбранной ячейки отсутствует";
            return false;
        }
        try {
            // слот остается прежним, шаг получает собственную копию формы и опоры
            StructuralElement nextSnapshot = element.CreateSnapshot();
            step.SetElementSnapshot(nextSnapshot);
            // пересчитываем очередь один раз, остальные шаги и общий выбор матрицы не меняем
            ApplyAdvancedQueue();
            return true;
        }
        catch (Exception exception) {
            // прежний снимок уже готов: возврат не требует еще одного копирования или пересчета
            // UpdateModifiedImage из G показывает результат только после подготовки Bitmap и гистограммы
            step.SetElementSnapshot(previousSnapshot);
            error = $"Не удалось изменить матрицу шага: {exception.Message}";
            return false;
        }
    }
}
