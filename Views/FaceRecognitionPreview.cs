using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal sealed class FaceRecognitionPreview : Control {
    private static readonly IPen FramePen = new Pen(Brushes.Gold, 2);
    private Bitmap? bitmap;
    private bool ownsBitmap;
    private FaceVideoResult? result;

    // заменяем пиксели и подписи вместе, сохраняя bitmap только текущего кадра
    public void ShowLive(FaceImage frame, FaceVideoResult recognized) => Show(CreateBitmap(frame), recognized, owns: true);

    // подготовленный кадр уже принадлежит общему плееру и совпадает с метаданными слоя
    public void ShowPrepared(Bitmap? original, FaceVideoResult? recognized = null) => Show(original, recognized, owns: false);

    // публикуем изображение, подписи и доступное имя в одном вызове
    private void Show(Bitmap? next, FaceVideoResult? recognized, bool owns) {
        Bitmap? previous = ownsBitmap ? bitmap : null;
        bitmap = next;
        ownsBitmap = owns;
        result = recognized;
        string name = recognized is { Faces.Count: > 0 } ? "Кадр распознавания. " +
            string.Join(", ", recognized.Faces.Select(face => LabelFor(face, recognized.EmptyCatalogue))) : "Кадр распознавания";
        AutomationProperties.SetName(this, name);
        InvalidateVisual();
        previous?.Dispose();
    }

    // рисуем рамки поверх того же вписанного прямоугольника, в котором видны пиксели
    public override void Render(DrawingContext context) {
        base.Render(context);
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) {
            return;
        }

        PixelSize size = bitmap.PixelSize;
        Rect content = FaceImportPreview.Fit(Bounds.Size, size.Width, size.Height);
        context.DrawImage(bitmap, new Rect(bitmap.Size), content);
        using (context.PushClip(content)) {
            if (result is null) {
                return;
            }
            foreach (RecognizedFace face in result.Faces) {
                Rect bounds = FaceImportPreview.MapBounds(content, size.Width, size.Height, face.Bounds);
                context.DrawRectangle(null, FramePen, bounds);
            }

            List<Rect> plaques = new(result.Faces.Count);
            foreach (RecognizedFace face in result.Faces) {
                Rect bounds = FaceImportPreview.MapBounds(content, size.Width, size.Height, face.Bounds);
                string name = LabelFor(face, result.EmptyCatalogue);
                FormattedText label = new(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 14, Brushes.Black);
                Rect plaque = PlaceLabel(content, bounds, new Size(label.Width + 8, label.Height + 4), plaques);
                plaques.Add(plaque);
                context.FillRectangle(Brushes.Gold, plaque);
                context.DrawText(label, new Point(plaque.X + 4, plaque.Y + 2));
            }
        }
    }

    // ставим подпись над лицом, а при пересечении ищем свободное место по вертикали
    private static Rect PlaceLabel(Rect content, Rect face, Size size, IReadOnlyList<Rect> plaques) {
        double width = Math.Min(size.Width, content.Width);
        double height = Math.Min(size.Height, content.Height);
        double left = Math.Clamp(face.Left, content.Left, Math.Max(content.Left, content.Right - width));
        double top = Math.Clamp(face.Top - height, content.Top, Math.Max(content.Top, content.Bottom - height));
        Rect preferred = new(left, top, width, height);
        Rect? placed = ShiftLabel(content, preferred, plaques, upward: true) ??
            ShiftLabel(content, preferred, plaques, upward: false);
        if (placed is { } free) {
            return free;
        }

        // если вертикаль занята, пробуем край с большим горизонтальным сдвигом
        double rightmost = Math.Max(content.Left, content.Right - width);
        double opposite = left - content.Left < rightmost - left ? rightmost : content.Left;
        Rect fallback = new(opposite, top, width, height);
        return ShiftLabel(content, fallback, plaques, upward: true) ??
            ShiftLabel(content, fallback, plaques, upward: false) ?? preferred;
    }

    // перескакиваем за каждый мешающий прямоугольник, пока не выйдем за пределы кадра
    private static Rect? ShiftLabel(Rect content, Rect candidate, IReadOnlyList<Rect> plaques, bool upward) {
        const double gap = 2;
        double lastTop = Math.Max(content.Top, content.Bottom - candidate.Height);
        while (candidate.Top >= content.Top && candidate.Top <= lastTop) {
            bool shifted = false;
            foreach (Rect used in plaques) {
                if (candidate.Intersects(used)) {
                    double top = upward ? used.Top - candidate.Height - gap : used.Bottom + gap;
                    candidate = new Rect(candidate.X, top, candidate.Width, candidate.Height);
                    shifted = true;
                    break;
                }
            }
            if (!shifted) {
                return candidate;
            }
        }
        return null;
    }

    // используем один и тот же текст над рамкой и в доступном имени кадра
    private static string LabelFor(RecognizedFace face, bool emptyCatalogue) =>
        face.Match.Profile?.Name ?? (emptyCatalogue ? "База пуста" : "Неизвестное лицо");

    // передаем исходный BGR8 в непрозрачный bitmap без промежуточного кодирования
    private static WriteableBitmap CreateBitmap(FaceImage frame) {
        WriteableBitmap result = new(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try {
            using ILockedFramebuffer target = result.Lock();
            ReadOnlySpan<byte> pixels = frame.Bgr8;
            byte[] row = new byte[checked(frame.Width * 4)];
            int input = 0;
            for (int y = 0; y < frame.Height; y++) {
                for (int x = 0; x < row.Length; x += 4, input += 3) {
                    row[x] = pixels[input];
                    row[x + 1] = pixels[input + 1];
                    row[x + 2] = pixels[input + 2];
                    row[x + 3] = 255;
                }
                Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
            }
            return result;
        }
        catch {
            result.Dispose();
            throw;
        }
    }
}
