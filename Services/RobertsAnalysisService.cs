using System;
using System.Threading;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public static class RobertsAnalysisService {
    public static AnalysisEdgeResult Analyze(
        AnalysisInputSnapshot input, CancellationToken cancellationToken = default) {

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

        byte[] intensities = CalculateFromRgb(rgb, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisEdgeResult(input, width, height, rgb, intensities);
    }

    public static byte[] CalculateFromRgb(
        byte[] rgb, int width, int height, CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0 || rgb.Length != checked(width * height * 3)) {
            throw new ArgumentException("Размер RGB-входа не совпадает с шириной и высотой");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // сначала переводим весь RGB-вход в яркость Y, отклик считаем из неизменной карты
        // у квадрата 2x2 берем разности по диагоналям и их евклидову длину
        // справа и снизу повторяем ближайший пиксель, без порога и нормализации по максимуму
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

        byte[] result = new byte[gray.Length];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            int nextY = Math.Min(y + 1, height - 1);
            for (int x = 0; x < width; x++) {
                int nextX = Math.Min(x + 1, width - 1);
                int a = gray[y * width + x];
                int b = gray[y * width + nextX];
                int c = gray[nextY * width + x];
                int d = gray[nextY * width + nextX];
                int gx = a - d;
                int gy = b - c;
                // для a=0, b=0, c=30, d=40 получаем sqrt(1600+900)=50
                int magnitude = (int)Math.Floor(Math.Sqrt(gx * gx + gy * gy) + 0.5);
                result[y * width + x] = (byte)Math.Min(255, magnitude);
            }
        }
        return result;
    }
}
