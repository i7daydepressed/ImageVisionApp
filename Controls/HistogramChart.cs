using System;
using System.Collections.Generic;
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

        context.DrawRectangle(
            BackgroundBrush,
            BorderPen,
            chartBounds);

        if (chartBounds.Width <= 2 ||
            chartBounds.Height <= 2) {
            return;
        }

        Rect plotBounds = new Rect(
            chartBounds.X + 1,
            chartBounds.Y + 1,
            chartBounds.Width - 2,
            chartBounds.Height - 2);

        DrawHorizontalGrid(context, plotBounds);

        if (HistogramData is null) {
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
    }

    private static void DrawHorizontalGrid(
        DrawingContext context,
        Rect plotBounds
        ) {

        for (int lineIndex = 1; lineIndex < 4; lineIndex++) {
            double y = plotBounds.Top +
                plotBounds.Height * lineIndex / 4.0;

            context.DrawLine(
                GridPen,
                new Point(plotBounds.Left, y),
                new Point(plotBounds.Right, y));
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
        double normalizedValue =
            Math.Min(value, VerticalMaximum) /
            (double)VerticalMaximum;
        double y = plotBounds.Bottom -
            normalizedValue * plotBounds.Height;

        return new Point(x, y);
    }
}
