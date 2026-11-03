using System;
using System.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public static class RobertsAnalysisService {
    public static AnalysisEdgeResult Analyze(
        AnalysisInputSnapshot input, CancellationToken cancellationToken = default) {

        (int width, int height, byte[] rgb) = AnalysisImageDecoder.Decode(input, cancellationToken);
        byte[] intensities = CalculateFromRgb(rgb, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisEdgeResult(input, width, height, rgb, intensities);
    }

    public static byte[] CalculateFromRgb(
        byte[] rgb, int width, int height, CancellationToken cancellationToken = default) {

        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, width, height, cancellationToken);
        // у квадрата 2x2 берем разности по диагоналям и их евклидову длину
        // справа и снизу повторяем ближайший пиксель, без порога и нормализации по максимуму
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
