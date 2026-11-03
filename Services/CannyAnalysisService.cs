using System;
using System.Runtime.InteropServices;
using System.Threading;
using ImageVisionApp.Models;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

public static class CannyAnalysisService {
    // native Canny с L2 ограничивает пороги этим значением перед возведением в квадрат
    public const double MaximumThreshold = 32767;

    public static bool AreThresholdsValid(double lowerThreshold, double upperThreshold) =>
        double.IsFinite(lowerThreshold) && double.IsFinite(upperThreshold) &&
        lowerThreshold >= 0 && lowerThreshold < upperThreshold && upperThreshold <= MaximumThreshold;

    public static AnalysisEdgeResult Analyze(
        AnalysisInputSnapshot input, double lowerThreshold, double upperThreshold,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(input);
        ValidateThresholds(lowerThreshold, upperThreshold);
        cancellationToken.ThrowIfCancellationRequested();
        (int width, int height, byte[] rgb) = AnalysisImageDecoder.Decode(input, cancellationToken);
        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, width, height, cancellationToken);
        byte[] intensities = CalculateFromGray(gray, width, height, lowerThreshold, upperThreshold, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisEdgeResult(input, width, height, rgb, intensities,
            AnalysisEdgeMethod.Canny, lowerThreshold, upperThreshold);
    }

    public static byte[] CalculateFromGray(
        byte[] gray, int width, int height, double lowerThreshold, double upperThreshold,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(gray);
        if (width <= 0 || height <= 0 || gray.Length != checked(width * height)) {
            throw new ArgumentException("Размер яркостного входа не совпадает с шириной и высотой");
        }
        ValidateThresholds(lowerThreshold, upperThreshold);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            throw new PlatformNotSupportedException("Canny этой версии рассчитан на macOS arm64");
        }

        // матрица Mat владеет своей копией, управляемые массивы не закрепляются на время native-вызовов
        using Mat source = new Mat(height, width, MatType.CV_8UC1);
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(gray, y * width, source.Ptr(y), width);
        }
        using Mat blurred = new Mat();
        using Mat edges = new Mat();
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.GaussianBlur(source, blurred, new CvSize(5, 5), 1.4, 1.4, BorderTypes.Reflect101);
        cancellationToken.ThrowIfCancellationRequested();
        // источник Sobel и ограничения L2: https://github.com/opencv/opencv/blob/4.13.0/modules/imgproc/src/canny.cpp
        // оператор Sobel 3x3 внутри native Canny использует повторение ближайшего пикселя, L2 включена
        Cv2.Canny(blurred, edges, lowerThreshold, upperThreshold, 3, true);
        cancellationToken.ThrowIfCancellationRequested();

        // размеры читаются один раз, Rows и Cols обращаются к native-коду
        int rows = edges.Rows;
        int columns = edges.Cols;
        MatType type = edges.Type();
        if (rows != height || columns != width || type != MatType.CV_8UC1) {
            throw new InvalidOperationException("Canny вернул карту неожиданного размера или типа");
        }
        byte[] result = new byte[checked(width * height)];
        for (int y = 0; y < rows; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(edges.Ptr(y), result, y * width, width);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void ValidateThresholds(double lowerThreshold, double upperThreshold) {
        if (!AreThresholdsValid(lowerThreshold, upperThreshold)) {
            throw new ArgumentException("Пороги Canny: 0 ≤ нижний < верхний ≤ 32767, оба числа должны быть конечными");
        }
    }
}
