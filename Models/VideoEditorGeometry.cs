using Avalonia;

namespace ImageVisionApp.Models;

internal readonly record struct VideoEditorGeometry(PixelSize InputSize, int ClockwiseQuarterTurns) {
    public PixelSize OutputSize => (ClockwiseQuarterTurns & 1) == 0
        ? InputSize
        : new PixelSize(InputSize.Height, InputSize.Width);

    // отсчитываем поворот к подложке от текущей системы координат слоя
    public VideoEditorGeometry RelativeTo(VideoEditorGeometry target) =>
        new(OutputSize, (target.ClockwiseQuarterTurns - ClockwiseQuarterTurns + 4) % 4);

    // переводим индекс пикселя предпросмотра, который уже ориентирован декодером
    // начало координат в левом верхнем углу, x растет вправо, y вниз
    public PixelPoint MapPixel(PixelPoint input) => ClockwiseQuarterTurns switch {
        1 => new PixelPoint(InputSize.Height - 1 - input.Y, input.X),
        2 => new PixelPoint(InputSize.Width - 1 - input.X, InputSize.Height - 1 - input.Y),
        3 => new PixelPoint(input.Y, InputSize.Width - 1 - input.X),
        _ => input
    };
}
