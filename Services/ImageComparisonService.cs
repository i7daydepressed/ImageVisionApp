using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace ImageVisionApp.Services;

internal static class ImageComparisonService {
    public const int MinimumImages = 4;

    private const int Side = ImageComparisonFeatures.Side;
    private const int ColorRange = byte.MaxValue * ImageComparisonFeatures.ColorCount;
    private const int GradientRange = 2 * byte.MaxValue * ImageComparisonFeatures.GradientCount;
    private const int SiftMaxSide = 1024;
    private const int SiftMaxPoints = 600;
    private const int MinimumInliers = 6;
    private const float MatchRatio = 0.75f;
    private const double ReprojectionThreshold = 3;

    public static bool CanUseGeometry =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    // один раз при загрузке строим признаки деталей и запасную сетку цветов и контуров
    public static ImageComparisonFeatures CreateFeatures(Bitmap bitmap, CancellationToken token = default) {
        byte[] rgb = ReadRgb(bitmap, new PixelSize(Side, Side), token);
        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, Side, Side, token);
        short[] gradients = new short[ImageComparisonFeatures.GradientCount];
        int index = 0;
        for (int y = 0; y < Side; y++) {
            for (int x = 0; x < Side; x++) {
                int pixel = y * Side + x;
                if (x < Side - 1) {
                    gradients[index++] = (short)(gray[pixel + 1] - gray[pixel]);
                }
                if (y < Side - 1) {
                    gradients[index++] = (short)(gray[pixel + Side] - gray[pixel]);
                }
            }
        }
        if (!CanUseGeometry) {
            return new(rgb, gradients);
        }
        var (points, descriptors) = ReadSift(bitmap, token);
        return new(rgb, gradients, points, descriptors);
    }

    // переносим видимые цвета с учетом прозрачности на черном фоне
    private static byte[] ReadRgb(Bitmap bitmap, PixelSize size, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        using Bitmap? scaled = bitmap.PixelSize == size ? null :
            bitmap.CreateScaledBitmap(size, BitmapInterpolationMode.HighQuality);
        using WriteableBitmap readback = new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer buffer = readback.Lock();
        (scaled ?? bitmap).CopyPixels(buffer);
        byte[] rgb = new byte[size.Width * size.Height * 3];
        byte[] row = new byte[size.Width * 4];
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (int x = 0; x < size.Width; x++) {
                int output = (y * size.Width + x) * 3;
                int input = x * 4;
                // premul уже содержит видимый вклад пикселя на черном фоне
                rgb[output] = row[input + 2];
                rgb[output + 1] = row[input + 1];
                rgb[output + 2] = row[input];
            }
        }
        return rgb;
    }

    // извлекаем детали из серого изображения с сохранением пропорций и ограничиваем объем снимка
    private static (Point2f[] Points, float[] Descriptors) ReadSift(Bitmap bitmap, CancellationToken token) {
        PixelSize original = bitmap.PixelSize;
        double scale = Math.Min(1, SiftMaxSide / (double)Math.Max(original.Width, original.Height));
        PixelSize size = new((int)Math.Round(original.Width * scale), (int)Math.Round(original.Height * scale));
        if (size.Width < 8 || size.Height < 8) {
            return ([], []);
        }
        byte[] rgb = ReadRgb(bitmap, size, token);
        byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, size.Width, size.Height, token);
        using Mat image = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC1, gray);
        using SIFT sift = SIFT.Create(SiftMaxPoints);
        using Mat descriptors = new();
        sift.DetectAndCompute(image, null, out KeyPoint[] keypoints, descriptors);
        token.ThrowIfCancellationRequested();
        // sift может оставить больше точек с равным откликом на границе
        int count = Math.Min(keypoints.Length, SiftMaxPoints);
        Point2f[] points = new Point2f[count];
        const int stride = ImageComparisonFeatures.SiftDescriptorSize;
        float[] values = new float[count * stride];
        for (int i = 0; i < count; i++) {
            points[i] = keypoints[i].Pt;
            Marshal.Copy(descriptors.Ptr(i), values, i * stride, stride);
        }
        return (points, values);
    }

    // сначала ищем подтвержденное геометрическое совпадение, при равенстве сохраняем порядок сетки
    public static (Guid First, Guid Second, bool UsesFallback) FindMostSimilarPair(
        IReadOnlyList<(Guid Id, ImageComparisonFeatures Features)> images, CancellationToken token = default) {
        if (images.Count is < MinimumImages or > ImageComparisonSet.Capacity) {
            throw new ArgumentOutOfRangeException(nameof(images), "Для сопоставления нужно от 4 до 9 изображений");
        }
        token.ThrowIfCancellationRequested();
        if (CanUseGeometry && FindGeometricPair(images, token) is { } geometric) {
            return (geometric.First, geometric.Second, false);
        }

        long bestDistance = long.MaxValue;
        (Guid First, Guid Second, bool UsesFallback) pair = default;
        for (int first = 0; first < images.Count - 1; first++) {
            for (int second = first + 1; second < images.Count; second++) {
                token.ThrowIfCancellationRequested();
                long distance = Distance(images[first].Features, images[second].Features);
                if (distance < bestDistance) {
                    bestDistance = distance;
                    pair = (images[first].Id, images[second].Id, true);
                }
            }
        }
        return pair;
    }

    // переиспользуем дескрипторы каждого изображения для всех его пар и освобождаем матрицы при отмене
    private static (Guid First, Guid Second)? FindGeometricPair(
        IReadOnlyList<(Guid Id, ImageComparisonFeatures Features)> images, CancellationToken token) {
        Mat?[] descriptors = new Mat?[images.Count];
        try {
            int eligible = 0;
            for (int i = 0; i < images.Count; i++) {
                token.ThrowIfCancellationRequested();
                ImageComparisonFeatures features = images[i].Features;
                int rows = features.SiftPoints.Length;
                if (rows >= MinimumInliers) {
                    descriptors[i] = Mat.FromPixelData(rows, ImageComparisonFeatures.SiftDescriptorSize,
                        MatType.CV_32FC1, features.SiftDescriptors.ToArray());
                    eligible++;
                }
            }
            if (eligible < 2) {
                return null;
            }

            using BFMatcher matcher = new(NormTypes.L2);
            int bestInliers = MinimumInliers - 1;
            (Guid First, Guid Second)? pair = null;
            for (int i = 0; i < images.Count - 1; i++) {
                if (descriptors[i] is not { } first) {
                    continue;
                }
                for (int j = i + 1; j < images.Count; j++) {
                    if (descriptors[j] is not { } second) {
                        continue;
                    }
                    int inliers = CountInliers(matcher, first, second, images[i].Features.SiftPoints,
                        images[j].Features.SiftPoints, token);
                    if (inliers > bestInliers) {
                        bestInliers = inliers;
                        pair = (images[i].Id, images[j].Id);
                    }
                }
            }
            return pair;
        }
        finally {
            foreach (Mat? descriptor in descriptors) {
                descriptor?.Dispose();
            }
        }
    }

    // проверяем геометрию в обе стороны, чтобы масштаб и порядок карточек не меняли оценку
    private static int CountInliers(BFMatcher matcher, Mat first, Mat second,
        ReadOnlySpan<Point2f> firstPoints, ReadOnlySpan<Point2f> secondPoints, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        DMatch[][] forward = matcher.KnnMatch(first, second, 2);
        token.ThrowIfCancellationRequested();
        DMatch[][] backward = matcher.KnnMatch(second, first, 2);
        token.ThrowIfCancellationRequested();
        int fromFirst = CountDirectionalInliers(forward, backward, firstPoints, secondPoints, token);
        if (fromFirst < MinimumInliers) {
            return 0;
        }
        int fromSecond = CountDirectionalInliers(backward, forward, secondPoints, firstPoints, token);
        return fromSecond < MinimumInliers ? 0 : Math.Min(fromFirst, fromSecond);
    }

    // оставляем взаимные совпадения с уникальными позициями и проверяем проекцию в пикселях цели
    private static int CountDirectionalInliers(DMatch[][] forward, DMatch[][] backward,
        ReadOnlySpan<Point2f> firstPoints, ReadOnlySpan<Point2f> secondPoints, CancellationToken token) {
        List<DMatch> mutual = [];
        foreach (DMatch[] neighbors in forward) {
            if (PassesRatio(neighbors)) {
                DMatch best = neighbors[0];
                DMatch[] reverse = backward[best.TrainIdx];
                if (PassesRatio(reverse) && reverse[0].TrainIdx == best.QueryIdx) {
                    mutual.Add(best);
                }
            }
        }

        // при нескольких ориентациях в одной позиции предпочитаем ближайший дескриптор
        mutual.Sort((left, right) => left.Distance == right.Distance
            ? left.QueryIdx.CompareTo(right.QueryIdx) : left.Distance.CompareTo(right.Distance));
        List<Point2d> source = [];
        List<Point2d> target = [];
        HashSet<(int X, int Y)> sourcePixels = [];
        HashSet<(int X, int Y)> targetPixels = [];
        foreach (DMatch match in mutual) {
            Point2f from = firstPoints[match.QueryIdx];
            Point2f to = secondPoints[match.TrainIdx];
            var fromPixel = ((int)MathF.Round(from.X), (int)MathF.Round(from.Y));
            var toPixel = ((int)MathF.Round(to.X), (int)MathF.Round(to.Y));
            if (sourcePixels.Contains(fromPixel) || targetPixels.Contains(toPixel)) {
                continue;
            }
            sourcePixels.Add(fromPixel);
            targetPixels.Add(toPixel);
            source.Add(new Point2d(from.X, from.Y));
            target.Add(new Point2d(to.X, to.Y));
        }
        if (source.Count < MinimumInliers) {
            return 0;
        }

        token.ThrowIfCancellationRequested();
        using Mat mask = new();
        using Mat homography = Cv2.FindHomography(source, target, HomographyMethods.Ransac, ReprojectionThreshold, mask);
        token.ThrowIfCancellationRequested();
        return homography.Empty() || mask.Empty() ? 0 : Cv2.CountNonZero(mask);
    }

    // отсекаем совпадение, если ближайший дескриптор не отделен от следующего
    private static bool PassesRatio(DMatch[] neighbors) =>
        neighbors.Length == 2 && neighbors[0].Distance < MatchRatio * neighbors[1].Distance;

    // приводим ошибки цветов и контуров к одному знаменателю с весами 3:1, без округления
    internal static long Distance(ImageComparisonFeatures first, ImageComparisonFeatures second) {
        ReadOnlySpan<byte> firstColors = first.Rgb;
        ReadOnlySpan<byte> secondColors = second.Rgb;
        int color = 0;
        for (int i = 0; i < firstColors.Length; i++) {
            color += Math.Abs(firstColors[i] - secondColors[i]);
        }

        ReadOnlySpan<short> firstGradients = first.Gradients;
        ReadOnlySpan<short> secondGradients = second.Gradients;
        int gradient = 0;
        for (int i = 0; i < firstGradients.Length; i++) {
            gradient += Math.Abs(firstGradients[i] - secondGradients[i]);
        }
        return 3L * color * GradientRange + (long)gradient * ColorRange;
    }
}
