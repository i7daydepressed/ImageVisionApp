using System;

namespace ImageVisionApp.Models;

public sealed class AnalysisEdgeResult {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public int Width { get; }
    public int Height { get; }
    public AnalysisEdgeMethod Method { get; }
    public double? CannyLowerThreshold { get; }
    public double? CannyUpperThreshold { get; }
    public ReadOnlyMemory<byte> SourceRgb { get; }
    public ReadOnlyMemory<byte> Intensities { get; }

    internal AnalysisEdgeResult(
        AnalysisInputSnapshot input, int width, int height,
        byte[] sourceRgb, byte[] intensities, AnalysisEdgeMethod method = AnalysisEdgeMethod.Roberts,
        double? cannyLowerThreshold = null, double? cannyUpperThreshold = null) {

        if (width <= 0 || height <= 0 ||
            sourceRgb.Length != checked(width * height * 3) ||
            intensities.Length != checked(width * height)) {
            throw new ArgumentException("Размеры карты границ не совпадают с данными");
        }

        if (method != AnalysisEdgeMethod.Roberts && method != AnalysisEdgeMethod.Canny) {
            throw new ArgumentOutOfRangeException(nameof(method));
        }
        if ((method == AnalysisEdgeMethod.Canny && (cannyLowerThreshold is null || cannyUpperThreshold is null)) ||
            (method == AnalysisEdgeMethod.Roberts && (cannyLowerThreshold is not null || cannyUpperThreshold is not null))) {
            throw new ArgumentException("Параметры результата не соответствуют методу границ");
        }

        Method = method;
        CannyLowerThreshold = cannyLowerThreshold;
        CannyUpperThreshold = cannyUpperThreshold;
        Source = input.Source;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        // сервис передает владение свежими массивами, наружу доступны только чтение и копирование
        SourceRgb = sourceRgb;
        Intensities = intensities;
    }
}
