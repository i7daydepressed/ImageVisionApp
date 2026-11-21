using System;
using System.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoFrameAnalysisSettings(AnalysisEdgeMethod? EdgeMethod, double Lower, double Upper,
    int? Clusters, int? FastThreshold, bool FastNonmaxSuppression);

internal sealed record VideoFrameAnalysisResult(AnalysisEdgeResult? Edges, AnalysisSegmentationResult? Segmentation,
    AnalysisKeypointResult? Keypoints);

internal static class VideoFrameAnalysisService {
    // используем два снимка одного кадра, деля RGB и яркость между методами с общим входом
    public static VideoFrameAnalysisResult Analyze(AnalysisInputSnapshot original, AnalysisInputSnapshot? edited,
        AnalysisImageSource edgeSource, AnalysisImageSource segmentationSource, VideoFrameAnalysisSettings settings,
        CancellationToken token) {
        VideoAnalysisCapture source = original.Video ?? throw new ArgumentException("Отсутствует снимок оригинала");
        if (original.Source != AnalysisImageSource.Original || edited is not null &&
            (edited.Video is not { } processed || !source.Key.IsSameFrame(processed.Key))) {
            throw new InvalidOperationException("Входы анализа относятся к разным кадрам");
        }
        AnalysisInputSnapshot edgeInput = edgeSource == AnalysisImageSource.Edited ? edited ?? original : original;
        AnalysisInputSnapshot segInput = segmentationSource == AnalysisImageSource.Edited ? edited ?? original : original;
        if (settings.EdgeMethod is not null && edgeInput.Source != edgeSource ||
            settings.Clusters is not null && segInput.Source != segmentationSource) {
            throw new InvalidOperationException("Нет снимка для выбранного источника метода");
        }

        byte[]? originalRgb = null;
        byte[]? editedRgb = null;
        byte[]? originalGray = null;

        // получаем RGB каждой версии кадра только при первом использовании
        byte[] ReadRgb(AnalysisInputSnapshot input) {
            VideoAnalysisCapture capture = input.Video!;
            if (capture.Pixels.Size != capture.Key.Geometry.OutputSize) {
                throw new InvalidOperationException("Размер кадра не соответствует выбранному источнику");
            }
            return input.Source == AnalysisImageSource.Original
                ? originalRgb ??= capture.Pixels.CopyRgb(token)
                : editedRgb ??= capture.Pixels.CopyRgb(token);
        }

        AnalysisEdgeResult? edges = null;
        if (settings.EdgeMethod is { } method) {
            var size = edgeInput.Video!.Pixels.Size;
            byte[] rgb = ReadRgb(edgeInput);
            if (method == AnalysisEdgeMethod.Canny) {
                byte[] gray = AnalysisImageDecoder.ToLuminance(rgb, size.Width, size.Height, token);
                if (edgeInput.Source == AnalysisImageSource.Original) {
                    originalGray = gray;
                }
                edges = new(edgeInput, size.Width, size.Height, rgb,
                    CannyAnalysisService.CalculateFromGray(gray, size.Width, size.Height, settings.Lower, settings.Upper, token),
                    method, settings.Lower, settings.Upper);
            }
            else {
                edges = new(edgeInput, size.Width, size.Height, rgb,
                    RobertsAnalysisService.CalculateFromRgb(rgb, size.Width, size.Height, token));
            }
        }
        AnalysisSegmentationResult? segmentation = null;
        if (settings.Clusters is { } count) {
            var size = segInput.Video!.Pixels.Size;
            byte[] rgb = ReadRgb(segInput);
            var (segmented, labels, centers, compactness) = KMeansAnalysisService.CalculateFromRgb(rgb, size.Width, size.Height, count, token);
            segmentation = new(segInput, size.Width, size.Height, count, rgb, segmented, labels, centers, compactness);
        }
        AnalysisKeypointResult? keypoints = null;
        if (settings.FastThreshold is { } threshold) {
            var size = source.Pixels.Size;
            byte[] rgb = ReadRgb(original);
            byte[] gray = originalGray ?? AnalysisImageDecoder.ToLuminance(rgb, size.Width, size.Height, token);
            keypoints = new(original, size.Width, size.Height, threshold, settings.FastNonmaxSuppression, rgb,
                FastAnalysisService.CalculateFromGray(gray, size.Width, size.Height, threshold, settings.FastNonmaxSuppression, token));
        }
        return new(edges, segmentation, keypoints);
    }
}
