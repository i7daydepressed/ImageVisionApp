using System;

namespace ImageVisionApp.Models;

public readonly record struct AnalysisKeypoint(int X, int Y);

public sealed class AnalysisKeypointResult {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public int Width { get; }
    public int Height { get; }
    public int Threshold { get; }
    public bool NonmaxSuppression { get; }
    public ReadOnlyMemory<byte> SourceRgb { get; }
    public ReadOnlyMemory<AnalysisKeypoint> Points { get; }

    internal AnalysisKeypointResult(AnalysisInputSnapshot input, int width, int height,
        int threshold, bool nonmaxSuppression, byte[] sourceRgb, AnalysisKeypoint[] points) {

        Source = input.Source;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        Threshold = threshold;
        NonmaxSuppression = nonmaxSuppression;
        SourceRgb = sourceRgb;
        Points = points;
    }
}
