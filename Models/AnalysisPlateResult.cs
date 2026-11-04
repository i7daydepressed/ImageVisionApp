using System;

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
    public AnalysisPlateRectangle? Rectangle { get; }

    internal AnalysisPlateResult(AnalysisInputSnapshot input, int width, int height,
        byte[] sourceRgb, AnalysisPlateRectangle? rectangle) {

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sourceRgb);
        if (width <= 0 || height <= 0 || sourceRgb.Length != checked(width * height * 3) ||
            (rectangle is not null && !rectangle.IsWithin(width, height))) {
            throw new ArgumentException("Область номера не соответствует геометрии входа");
        }
        Source = input.Source;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        SourceRgb = sourceRgb;
        // null означает завершенный поиск без кандидата, а не ошибку обработки
        Rectangle = rectangle;
    }
}
