namespace ImageVisionApp.Models;

// какие преобразования могут быть отдельными действиями в очереди
public enum ImageOperationType {
    Brightness,
    Saturation,
    Contrast,
    Grayscale,
    LinearGrayscaleCorrection,
    NonlinearGrayscaleCorrection,
    Rotation,
    Erosion,
    Dilation,
    Opening,
    Closing,
    MorphologicalGradient,
    BlackHat,
    Sharpen,
    MotionBlur,
    Emboss,
    CustomFilter,
    Median,
    // новый тип дописываем в конец: TopHat получает 18, прежние номера не сдвигаются
    TopHat
}