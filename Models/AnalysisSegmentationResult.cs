using System;

namespace ImageVisionApp.Models;

public sealed class AnalysisSegmentationResult {
    public AnalysisImageSource Source { get; }
    public long InputRevision { get; }
    public int Width { get; }
    public int Height { get; }
    public int ClusterCount { get; }
    public double Compactness { get; }
    public ReadOnlyMemory<byte> SourceRgb { get; }
    public ReadOnlyMemory<byte> SegmentedRgb { get; }
    public ReadOnlyMemory<int> Labels { get; }
    public ReadOnlyMemory<float> CentersRgb { get; }

    internal AnalysisSegmentationResult(
        AnalysisInputSnapshot input, int width, int height, int clusterCount, byte[] sourceRgb,
        byte[] segmentedRgb, int[] labels, float[] centersRgb, double compactness) {

        if (width <= 0 || height <= 0 || clusterCount < 2 || clusterCount > 20 ||
            sourceRgb.Length != checked(width * height * 3) || segmentedRgb.Length != sourceRgb.Length ||
            labels.Length != checked(width * height) || centersRgb.Length != clusterCount * 3 ||
            !double.IsFinite(compactness) || compactness < 0) {
            throw new ArgumentException("Данные сегментации не соответствуют размерам и числу кластеров");
        }
        Source = input.Source;
        InputRevision = input.InputRevision;
        Width = width;
        Height = height;
        ClusterCount = clusterCount;
        Compactness = compactness;
        // сервис передает свежие массивы, цвета центров хранятся в порядке RGB
        SourceRgb = sourceRgb;
        SegmentedRgb = segmentedRgb;
        Labels = labels;
        CentersRgb = centersRgb;
    }
}
