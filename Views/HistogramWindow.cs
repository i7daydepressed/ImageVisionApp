using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

public class HistogramWindow : Window {
    // держим ссылки на созданные графики, чтобы потом сразу передать им данные
    private readonly HistogramChart originalChart = new HistogramChart {
        Name = "OriginalHistogramChart",
        MinHeight = 320
    };
    private readonly HistogramChart processedChart = new HistogramChart {
        Name = "ProcessedHistogramChart",
        MinHeight = 320
    };
    private readonly TextBlock sharedScaleText = new TextBlock {
        Name = "SharedScaleText",
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    public HistogramWindow() {
        Title = "Гистограммы изображения";
        Width = 1100;
        Height = 650;
        MinWidth = 800;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = CreateLayout();
    }

    public HistogramWindow(
        ImageHistogramComparison comparison
        ) : this() {

        long originalMaximum =
            FindMaximum(comparison.OriginalHistogram);
        long processedMaximum =
            FindMaximum(comparison.ProcessedHistogram);
        // одна шкала на оба графика, иначе высоту пиков до/после не сравнить
        long sharedMaximum = Math.Max(
            1,
            Math.Max(originalMaximum, processedMaximum));

        originalChart.HistogramData =
            comparison.OriginalHistogram;
        originalChart.VerticalMaximum = sharedMaximum;

        processedChart.HistogramData =
            comparison.ProcessedHistogram;
        processedChart.VerticalMaximum = sharedMaximum;

        sharedScaleText.Text =
            "Ось Y: логарифмическая, количество пикселей; " +
            $"общий максимум: {sharedMaximum:N0}";
    }

    private Grid CreateLayout() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 12,
            Margin = new Thickness(16)
        };
        layout.Children.Add(new TextBlock {
            Text = "Сравнение RGB-гистограмм",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        });

        Grid legend = new Grid {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 24
        };
        legend.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = {
                CreateLegendItem("Красный", "#FF4646"),
                CreateLegendItem("Зелёный", "#46DC46"),
                CreateLegendItem("Синий", "#4682FF")
            }
        });
        Grid.SetColumn(sharedScaleText, 1);
        legend.Children.Add(sharedScaleText);
        Grid.SetRow(legend, 1);
        layout.Children.Add(legend);

        Grid charts = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12
        };
        charts.Children.Add(CreateChartPanel("До обработки", originalChart));
        Border processedPanel = CreateChartPanel("После обработки", processedChart);
        Grid.SetColumn(processedPanel, 1);
        charts.Children.Add(processedPanel);
        Grid.SetRow(charts, 2);
        layout.Children.Add(charts);
        return layout;
    }

    private static StackPanel CreateLegendItem(string text, string color) {
        return new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = {
                new Border {
                    Width = 24,
                    Height = 3,
                    Background = new SolidColorBrush(Color.Parse(color)),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
            }
        };
    }

    private static Border CreateChartPanel(string title, HistogramChart chart) {
        Grid content = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8
        };
        content.Children.Add(new TextBlock {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Grid.SetRow(chart, 1);
        content.Children.Add(chart);

        TextBlock axisLabel = new TextBlock {
            Text = "Интенсивность",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(axisLabel, 2);
        content.Children.Add(axisLabel);
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = content
        };
    }

    private static long FindMaximum(
        ImageHistogramData histogramData
        ) {

        long maximumValue = 0;

        // ищем самый высокий пик среди всех трёх каналов
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
