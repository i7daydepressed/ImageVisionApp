using System;
using ImageVisionApp.Services;

namespace ImageVisionApp.Models;

public sealed class AnalysisInputSnapshot {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public byte[] ImageData { get; }
    internal VideoAnalysisCapture? Video { get; }

    public AnalysisInputSnapshot(
        AnalysisImageSource source,
        long inputRevision,
        byte[] imageData) {

        ArgumentNullException.ThrowIfNull(imageData);
        Source = source;
        InputRevision = inputRevision;
        // снимок владеет своей копией, изменение массива не затрагивает документ
        ImageData = (byte[])imageData.Clone();
    }

    // принимаем принадлежащие запросу пиксели кадра отдельно от байтов фото
    internal AnalysisInputSnapshot(AnalysisImageSource source, long inputRevision, VideoAnalysisCapture video) {
        Source = source;
        InputRevision = inputRevision;
        ImageData = [];
        Video = video;
    }
}

internal readonly record struct VideoAnalysisFrameKey(MediaDocument Document, AnalysisImageSource Source,
    long SourceRevision, long Frame, int PreviewRevision, long PlanRevision, long Position,
    VideoFrameCursor? Cursor, VideoEditorGeometry Geometry);

internal sealed record VideoAnalysisCapture(VideoAnalysisFrameKey Key, VideoEditorFrame Pixels, VideoEditorPlan? Plan);
