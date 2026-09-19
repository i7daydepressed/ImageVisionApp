namespace ImageVisionApp.Models;

public sealed class ImageTransformationSettings {//джончек

    public bool IsGrayscaleEnabled { get; init; }

    // базовая яркость 0 , -100 100
    public double BrightnessAdjustment { get; init;}
}