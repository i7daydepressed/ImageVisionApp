using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;
using OpenCvSharp;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;
using Point = Avalonia.Point;

namespace ImageVisionApp.Views;

internal sealed class FaceImportPreview : Control {
    private static readonly IPen FacePen = new Pen(Brushes.Gold, 2);
    private static readonly IPen SelectedPen = new Pen(Brushes.DeepSkyBlue, 3);
    private FaceImportItem? photo;
    private Bitmap? bitmap;

    public event Action<int>? FaceChosen;

    // храним bitmap только выбранного фото и перерисовываем рамку при смене номера
    public void ShowPhoto(FaceImportItem? item) {
        if (!ReferenceEquals(photo, item)) {
            bitmap?.Dispose();
            bitmap = null;
            photo = item;
            if (photo?.PreviewPng is byte[] png) {
                using MemoryStream stream = new(png);
                bitmap = new Bitmap(stream);
            }
        }
        InvalidateVisual();
    }

    // используем одну область вписанного фото для изображения, рамок и номеров
    public override void Render(DrawingContext context) {
        base.Render(context);
        if (bitmap is null || photo?.Detection is not { } detection) {
            return;
        }
        Rect image = Fit(Bounds.Size, detection.Source);
        if (image.Width <= 0 || image.Height <= 0) {
            return;
        }

        context.DrawImage(bitmap, new Rect(bitmap.Size), image);
        using (context.PushClip(image)) {
            for (int index = 0; index < detection.Faces.Count; index++) {
                Rect frame = MapBounds(image, detection.Source, detection.Faces[index].Bounds);
                bool selected = photo.SelectedIndex == index;
                context.DrawRectangle(null, selected ? SelectedPen : FacePen, frame);
                FormattedText label = new((index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 15, Brushes.Black);
                Point origin = frame.TopLeft + new Vector(2, 2);
                context.FillRectangle(selected ? Brushes.DeepSkyBlue : Brushes.Gold,
                    new Rect(origin, new Size(label.Width + 10, label.Height + 4)));
                context.DrawText(label, origin + new Vector(5, 2));
            }
        }
    }

    // выбираем наиболее тесную рамку в точке клика, если области перекрываются
    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || photo?.Detection is not { } detection) {
            return;
        }
        Point position = e.GetPosition(this);
        Rect image = Fit(Bounds.Size, detection.Source);
        int index = FindFaceAt(position, image, detection);
        if (index >= 0) {
            FaceChosen?.Invoke(index);
            e.Handled = true;
        }
    }

    // поле вне фото не выбирает лицо, при пересечении берем меньшую рамку
    internal static int FindFaceAt(Point position, Rect image, FaceDetectionResult detection) {
        if (!image.Contains(position)) {
            return -1;
        }
        int bestIndex = -1;
        double bestArea = double.MaxValue;
        for (int index = 0; index < detection.Faces.Count; index++) {
            Rect frame = MapBounds(image, detection.Source, detection.Faces[index].Bounds);
            double area = frame.Width * frame.Height;
            if (frame.Contains(position) && area < bestArea) {
                bestIndex = index;
                bestArea = area;
            }
        }
        return bestIndex;
    }

    // считаем одинаковое поле с обеих сторон ориентированного изображения
    internal static Rect Fit(Size viewport, FaceImage source) => Fit(viewport, source.Width, source.Height);

    // принимаем размеры общего bitmap без копирования его пикселей в модель фото
    internal static Rect Fit(Size viewport, int sourceWidth, int sourceHeight) {
        double scale = Math.Min(viewport.Width / sourceWidth, viewport.Height / sourceHeight);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        return new Rect((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    // переносим рамку из координат фото в тот же прямоугольник, где оно показано
    internal static Rect MapBounds(Rect image, FaceImage source, FaceBounds bounds) =>
        MapBounds(image, source.Width, source.Height, bounds);

    // используем ту же геометрию для исходного размера видео
    internal static Rect MapBounds(Rect image, int sourceWidth, int sourceHeight, FaceBounds bounds) {
        double scaleX = image.Width / sourceWidth;
        double scaleY = image.Height / sourceHeight;
        return new Rect(image.X + bounds.Left * scaleX, image.Y + bounds.Top * scaleY,
            (bounds.Right - bounds.Left) * scaleX, (bounds.Bottom - bounds.Top) * scaleY);
    }

    // сжимаем только экранную копию, признаки остаются связаны с исходными BGR8
    internal static byte[] EncodePreview(FaceImage source, CancellationToken token) {
        const int longestSide = 1200;
        token.ThrowIfCancellationRequested();
        using Mat pixels = source.BorrowBgr8();
        using Mat reduced = new();
        double scale = (double)longestSide / Math.Max(source.Width, source.Height);
        if (scale < 1) {
            var size = new OpenCvSharp.Size(Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale)));
            Cv2.Resize(pixels, reduced, size, interpolation: InterpolationFlags.Area);
        }
        if (!Cv2.ImEncode(".png", scale < 1 ? reduced : pixels, out byte[] data)) {
            throw new InvalidDataException("Не удалось подготовить предпросмотр фото.");
        }
        token.ThrowIfCancellationRequested();
        return data;
    }
}
