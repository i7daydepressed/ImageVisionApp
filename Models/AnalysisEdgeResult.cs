using System;

namespace ImageVisionApp.Models;

public sealed class AnalysisEdgeResult {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> SourceRgb { get; }
    public ReadOnlyMemory<byte> Intensities { get; }

    internal AnalysisEdgeResult(
        AnalysisInputSnapshot input, int width, int height,
        byte[] sourceRgb, byte[] intensities) {

        if (width <= 0 || height <= 0 ||
            sourceRgb.Length != checked(width * height * 3) ||
            intensities.Length != checked(width * height)) {
            throw new ArgumentException("Размеры карты границ не совпадают с данными");
        }

        Source = input.Source;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        // сервис передает владение свежими массивами, наружу доступны только чтение и копирование
        SourceRgb = sourceRgb;
        Intensities = intensities;
    }
}
