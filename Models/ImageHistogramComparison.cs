namespace ImageVisionApp.Models;

public sealed class ImageHistogramComparison {

    public ImageHistogramData OriginalHistogram { get; }
    public ImageHistogramData ProcessedHistogram { get; }

    public ImageHistogramComparison(
        ImageHistogramData originalHistogram,
        ImageHistogramData processedHistogram
        ) {

        OriginalHistogram = originalHistogram;
        ProcessedHistogram = processedHistogram;
    }
}