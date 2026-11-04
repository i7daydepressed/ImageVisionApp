using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ImageVisionApp.Models;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

public static class PlateAnalysisService {
    public const string AlgorithmId = "light-sobel-glyphs-v2";

    public static AnalysisPlateResult Analyze(AnalysisInputSnapshot input,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(input);
        (int width, int height, byte[] rgb) = AnalysisImageDecoder.Decode(input, cancellationToken);
        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, width, height, cancellationToken);
        AnalysisPlateRectangle? rectangle = CalculateFromGray(gray, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisPlateResult(input, width, height, rgb, rectangle);
    }

    // проверенный расчет A6.1b получает яркость и размер, результаты диагностики в приложение не переносятся
    // https://docs.opencv.org/4.13.0/d5/d0f/tutorial_py_gradients.html
    public static AnalysisPlateRectangle? CalculateFromGray(byte[] gray, int width, int height,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(gray);
        if (width <= 0 || height <= 0 || gray.Length != checked(width * height)) {
            throw new ArgumentException("Размер яркости не соответствует геометрии");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            throw new PlatformNotSupportedException("Поиск номера этой версии рассчитан на macOS arm64");
        }
        using Mat source = ByteMat(gray, width, height, cancellationToken);
        byte[] bright = Threshold(gray, Otsu(gray, cancellationToken), false, cancellationToken);
        using Mat brightInput = ByteMat(bright, width, height, cancellationToken);
        using Mat brightClosed = new Mat();
        using Mat brightClean = new Mat();
        using Mat lightKernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new CvSize(OddKernel(width, 0.008, 5, 31), OddKernel(height, 0.004, 3, 15)));
        using Mat smallKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new CvSize(3, 3));
        Cv2.MorphologyEx(brightInput, brightClosed, MorphTypes.Close, lightKernel,
            borderType: BorderTypes.Constant, borderValue: Scalar.All(0));
        Cv2.MorphologyEx(brightClosed, brightClean, MorphTypes.Open, smallKernel,
            borderType: BorderTypes.Constant, borderValue: Scalar.All(0));

        // float64 сохраняет знак и точность до отдельного округления сглаженной яркости
        using Mat source64 = new Mat();
        source.ConvertTo(source64, MatType.CV_64FC1);
        using Mat blurred64 = new Mat();
        Cv2.GaussianBlur(source64, blurred64, new CvSize(5, 5), 1.4, 1.4, BorderTypes.Reflect101);
        byte[] roundedBlur = new byte[gray.Length];
        double[] row = new double[width];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(blurred64.Ptr(y), row, 0, width);
            for (int x = 0; x < width; x++) {
                roundedBlur[y * width + x] = (byte)Math.Clamp((int)Math.Floor(row[x] + 0.5), 0, 255);
            }
        }
        using Mat blurred = ByteMat(roundedBlur, width, height, cancellationToken);
        using Mat signedGradient = new Mat();
        Cv2.Sobel(blurred, signedGradient, MatType.CV_16SC1, 1, 0, 3, 1, 0, BorderTypes.Reflect101);
        short[] signedRow = new short[width];
        byte[] gradient = new byte[gray.Length];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(signedGradient.Ptr(y), signedRow, 0, width);
            for (int x = 0; x < width; x++) {
                gradient[y * width + x] = (byte)Math.Min(255,
                    (int)Math.Floor(Math.Abs((int)signedRow[x]) / 4.0 + 0.5));
            }
        }
        int gradientThreshold = Otsu(gradient, cancellationToken);
        byte[] gradientBinary = Threshold(gradient, gradientThreshold, false, cancellationToken);
        using Mat gradientInput = ByteMat(gradientBinary, width, height, cancellationToken);
        using Mat gradientClosed = new Mat();
        using Mat gradientKernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new CvSize(OddKernel(width, 0.015, 5, 31), 3));
        Cv2.MorphologyEx(gradientInput, gradientClosed, MorphTypes.Close, gradientKernel,
            borderType: BorderTypes.Constant, borderValue: Scalar.All(0));

        List<Candidate> candidates = new List<Candidate>();
        InspectRegions(Components(brightClean, cancellationToken), "light", gray, width, height, candidates, cancellationToken);
        InspectRegions(Components(gradientClosed, cancellationToken), "sobel", gray, width, height, candidates, cancellationToken);
        Candidate[] ranked = candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Box.Y).ThenBy(candidate => candidate.Box.X)
            .ThenBy(candidate => candidate.Box.Width).ThenBy(candidate => candidate.Box.Height)
            .ThenBy(candidate => candidate.Mask, StringComparer.Ordinal).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return ranked.FirstOrDefault()?.Box;
    }

    private static void InspectRegions(Region[] regions, string mask, byte[] gray, int width, int height,
        List<Candidate> candidates, CancellationToken cancellationToken) {

        foreach (Region region in regions) {
            cancellationToken.ThrowIfCancellationRequested();
            AnalysisPlateRectangle box = region.Box;
            double aspect = (double)box.Width / box.Height;
            double relativeArea = (double)box.Area / ((long)width * height);
            double fill = (double)region.Pixels / box.Area;
            if (box.Width < 48 || box.Height < 12 || relativeArea < 0.0008 || relativeArea > 0.18) {
                continue;
            }
            if (aspect < 1.2 || aspect > 6.8 || fill < 0.20) {
                continue;
            }
            byte[] crop = new byte[checked(box.Width * box.Height)];
            for (int y = 0; y < box.Height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Copy(gray, (box.Y + y) * width + box.X, crop, y * box.Width, box.Width);
            }
            byte[] darkPixels = Threshold(crop, Otsu(crop, cancellationToken), true, cancellationToken);
            using Mat dark = ByteMat(darkPixels, box.Width, box.Height, cancellationToken);
            Region[] glyphs = Components(dark, cancellationToken).Where(component => IsGlyph(component, box.Width, box.Height)).ToArray();
            if (glyphs.Length < 4 || glyphs.Length > 12) {
                continue;
            }
            double[] heights = glyphs.Select(glyph => (double)glyph.Box.Height).OrderBy(value => value).ToArray();
            double medianHeight = heights.Length % 2 == 0
                ? (heights[heights.Length / 2 - 1] + heights[heights.Length / 2]) / 2
                : heights[heights.Length / 2];
            double[] centers = glyphs.Select(glyph => glyph.Box.Y + glyph.Box.Height / 2.0).ToArray();
            double average = centers.Average();
            double alignment = Math.Sqrt(centers.Select(value => (value - average) * (value - average)).Average()) / medianHeight;
            if (alignment > 0.30) {
                continue;
            }
            double score = glyphs.Length + fill - alignment;
            candidates.Add(new Candidate(box, score, mask));
        }
    }

    private static bool IsGlyph(Region component, int width, int height) {
        AnalysisPlateRectangle rectangle = component.Box;
        double relativeHeight = (double)rectangle.Height / height;
        double relativeWidth = (double)rectangle.Width / width;
        double aspect = (double)rectangle.Width / rectangle.Height;
        double fill = (double)component.Pixels / rectangle.Area;
        return rectangle.X > 0 && rectangle.Y > 0 && rectangle.Right < width && rectangle.Bottom < height &&
            relativeHeight >= 0.22 && relativeHeight <= 0.95 && relativeWidth >= 0.025 && relativeWidth <= 0.18 &&
            aspect >= 0.12 && aspect <= 1.1 && fill >= 0.12 && fill <= 0.85;
    }

    private static int OddKernel(int length, double ratio, int minimum, int maximum) =>
        Math.Clamp((int)Math.Round(length * ratio, MidpointRounding.ToEven), minimum, maximum) | 1;

    private static int Otsu(byte[] values, CancellationToken cancellationToken) {
        long[] histogram = new long[256];
        for (int i = 0; i < values.Length; i++) {
            if (i % 65536 == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            histogram[values[i]]++;
        }
        long totalMoment = 0;
        for (int i = 0; i < histogram.Length; i++) { totalMoment += histogram[i] * i; }
        long weight = 0;
        long moment = 0;
        double maximum = 0;
        int threshold = 0;
        for (int i = 0; i < histogram.Length; i++) {
            weight += histogram[i];
            moment += histogram[i] * i;
            double denominator = (double)weight * (values.Length - weight);
            if (denominator == 0) { continue; }
            double numerator = (double)totalMoment * weight - (double)values.Length * moment;
            double variance = numerator * numerator / denominator;
            if (variance > maximum) { maximum = variance; threshold = i; }
        }
        return threshold;
    }

    private static byte[] Threshold(byte[] values, int threshold, bool invert, CancellationToken cancellationToken) {
        byte[] result = new byte[values.Length];
        for (int i = 0; i < values.Length; i++) {
            if (i % 65536 == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            bool white = values[i] > threshold;
            result[i] = white != invert ? (byte)255 : (byte)0;
        }
        return result;
    }

    private static Mat ByteMat(byte[] bytes, int width, int height, CancellationToken cancellationToken) {
        Mat mat = new Mat(height, width, MatType.CV_8UC1);
        try {
            for (int y = 0; y < height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(bytes, y * width, mat.Ptr(y), width);
            }
            return mat;
        }
        catch { mat.Dispose(); throw; }
    }

    private static Region[] Components(Mat binary, CancellationToken cancellationToken) {
        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        Region[] result = new Region[count - 1];
        int[] row = new int[5];
        for (int label = 1; label < count; label++) {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(stats.Ptr(label), row, 0, 5);
            result[label - 1] = new Region(new AnalysisPlateRectangle(row[0], row[1], row[2], row[3]), row[4]);
        }
        return result;
    }

    private sealed record Candidate(AnalysisPlateRectangle Box, double Score, string Mask);
    private sealed record Region(AnalysisPlateRectangle Box, int Pixels);
}
