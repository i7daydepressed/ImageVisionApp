using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageVisionApp.Models;

public sealed partial class ImageEditStep : ObservableObject {
    public ImageOperationType Type { get; }

    // матрица этого шага на момент его создания, отдельно от выбора для следующих действий
    // null означает, что матрица шагу не передавалась, прежние числовые действия работают как раньше
    // модель хранит только форму 0/1 и одну опору, состояние редактора сюда не переносим
    public StructuralElement? ElementSnapshot { get; }

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

    public ImageEditStep(
        ImageOperationType type,
        double parameter = 0,
        StructuralElement? structuralElement = null) {
        Type = type;
        Parameter = parameter;
        // у каждого шага свой снимок, новый выбор или загрузка слота не заменяют его содержимое
        // например, шаг A получил крест 3x3, затем выбран квадрат 5x5 для шага B
        // шаг A продолжит хранить крест со своей опорой, B получит квадрат со своей опорой
        ElementSnapshot = structuralElement?.CreateSnapshot();
    }
    partial void OnParameterChanged(double value) {
        OnPropertyChanged(nameof(DisplayName));
    }
}