using System;
using System.Runtime.InteropServices;
using System.Threading;
using ImageVisionApp.Models;
using OpenCvSharp;

namespace ImageVisionApp.Services;

public static class FastAnalysisService {
    // значение TYPE_9_16 в закрепленном binding из S0
    // https://github.com/shimat/opencvsharp/blob/4.13.0.20260627/src/OpenCvSharp/Modules/features2d/FastFeatureDetector.cs#L79
    private const int FastType9Of16 = 2;

    public static bool IsThresholdValid(int threshold) => threshold >= 1 && threshold <= 255;

    public static AnalysisKeypointResult Analyze(AnalysisInputSnapshot input, int threshold,
        bool nonmaxSuppression, CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(input);
        ValidateThreshold(threshold);
        (int width, int height, byte[] rgb) = AnalysisImageDecoder.Decode(input, cancellationToken);
        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, width, height, cancellationToken);
        AnalysisKeypoint[] points = CalculateFromGray(gray, width, height, threshold, nonmaxSuppression, cancellationToken);
        return new AnalysisKeypointResult(input, width, height, threshold, nonmaxSuppression, rgb, points);
    }

    public static AnalysisKeypoint[] CalculateFromGray(byte[] gray, int width, int height,
        int threshold, bool nonmaxSuppression, CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(gray);
        if (width <= 0 || height <= 0 || gray.Length != checked(width * height)) {
            throw new ArgumentException("Размер яркостного входа не совпадает с шириной и высотой");
        }
        ValidateThreshold(threshold);
        cancellationToken.ThrowIfCancellationRequested();
        if (width < 7 || height < 7) {
            return Array.Empty<AnalysisKeypoint>();
        }
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            throw new PlatformNotSupportedException("FAST этой версии рассчитан на macOS arm64");
        }

        using Mat source = new Mat(height, width, MatType.CV_8UC1);
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(gray, y * width, source.Ptr(y), width);
        }
        using FastFeatureDetector detector = FastFeatureDetector.Create(threshold, nonmaxSuppression);
        detector.Type = FastType9Of16;
        cancellationToken.ThrowIfCancellationRequested();
        // берем дугу из 9 соседей на круге из 16, NMS оставляет локальные максимумы отклика
        // REVIEW-SOURCE: OpenCV 4.13 :: FAST — проверка дуги 9/16 и NMS: <добавить при ревью>
        KeyPoint[] detected = detector.Detect(source);
        cancellationToken.ThrowIfCancellationRequested();

        // сохраняем координаты центров в пикселях входа отдельно от рисунка отметок
        AnalysisKeypoint[] points = new AnalysisKeypoint[detected.Length];
        for (int i = 0; i < detected.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            float x = detected[i].Pt.X;
            float y = detected[i].Pt.Y;
            if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || x >= width || y < 0 || y >= height ||
                x != MathF.Truncate(x) || y != MathF.Truncate(y)) {
                throw new InvalidOperationException("FAST вернул некорректную координату точки");
            }
            points[i] = new AnalysisKeypoint((int)x, (int)y);
        }
        return points;
    }

    private static void ValidateThreshold(int threshold) {
        if (!IsThresholdValid(threshold)) {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Порог FAST должен быть целым числом от 1 до 255");
        }
    }
}
