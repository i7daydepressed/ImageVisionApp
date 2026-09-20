using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageVisionApp.Models;

public sealed partial class ImageTransformationSettings
    : ObservableObject {//джончек

    // false — изображение цветное
    // true — применяются градации серого
    [ObservableProperty]
    public partial bool IsGrayscaleEnabled { get; set; } = false;

    // 0 — исходная яркость без изменений
    // авалония автоприсваивает сюда значение по изменению ползунка
    // базовая яркость 0 , -100 100
    [ObservableProperty]
    public partial double BrightnessAdjustment { get; set; } = 0;

    // 100 — исходная насыщенность
    [ObservableProperty]
    public partial double SaturationPercentage { get; set; } = 100;

    // 0 — исходная контрастность без изменений
    [ObservableProperty]
    public partial double ContrastAdjustment { get; set; } = 0;

    // 0 — изображение не повёрнуто
    // следующие значения: 90, 180, 270
    [ObservableProperty]
    public partial int RotationDegrees { get; set; } = 0;

    public void Reset() {
        IsGrayscaleEnabled = false;//еее
        BrightnessAdjustment = 0;
        SaturationPercentage = 100;
        ContrastAdjustment = 0;
        RotationDegrees = 0;
    }
}
