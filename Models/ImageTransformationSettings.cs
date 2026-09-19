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

    public void Reset() {
        IsGrayscaleEnabled = false;// хз по идее надо джсончике параметры сохранять
        BrightnessAdjustment = 0;
        SaturationPercentage = 100;
        ContrastAdjustment = 0;
    }
}
