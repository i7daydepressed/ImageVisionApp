using System;
using System.Collections.Generic;

namespace ImageVisionApp.Models;

public sealed record AnalysisPlateRectangle(int X, int Y, int Width, int Height) {
    internal int Right => checked(X + Width);
    internal int Bottom => checked(Y + Height);
    internal long Area => (long)Width * Height;
    internal bool IsWithin(int width, int height) =>
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && (long)X + Width <= width && (long)Y + Height <= height;
}

public sealed class AnalysisPlateResult {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> SourceRgb { get; }
    public IReadOnlyList<AnalysisPlateRectangle> Rectangles { get; }
    public AnalysisPlateRectangle? Rectangle => Rectangles.Count == 0 ? null : Rectangles[0];
    internal VideoAnalysisFrameKey? VideoFrame { get; }

    // сохраняем ранжированные области и отметку физического кадра для проверки слоя
    internal AnalysisPlateResult(AnalysisInputSnapshot input, int width, int height,
        byte[] sourceRgb, IReadOnlyList<AnalysisPlateRectangle> rectangles) {

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sourceRgb);
        ArgumentNullException.ThrowIfNull(rectangles);
        if (width <= 0 || height <= 0 || sourceRgb.Length != checked(width * height * 3)) {
            throw new ArgumentException("Область номера не соответствует геометрии входа");
        }
        AnalysisPlateRectangle[] copy = new AnalysisPlateRectangle[rectangles.Count];
        for (int i = 0; i < copy.Length; i++) {
            if (rectangles[i] is not { } rectangle || !rectangle.IsWithin(width, height)) {
                throw new ArgumentException("Область номера не соответствует геометрии входа");
            }
            copy[i] = rectangle;
        }
        Source = input.Source;
        VideoFrame = input.Video?.Key;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        SourceRgb = sourceRgb;
        // пустая коллекция означает завершенный поиск без кандидатов
        Rectangles = Array.AsReadOnly(copy);
    }
}
