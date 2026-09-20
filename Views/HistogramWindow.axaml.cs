using System;
using Avalonia.Controls;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

public partial class HistogramWindow : Window {

    public HistogramWindow() {
        InitializeComponent();
    }

    public HistogramWindow(
        ImageHistogramComparison comparison
        ) : this() {

        long originalMaximum =
            FindMaximum(comparison.OriginalHistogram);
        long processedMaximum =
            FindMaximum(comparison.ProcessedHistogram);
        long sharedMaximum = Math.Max(
            1,
            Math.Max(originalMaximum, processedMaximum));

        HistogramChart originalChart =
            this.FindControl<HistogramChart>("OriginalHistogramChart")
            ?? throw new InvalidOperationException(
                "не найден график исходного изображения");
        HistogramChart processedChart =
            this.FindControl<HistogramChart>("ProcessedHistogramChart")
            ?? throw new InvalidOperationException(
                "не найден график обработанного изображения");
        TextBlock sharedScaleText =
            this.FindControl<TextBlock>("SharedScaleText")
            ?? throw new InvalidOperationException(
                "не найдена подпись общей шкалы");

        originalChart.HistogramData =
            comparison.OriginalHistogram;
        originalChart.VerticalMaximum = sharedMaximum;

        processedChart.HistogramData =
            comparison.ProcessedHistogram;
        processedChart.VerticalMaximum = sharedMaximum;

        sharedScaleText.Text =
            $"Общая вертикальная шкала: 0–{sharedMaximum:N0} пикселей";
    }

    private static long FindMaximum(
        ImageHistogramData histogramData
        ) {

        long maximumValue = 0;

        for (int intensity = 0; intensity < 256; intensity++) {
            maximumValue = Math.Max(
                maximumValue,
                histogramData.RedValues[intensity]);
            maximumValue = Math.Max(
                maximumValue,
                histogramData.GreenValues[intensity]);
            maximumValue = Math.Max(
                maximumValue,
                histogramData.BlueValues[intensity]);
        }

        return maximumValue;
    }
}
