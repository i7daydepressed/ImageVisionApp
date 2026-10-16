using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    // выбор для следующего шага, уже добавленные шаги хранят независимые снимки
    public FilterKernel SelectedFilterKernel { get; private set; } = FilterKernel.CreateIdentity();

    public string FilterKernelDescription =>
        $"строк (Y): {SelectedFilterKernel.Rows}, столбцов (X): {SelectedFilterKernel.Columns}, " +
        $"опора X={SelectedFilterKernel.AnchorColumn + 1}, Y={SelectedFilterKernel.AnchorRow + 1}, " +
        $"делитель {SelectedFilterKernel.Divisor:G}, смещение {SelectedFilterKernel.Offset:G}";

    public void SetFilterKernel(FilterKernel kernel) {
        ArgumentNullException.ThrowIfNull(kernel);
        SelectedFilterKernel = kernel.CreateSnapshot();
        OnPropertyChanged(nameof(SelectedFilterKernel));
        OnPropertyChanged(nameof(FilterKernelDescription));
    }

    public bool TryAddCustomFilterStep(out string error) {
        return TryAddFilterStep(ImageOperationType.CustomFilter, out error, SelectedFilterKernel);
    }

    public bool TryAddCustomFilterStep(FilterKernel kernel, out string error) {
        ArgumentNullException.ThrowIfNull(kernel);
        return TryAddFilterStep(ImageOperationType.CustomFilter, out error, kernel);
    }
    public bool TryAddSharpenStep(out string error) {
        return TryAddFilterStep(ImageOperationType.Sharpen, out error);
    }

    public bool TryAddMotionBlurStep(out string error) {
        return TryAddFilterStep(ImageOperationType.MotionBlur, out error);
    }

    public bool TryAddEmbossStep(out string error) {
        return TryAddFilterStep(ImageOperationType.Emboss, out error);
    }

    private bool TryAddFilterStep(
        ImageOperationType type, out string error, FilterKernel? filterKernel = null) {
        error = string.Empty;
        if (originalImageData is null) {
            error = "Сначала выберите изображение";
            return false;
        }

        // фиксированные фильтры передают null, пользовательский получает свой числовой снимок
        ImageEditStep step = new ImageEditStep(type, filterKernel: filterKernel);
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
                ImageOperationType.CustomFilter => "пользовательский фильтр",
                _ => "фильтр"
            };
            error = $"Не удалось добавить {operationName}: {exception.Message}";
            return false;
        }
    }
}
