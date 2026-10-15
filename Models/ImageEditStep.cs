using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageVisionApp.Models;

public sealed partial class ImageEditStep : ObservableObject {
    public ImageOperationType Type { get; }

    // матрица этого шага на момент его создания, отдельно от выбора для следующих действий
    // null означает, что матрица шагу не передавалась, прежние числовые действия работают как раньше
    // модель хранит только форму 0/1 и одну опору, состояние редактора сюда не переносим
    public StructuralElement? ElementSnapshot { get; private set; }

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
        ImageOperationType.Erosion => ElementSnapshot is StructuralElement element
            ? $"Эрозия {element.Rows}×{element.Columns} (Y×X), " +
              $"опора X={element.AnchorColumn + 1}, Y={element.AnchorRow + 1}"
            : "Эрозия: матрица отсутствует",
        ImageOperationType.Dilation => ElementSnapshot is StructuralElement dilationElement
            ? $"Дилатация {dilationElement.Rows}×{dilationElement.Columns} (Y×X), " +
              $"опора X={dilationElement.AnchorColumn + 1}, Y={dilationElement.AnchorRow + 1}"
            : "Дилатация: матрица отсутствует",
        ImageOperationType.Opening => ElementSnapshot is StructuralElement openingElement
            ? $"Открытие {openingElement.Rows}×{openingElement.Columns} (Y×X), " +
              $"опора X={openingElement.AnchorColumn + 1}, Y={openingElement.AnchorRow + 1}"
            : "Открытие: матрица отсутствует",
        ImageOperationType.Closing => ElementSnapshot is StructuralElement closingElement
            ? $"Закрытие {closingElement.Rows}×{closingElement.Columns} (Y×X), " +
              $"опора X={closingElement.AnchorColumn + 1}, Y={closingElement.AnchorRow + 1}"
            : "Закрытие: матрица отсутствует",
        ImageOperationType.MorphologicalGradient => ElementSnapshot is StructuralElement gradientElement
            ? $"Градиент {gradientElement.Rows}×{gradientElement.Columns} (Y×X), " +
              $"опора X={gradientElement.AnchorColumn + 1}, Y={gradientElement.AnchorRow + 1}"
            : "Градиент: матрица отсутствует",
        ImageOperationType.BlackHat => ElementSnapshot is StructuralElement blackHatElement
            ? $"Черная шляпа {blackHatElement.Rows}×{blackHatElement.Columns} (Y×X), " +
              $"опора X={blackHatElement.AnchorColumn + 1}, Y={blackHatElement.AnchorRow + 1}"
            : "Черная шляпа: матрица отсутствует",
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
    // команда передает отдельный готовый снимок, при откате возвращает прежний объект
    // StructuralElement неизменяемый, его закрытый массив не доступен вызывающему коду
    internal void SetElementSnapshot(StructuralElement snapshot) {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        if (Type is not (ImageOperationType.Erosion or ImageOperationType.Dilation or
                         ImageOperationType.Opening or ImageOperationType.Closing or
                         ImageOperationType.MorphologicalGradient or ImageOperationType.BlackHat)) {
            throw new System.InvalidOperationException("Этот шаг не использует структурный элемент");
        }
        if (System.Object.ReferenceEquals(ElementSnapshot, snapshot)) {
            return;
        }
        ElementSnapshot = snapshot;
        OnPropertyChanged(nameof(ElementSnapshot));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnParameterChanged(double value) {
        OnPropertyChanged(nameof(DisplayName));
    }
}