using System;

namespace ImageVisionApp.Models;

public sealed class AnalysisInputSnapshot {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public byte[] ImageData { get; }

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
}
