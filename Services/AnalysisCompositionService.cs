using System;
using System.Threading;
using Avalonia;
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

        return CreatePng(edges, segmentation, keypoints, null, background, showEdges, showKeypoints, false, cancellationToken);
    }

    public static byte[] CreatePng(
        AnalysisEdgeResult? edges, AnalysisSegmentationResult? segmentation, AnalysisKeypointResult? keypoints,
        AnalysisPlateResult? plate, AnalysisResultBackground background, bool showEdges, bool showKeypoints,
        bool showPlate, CancellationToken cancellationToken = default) =>
        CreatePng(edges, segmentation, keypoints, plate, background, showEdges, showKeypoints,
            showPlate ? 1 : 0, cancellationToken);

    // для текущего фото передаем только лимит показа и используем уже рассчитанные области
    internal static byte[] CreatePng(
        AnalysisEdgeResult? edges, AnalysisSegmentationResult? segmentation, AnalysisKeypointResult? keypoints,
        AnalysisPlateResult? plate, AnalysisResultBackground background, bool showEdges, bool showKeypoints,
        int plateLimit, CancellationToken cancellationToken) {

        cancellationToken.ThrowIfCancellationRequested();
        if (edges is null && segmentation is null && keypoints is null && plate is null) {
            throw new ArgumentException("Нет действительных данных для композиции");
        }
        if (background != AnalysisResultBackground.Black && background != AnalysisResultBackground.Input &&
            background != AnalysisResultBackground.Segmentation) {
            throw new ArgumentOutOfRangeException(nameof(background));
        }
        int width = edges?.Width ?? segmentation?.Width ?? keypoints?.Width ?? plate!.Width;
        int height = edges?.Height ?? segmentation?.Height ?? keypoints?.Height ?? plate!.Height;
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
        if (plate is not null && (plate.Width != width || plate.Height != height ||
            plate.Source != (edges?.Source ?? segmentation?.Source ?? keypoints?.Source ?? plate.Source) ||
            plate.InputRevision != (edges?.InputRevision ?? segmentation?.InputRevision ?? keypoints?.InputRevision ?? plate.InputRevision))) {
            throw new ArgumentException("Рамка и подложка относятся к разным входам");
        }
        if (background == AnalysisResultBackground.Segmentation && segmentation is null) {
            throw new ArgumentException("Сегментированная подложка еще не рассчитана");
        }
        // собираем PNG из готовой карты и RGB-подложки, детектор здесь заново не вызываем
        // отклик управляет белым слоем: на черном фоне получаем саму карту, без слоя остается подложка
        ReadOnlySpan<byte> source = edges is not null ? edges.SourceRgb.Span
            : segmentation is not null ? segmentation.SourceRgb.Span
            : keypoints is not null ? keypoints.SourceRgb.Span : plate!.SourceRgb.Span;
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
        if (plate is not null) {
            DrawPlateRectangles(rgb, width, height, plate, plateLimit, null, cancellationToken);
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

    // рисуем готовые слои в координатах заданной видеоподложки и возвращаем пиксели для интерфейса
    internal static VideoEditorFrame ComposeVideoFrame(VideoAnalysisFrameKey backdrop,
        VideoEditorFrame? image, AnalysisSegmentationResult? segmentation, AnalysisEdgeResult? edges,
        AnalysisKeypointResult? keypoints, CancellationToken token) =>
        ComposeVideoFrame(backdrop, image, segmentation, edges, keypoints, null, 0, token);

    // совместимый слой номера рисуем поверх всех остальных слоев на координатах выбранной подложки
    internal static VideoEditorFrame ComposeVideoFrame(VideoAnalysisFrameKey backdrop,
        VideoEditorFrame? image, AnalysisSegmentationResult? segmentation, AnalysisEdgeResult? edges,
        AnalysisKeypointResult? keypoints, AnalysisPlateResult? plate, int plateLimit, CancellationToken token) {

        PixelSize size = backdrop.Geometry.OutputSize;
        byte[] rgb;
        if (segmentation is not null) {
            VideoEditorGeometry transform = GetLayerTransform(segmentation.VideoFrame, segmentation.Width, segmentation.Height, backdrop);
            if (transform.ClockwiseQuarterTurns != 0) {
                throw new ArgumentException("Сегментированная подложка должна иметь собственную геометрию");
            }
            rgb = segmentation.SegmentedRgb.ToArray();
        }
        else if (image is not null) {
            if (image.Size != size) {
                throw new ArgumentException("Размер видеоподложки не соответствует кадру");
            }
            rgb = image.CopyRgb(token);
        }
        else {
            rgb = new byte[checked(size.Width * size.Height * 3)];
        }

        if (edges is not null) {
            VideoEditorGeometry transform = GetLayerTransform(edges.VideoFrame, edges.Width, edges.Height, backdrop);
            BlendVideoEdges(rgb, size.Width, edges.Intensities.Span, transform, token);
        }
        if (keypoints is not null) {
            VideoEditorGeometry transform = GetLayerTransform(keypoints.VideoFrame, keypoints.Width, keypoints.Height, backdrop);
            ReadOnlySpan<AnalysisKeypoint> points = keypoints.Points.Span;
            if (transform.ClockwiseQuarterTurns != 0) {
                AnalysisKeypoint[] mapped = new AnalysisKeypoint[points.Length];
                for (int i = 0; i < mapped.Length; i++) {
                    token.ThrowIfCancellationRequested();
                    PixelPoint pixel = transform.MapPixel(new(points[i].X, points[i].Y));
                    mapped[i] = new(pixel.X, pixel.Y);
                }
                DrawKeypoints(rgb, size.Width, size.Height, mapped, token);
            }
            else {
                DrawKeypoints(rgb, size.Width, size.Height, points, token);
            }
        }
        if (plate is not null && plateLimit > 0) {
            VideoEditorGeometry transform = GetLayerTransform(plate.VideoFrame, plate.Width, plate.Height, backdrop);
            DrawPlateRectangles(rgb, size.Width, size.Height, plate, plateLimit, transform, token);
        }
        return VideoEditorFrame.FromRgb(size, rgb, token);
    }

    // проверяем, что слой относится к тому же кадру, а его растр можно повернуть на подложку
    private static VideoEditorGeometry GetLayerTransform(VideoAnalysisFrameKey? layer, int width, int height,
        VideoAnalysisFrameKey backdrop) {
        if (layer is not { } source || !source.IsSameFrame(backdrop) || source.Geometry.OutputSize != new PixelSize(width, height)) {
            throw new ArgumentException("Слой и подложка относятся к разным кадрам или размерам");
        }
        return source.Geometry.RelativeTo(backdrop.Geometry);
    }

    // накладываем белый отклик границ и переносим каждый пиксель карты без интерполяции
    private static void BlendVideoEdges(byte[] rgb, int outputWidth, ReadOnlySpan<byte> intensities,
        VideoEditorGeometry transform, CancellationToken token) {
        PixelSize size = transform.InputSize;
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < size.Width; x++) {
                int strength = intensities[y * size.Width + x];
                if (strength == 0) {
                    continue;
                }
                PixelPoint pixel = transform.MapPixel(new(x, y));
                int first = (pixel.Y * outputWidth + pixel.X) * 3;
                for (int i = first; i < first + 3; i++) {
                    rgb[i] = (byte)((rgb[i] * (255 - strength) + 255 * strength + 127) / 255);
                }
            }
        }
    }

    // берем заданное число лучших рамок и переносим их границы без дробных координат
    private static void DrawPlateRectangles(byte[] rgb, int width, int height, AnalysisPlateResult plate,
        int limit, VideoEditorGeometry? transform, CancellationToken token) {
        for (int i = 0; i < Math.Min(limit, plate.Rectangles.Count); i++) {
            AnalysisPlateRectangle rectangle = plate.Rectangles[i];
            if (transform is { ClockwiseQuarterTurns: not 0 } rotation) {
                PixelPoint first = rotation.MapPixel(new(rectangle.X, rectangle.Y));
                PixelPoint opposite = rotation.MapPixel(new(rectangle.Right - 1, rectangle.Bottom - 1));
                rectangle = new(Math.Min(first.X, opposite.X), Math.Min(first.Y, opposite.Y),
                    Math.Abs(opposite.X - first.X) + 1, Math.Abs(opposite.Y - first.Y) + 1);
            }
            DrawPlateRectangle(rgb, width, height, rectangle, token);
        }
    }

    private static void DrawPlateRectangle(byte[] rgb, int width, int height, AnalysisPlateRectangle rectangle,
        CancellationToken cancellationToken) {

        if (!rectangle.IsWithin(width, height)) {
            throw new ArgumentException("Рамка вышла за границы выбранного входа");
        }
        // зеленая полоса толщиной три пикселя проходит внутри рамки и перекрывает точки
        for (int y = rectangle.Y; y < rectangle.Bottom; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = rectangle.X; x < rectangle.Right; x++) {
                if (x >= rectangle.X + 3 && x < rectangle.Right - 3 &&
                    y >= rectangle.Y + 3 && y < rectangle.Bottom - 3) {
                    continue;
                }
                int offset = (y * width + x) * 3;
                rgb[offset] = 0;
                rgb[offset + 1] = 255;
                rgb[offset + 2] = 0;
            }
        }
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
