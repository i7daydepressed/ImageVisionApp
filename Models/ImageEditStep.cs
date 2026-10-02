using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageVisionApp.Models;

public sealed partial class ImageEditStep : ObservableObject {
    public ImageOperationType Type { get; }

    // У каждого шага свое значение: например, две яркости могут быть +60 и +90
    [ObservableProperty]
    public partial double Parameter { get; set; }

    public string DisplayName => Type switch {
        ImageOperationType.Brightness =>
            $"Яркость {Parameter:+0.##;-0.##;0}",
        ImageOperationType.Saturation =>
            $"Насыщенность {Parameter:0.##}%",
        ImageOperationType.Contrast =>
            $"Контрастность {Parameter:+0.##;-0.##;0}",
        ImageOperationType.Grayscale =>
            "Градации серого",
        ImageOperationType.LinearGrayscaleCorrection =>
            "Линейная коррекция ЧБ",
        ImageOperationType.NonlinearGrayscaleCorrection =>
            "Нелинейная коррекция ЧБ",
        ImageOperationType.Rotation =>
            $"Поворот {Parameter:0}°",
        _ => Type.ToString()
    };

    public ImageEditStep(ImageOperationType type, double parameter = 0) {
        Type = type;
        Parameter = parameter;
    }
    partial void OnParameterChanged(double value) {
        OnPropertyChanged(nameof(DisplayName));
    }
}