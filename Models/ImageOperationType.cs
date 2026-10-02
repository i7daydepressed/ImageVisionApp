namespace ImageVisionApp.Models;

// какие преобразования могут быть отдельными действиями в очереди
public enum ImageOperationType {
    Brightness,
    Saturation,
    Contrast,
    Grayscale,
    LinearGrayscaleCorrection,
    NonlinearGrayscaleCorrection,
    Rotation
}