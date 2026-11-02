using System;
using System.Threading;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public static class AnalysisCompositionService {
    public static byte[] CreatePng(
        AnalysisEdgeResult edges, bool useInputBackground, bool showEdges,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(edges);
        cancellationToken.ThrowIfCancellationRequested();
        // собираем PNG из готовой карты и RGB-подложки, детектор здесь заново не вызываем
        // отклик управляет белым слоем: на черном фоне получаем саму карту, без слоя остается подложка
        ReadOnlySpan<byte> source = edges.SourceRgb.Span;
        ReadOnlySpan<byte> intensities = edges.Intensities.Span;
        byte[] rgb = new byte[checked(edges.Width * edges.Height * 3)];
        for (int y = 0; y < edges.Height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < edges.Width; x++) {
                int pixel = y * edges.Width + x;
                int strength = showEdges ? intensities[pixel] : 0;
                for (int channel = 0; channel < 3; channel++) {
                    int index = pixel * 3 + channel;
                    int background = useInputBackground ? source[index] : 0;
                    // белый слой с alpha=strength/255, на черном фоне канал точно равен strength
                    rgb[index] = (byte)((background * (255 - strength) + 255 * strength + 127) / 255);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using MagickImage image = new MagickImage(MagickColors.Black, (uint)edges.Width, (uint)edges.Height);
        image.ColorSpace = ColorSpace.sRGB;
        image.ImportPixels(rgb,
            new PixelImportSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGB));
        image.Depth = 8;
        // новый результат непрозрачный, вход и сохранение редактора не меняем
        image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
        byte[] png = image.ToByteArray(MagickFormat.Png);
        cancellationToken.ThrowIfCancellationRequested();
        return png;
    }
}
