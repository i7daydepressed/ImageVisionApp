using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

public sealed class HistogramPanel : StackPanel {
    private readonly HistogramChart originalChart = new HistogramChart {
        Height = 260
    };

    private readonly HistogramChart processedChart = new HistogramChart {
        Height = 260
    };

    private readonly TextBlock scaleText = new TextBlock();

    public HistogramPanel() {
        Margin = new Thickness(12);
        Spacing = 10;

        Children.Add(new TextBlock {
            Text = "Сравнение RGB-гистограмм",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        });

        Children.Add(scaleText);

        Grid charts = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12
        };

        charts.Children.Add(CreateChartPanel("До обработки", originalChart));

        Border processedPanel =
            CreateChartPanel("После обработки", processedChart);
        Grid.SetColumn(processedPanel, 1);
        charts.Children.Add(processedPanel);

        Children.Add(charts);
        ShowComparison(null);
    }

    public void ShowComparison(ImageHistogramComparison? comparison) {
        originalChart.HistogramData = comparison?.OriginalHistogram;
        processedChart.HistogramData = comparison?.ProcessedHistogram;

        if (comparison is null) {
            scaleText.Text = "Откройте изображение, чтобы увидеть гистограммы";
            return;
        }

        // Оба графика рисуем в одном масштабе, чтобы высоту пиков можно было сравнить.
        long maximum = Math.Max(
            1,
            Math.Max(
                FindMaximum(comparison.OriginalHistogram),
                FindMaximum(comparison.ProcessedHistogram)));

        originalChart.VerticalMaximum = maximum;
        processedChart.VerticalMaximum = maximum;
        scaleText.Text = $"Ось Y: количество пикселей; общий максимум: {maximum:N0}";
    }

    private static Border CreateChartPanel(string title, HistogramChart chart) {
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel {
                Spacing = 8,
                Children = {
                    new TextBlock {
                        Text = title,
                        FontSize = 17,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    chart
                }
            }
        };
    }

    private static long FindMaximum(ImageHistogramData histogram) {
        long maximum = 0;

        for (int intensity = 0; intensity < 256; intensity++) {
            maximum = Math.Max(maximum, histogram.RedValues[intensity]);
            maximum = Math.Max(maximum, histogram.GreenValues[intensity]);
            maximum = Math.Max(maximum, histogram.BlueValues[intensity]);
        }

        return maximum;
    }
}