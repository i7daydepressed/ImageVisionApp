using System;
using System.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoFrameAnalysisSettings(AnalysisEdgeMethod? EdgeMethod, double Lower, double Upper,
    int? Clusters, int? FastThreshold, bool FastNonmaxSuppression);

internal sealed record VideoFrameAnalysisResult(AnalysisEdgeResult? Edges, AnalysisSegmentationResult? Segmentation,
    AnalysisKeypointResult? Keypoints, byte[] Png);

internal static class VideoFrameAnalysisService {
    // готовим общий RGB-вход и передаем его действующим алгоритмам без промежуточного PNG
    public static VideoFrameAnalysisResult Analyze(AnalysisInputSnapshot input, VideoFrameAnalysisSettings settings,
        CancellationToken token) {
        VideoAnalysisCapture capture = input.Video ?? throw new ArgumentException("Отсутствует снимок видеокадра");
        VideoEditorFrame frame = capture.Pixels;
        token.ThrowIfCancellationRequested();
        if (frame.Size != capture.Key.Geometry.OutputSize) {
            throw new InvalidOperationException("Размер кадра не соответствует выбранному источнику");
        }
        int width = frame.Size.Width;
        int height = frame.Size.Height;
        byte[] rgb = frame.CopyRgb(token);
        byte[]? gray = settings.EdgeMethod == AnalysisEdgeMethod.Canny || settings.FastThreshold is not null
            ? AnalysisImageDecoder.ToLuminance(rgb, width, height, token) : null;

        AnalysisEdgeResult? edges = settings.EdgeMethod switch {
            AnalysisEdgeMethod.Roberts => new(input, width, height, rgb,
                RobertsAnalysisService.CalculateFromRgb(rgb, width, height, token)),
            AnalysisEdgeMethod.Canny => new(input, width, height, rgb,
                CannyAnalysisService.CalculateFromGray(gray!, width, height, settings.Lower, settings.Upper, token),
                AnalysisEdgeMethod.Canny, settings.Lower, settings.Upper),
            _ => null
        };
        AnalysisSegmentationResult? segmentation = null;
        if (settings.Clusters is { } count) {
            var (segmented, labels, centers, compactness) = KMeansAnalysisService.CalculateFromRgb(rgb, width, height, count, token);
            segmentation = new(input, width, height, count, rgb, segmented, labels, centers, compactness);
        }
        AnalysisKeypointResult? keypoints = settings.FastThreshold is { } threshold
            ? new(input, width, height, threshold, settings.FastNonmaxSuppression, rgb,
                FastAnalysisService.CalculateFromGray(gray!, width, height, threshold, settings.FastNonmaxSuppression, token))
            : null;

        AnalysisResultBackground background = segmentation is not null ? AnalysisResultBackground.Segmentation :
            keypoints is not null ? AnalysisResultBackground.Input : AnalysisResultBackground.Black;
        byte[] png = AnalysisCompositionService.CreatePng(edges, segmentation, keypoints, background,
            showEdges: edges is not null, showKeypoints: keypoints is not null, token);
        return new(edges, segmentation, keypoints, png);
    }
}
