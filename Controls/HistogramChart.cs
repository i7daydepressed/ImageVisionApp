using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed class HistogramChart : Control {
    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(24, 24, 24));
    private static readonly IPen BorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1);
    private static readonly IPen GridPen =
        new Pen(new SolidColorBrush(Color.FromArgb(90, 150, 150, 150)), 1);
    private static readonly IPen RedPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 70, 70)), 1.5);
    private static readonly IPen GreenPen =
        new Pen(new SolidColorBrush(Color.FromRgb(70, 220, 70)), 1.5);
    private static readonly IPen BluePen =
        new Pen(new SolidColorBrush(Color.FromRgb(70, 130, 255)), 1.5);
    private static readonly IBrush AxisTextBrush =
        new SolidColorBrush(Color.FromRgb(215, 215, 215));
    private static readonly Typeface AxisTypeface =
        new Typeface(
            FontFamily.Default,
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal);

    private const double AxisFontSize = 12;
    private const double AxisLabelSpacing = 6;
    private const double PlotTopPadding = 8;
    private const double PlotRightPadding = 6;
    private const double PlotBottomPadding = 24;

    private ImageHistogramData? histogramData;
    private long verticalMaximum = 1;

    public ImageHistogramData? HistogramData {
        get {
            return histogramData;
        }
        set {
            if (ReferenceEquals(histogramData, value)) {
                return;
            }

            histogramData = value;
            InvalidateVisual();
        }
    }

    public long VerticalMaximum {
        get {
            return verticalMaximum;
        }
        set {
            long normalizedValue = Math.Max(1, value);

            if (verticalMaximum == normalizedValue) {
                return;
            }

            verticalMaximum = normalizedValue;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context) {
        base.Render(context);

        Rect chartBounds = new Rect(Bounds.Size);

        context.DrawRectangle(BackgroundBrush, BorderPen, chartBounds);

        if (chartBounds.Width <= 2 ||
            chartBounds.Height <= 2) {
            return;
        }

        IReadOnlyList<long> yAxisTicks =
            CreateLogarithmicYAxisTicks();
        List<FormattedText> yAxisLabels = [];
        double maximumYAxisLabelWidth = 0;

        foreach (long yAxisTick in yAxisTicks) {
            FormattedText yAxisLabel = CreateAxisLabel(yAxisTick);

            yAxisLabels.Add(yAxisLabel);
            maximumYAxisLabelWidth = Math.Max(
                maximumYAxisLabelWidth,
                yAxisLabel.Width);
        }

        Rect plotBounds = new Rect(
            chartBounds.X + maximumYAxisLabelWidth +
                AxisLabelSpacing * 2,
            chartBounds.Y + PlotTopPadding,
            chartBounds.Width - maximumYAxisLabelWidth -
                AxisLabelSpacing * 3 - PlotRightPadding,
            chartBounds.Height - PlotTopPadding -
                PlotBottomPadding);

        if (plotBounds.Width <= 1 ||
            plotBounds.Height <= 1) {
            return;
        }

        DrawHorizontalGridAndYAxis(
            context,
            plotBounds,
            yAxisTicks,
            yAxisLabels);
        DrawXAxis(context, plotBounds);

        if (HistogramData is null) {
            context.DrawRectangle(null, BorderPen, plotBounds);
            return;
        }

        DrawChannel(
            context,
            HistogramData.RedValues,
            RedPen,
            plotBounds);
        DrawChannel(
            context,
            HistogramData.GreenValues,
            GreenPen,
            plotBounds);
        DrawChannel(
            context,
            HistogramData.BlueValues,
            BluePen,
            plotBounds);

        context.DrawRectangle(null, BorderPen, plotBounds);
    }

    private void DrawHorizontalGridAndYAxis(
        DrawingContext context,
        Rect plotBounds,
        IReadOnlyList<long> yAxisTicks,
        IReadOnlyList<FormattedText> yAxisLabels
        ) {

        for (int tickIndex = 0;
            tickIndex < yAxisTicks.Count;
            tickIndex++) {

            double normalizedValue = NormalizeCount(
                yAxisTicks[tickIndex]);
            double y = plotBounds.Bottom -
                normalizedValue * plotBounds.Height;
            FormattedText yAxisLabel = yAxisLabels[tickIndex];
            double labelY = Math.Clamp(
                y - yAxisLabel.Height / 2.0,
                0,
                Bounds.Height - yAxisLabel.Height);
            bool isExactNonPowerMaximum =
                yAxisTicks[tickIndex] == VerticalMaximum &&
                !IsPowerOfTen(VerticalMaximum);
            double labelX = isExactNonPowerMaximum
                ? plotBounds.Right - yAxisLabel.Width
                : plotBounds.Left - AxisLabelSpacing -
                    yAxisLabel.Width;

            context.DrawLine(
                GridPen,
                new Point(plotBounds.Left, y),
                new Point(plotBounds.Right, y));
            context.DrawText(
                yAxisLabel,
                new Point(labelX, labelY));
        }
    }

    private static void DrawXAxis(
        DrawingContext context,
        Rect plotBounds
        ) {

        int[] intensityTicks = [0, 64, 128, 192, 255];

        foreach (int intensityTick in intensityTicks) {
            FormattedText xAxisLabel = CreateAxisLabel(
                intensityTick);
            double x = plotBounds.Left +
                intensityTick / 255.0 * plotBounds.Width;
            double labelX = Math.Clamp(
                x - xAxisLabel.Width / 2.0,
                plotBounds.Left,
                plotBounds.Right - xAxisLabel.Width);

            context.DrawText(
                xAxisLabel,
                new Point(
                    labelX,
                    plotBounds.Bottom + 4));
        }
    }

    private void DrawChannel(
        DrawingContext context,
        IReadOnlyList<long> values,
        IPen pen,
        Rect plotBounds
        ) {

        double xStep = plotBounds.Width / 255.0;
        Point previousPoint = CreatePoint(
            0,
            values[0],
            xStep,
            plotBounds);

        for (int intensity = 1; intensity < 256; intensity++) {
            Point currentPoint = CreatePoint(
                intensity,
                values[intensity],
                xStep,
                plotBounds);

            context.DrawLine(
                pen,
                previousPoint,
                currentPoint);

            previousPoint = currentPoint;
        }
    }

    private Point CreatePoint(
        int intensity,
        long value,
        double xStep,
        Rect plotBounds
        ) {

        double x = plotBounds.Left + intensity * xStep;
        double normalizedValue = NormalizeCount(value);
        double y = plotBounds.Bottom -
            normalizedValue * plotBounds.Height;

        return new Point(x, y);
    }

    private double NormalizeCount(long count) {
        return Math.Log10(count + 1.0) /
            Math.Log10(VerticalMaximum + 1.0);
    }

    private IReadOnlyList<long> CreateLogarithmicYAxisTicks() {
        List<long> ticks = [0];
        long powerOfTen = 1;

        while (powerOfTen <= VerticalMaximum) {
            ticks.Add(powerOfTen);

            if (powerOfTen > long.MaxValue / 10) {
                break;
            }

            powerOfTen *= 10;
        }

        if (ticks[^1] != VerticalMaximum) {
            ticks.Add(VerticalMaximum);
        }

        return ticks;
    }

    private static FormattedText CreateAxisLabel(long value) {
        string formattedValue = value.ToString(
            "N0",
            CultureInfo.CurrentCulture);

        return new FormattedText(
            formattedValue,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            AxisFontSize,
            AxisTextBrush);
    }

    private static bool IsPowerOfTen(long value) {
        if (value < 1) {
            return false;
        }

        while (value % 10 == 0) {
            value /= 10;
        }

        return value == 1;
    }
}
