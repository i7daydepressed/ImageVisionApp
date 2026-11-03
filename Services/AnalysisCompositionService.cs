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
        return CreatePng(edges, null, useInputBackground ? AnalysisResultBackground.Input : AnalysisResultBackground.Black,
            showEdges, cancellationToken);
    }

    public static byte[] CreatePng(
        AnalysisEdgeResult? edges, AnalysisSegmentationResult? segmentation,
        AnalysisResultBackground background, bool showEdges, CancellationToken cancellationToken = default) {

        return CreatePng(edges, segmentation, null, background, showEdges, false, cancellationToken);
    }

    public static byte[] CreatePng(
        AnalysisEdgeResult? edges, AnalysisSegmentationResult? segmentation, AnalysisKeypointResult? keypoints,
        AnalysisResultBackground background, bool showEdges, bool showKeypoints,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        if (edges is null && segmentation is null && keypoints is null) {
            throw new ArgumentException("Нет действительных данных для композиции");
        }
        if (background != AnalysisResultBackground.Black && background != AnalysisResultBackground.Input &&
            background != AnalysisResultBackground.Segmentation) {
            throw new ArgumentOutOfRangeException(nameof(background));
        }
        int width = edges?.Width ?? segmentation?.Width ?? keypoints!.Width;
        int height = edges?.Height ?? segmentation?.Height ?? keypoints!.Height;
        if (edges is not null && segmentation is not null &&
            (edges.Width != segmentation.Width || edges.Height != segmentation.Height ||
             edges.Source != segmentation.Source || edges.InputRevision != segmentation.InputRevision)) {
            throw new ArgumentException("Результаты относятся к разным входам");
        }
        if (keypoints is not null && (keypoints.Width != width || keypoints.Height != height ||
            keypoints.Source != (edges?.Source ?? segmentation?.Source ?? keypoints.Source) ||
            keypoints.InputRevision != (edges?.InputRevision ?? segmentation?.InputRevision ?? keypoints.InputRevision))) {
            throw new ArgumentException("Точки и подложка относятся к разным входам");
        }
        if (background == AnalysisResultBackground.Segmentation && segmentation is null) {
            throw new ArgumentException("Сегментированная подложка еще не рассчитана");
        }
        // собираем PNG из готовой карты и RGB-подложки, детектор здесь заново не вызываем
        // отклик управляет белым слоем: на черном фоне получаем саму карту, без слоя остается подложка
        ReadOnlySpan<byte> source = edges is not null ? edges.SourceRgb.Span
            : segmentation is not null ? segmentation.SourceRgb.Span : keypoints!.SourceRgb.Span;
        ReadOnlySpan<byte> segmented = segmentation is not null ? segmentation.SegmentedRgb.Span : ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> intensities = edges is not null ? edges.Intensities.Span : ReadOnlySpan<byte>.Empty;
        byte[] rgb = new byte[checked(width * height * 3)];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int pixel = y * width + x;
                int strength = showEdges && edges is not null ? intensities[pixel] : 0;
                for (int channel = 0; channel < 3; channel++) {
                    int index = pixel * 3 + channel;
                    int baseChannel = background == AnalysisResultBackground.Input ? source[index]
                        : background == AnalysisResultBackground.Segmentation ? segmented[index] : 0;
                    // белый слой с alpha=strength/255, на черном фоне канал точно равен strength
                    // белый слой сохраняет формулу A2, сегментация заменяет только подложку
                    rgb[index] = (byte)((baseChannel * (255 - strength) + 255 * strength + 127) / 255);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (showKeypoints && keypoints is not null) {
            DrawKeypoints(rgb, width, height, keypoints.Points.Span, cancellationToken);
        }
        using MagickImage image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
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

    private static void DrawKeypoints(byte[] rgb, int width, int height, ReadOnlySpan<AnalysisKeypoint> points,
        CancellationToken cancellationToken) {

        // сначала все черные обводки, затем желтые кресты, чтобы соседняя обводка не стерла точку
        DrawKeypointCrosses(rgb, width, height, points, 3, 1, 0, cancellationToken);
        DrawKeypointCrosses(rgb, width, height, points, 2, 0, 255, cancellationToken);
    }

    private static void DrawKeypointCrosses(byte[] rgb, int width, int height, ReadOnlySpan<AnalysisKeypoint> points,
        int radius, int halfStroke, byte redGreen, CancellationToken cancellationToken) {

        foreach (AnalysisKeypoint point in points) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int dy = -radius; dy <= radius; dy++) {
                for (int dx = -radius; dx <= radius; dx++) {
                    int x = point.X + dx;
                    int y = point.Y + dy;
                    if ((Math.Abs(dx) > halfStroke && Math.Abs(dy) > halfStroke) ||
                        x < 0 || x >= width || y < 0 || y >= height) {
                        continue;
                    }
                    int index = (y * width + x) * 3;
                    rgb[index] = redGreen;
                    rgb[index + 1] = redGreen;
                    rgb[index + 2] = 0;
                }
            }
        }
    }
}
