using System;
using Avalonia;

namespace ImageVisionApp.Services;

internal static class VideoPreviewDimensions {
    private const int LongSideLimit = 1280;

    // получаем одинаковый размер для первого, неподвижного и живого кадров
    public static PixelSize Fit(PixelSize input) {
        double scale = Math.Min(1, (double)LongSideLimit / Math.Max(input.Width, input.Height));
        return new PixelSize(ScaleSide(input.Width, scale), ScaleSide(input.Height, scale));
    }

    // четные стороны нужны для преобразования цветовых форматов 4:2:0 в VLC
    private static int ScaleSide(int length, double scale) =>
        Math.Max(1, (int)Math.Round(length * scale) & ~1);
}
