using System;
using System.Threading;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal static class AnalysisImageDecoder {
    internal static (int Width, int Height, byte[] Rgb) Decode(
        AnalysisInputSnapshot input, CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        using MagickImage image = new MagickImage(input.ImageData);
        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        int count = checked(width * height);
        // не вызываем AutoOrient, используем ту же геометрию, что у входа документа
        byte[] rgba = image.ToByteArray(MagickFormat.Rgba);
        if (rgba.Length != checked(count * 4)) {
            throw new InvalidOperationException("Не удалось получить полный RGBA-вход анализа");
        }

        byte[] rgb = new byte[checked(count * 3)];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int pixel = y * width + x;
                // alpha не участвует в яркости, RGB прозрачного пикселя не смешиваем с фоном
                rgb[pixel * 3] = rgba[pixel * 4];
                rgb[pixel * 3 + 1] = rgba[pixel * 4 + 1];
                rgb[pixel * 3 + 2] = rgba[pixel * 4 + 2];
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (width, height, rgb);
    }

    internal static byte[] ToLuminance(
        byte[] rgb, int width, int height, CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0 || rgb.Length != checked(width * height * 3)) {
            throw new ArgumentException("Размер RGB-входа не совпадает с шириной и высотой");
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] gray = new byte[checked(width * height)];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int pixel = y * width + x;
                int channel = pixel * 3;
                gray[pixel] = (byte)((299 * rgb[channel] + 587 * rgb[channel + 1]
                    + 114 * rgb[channel + 2] + 500) / 1000);
            }
        }
        return gray;
    }
}
