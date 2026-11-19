using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageVisionApp.Services;

// передаёт кадры из потока декодера в UI, удерживая только последний готовый кадр
internal sealed class VideoPlaybackFrames : IDisposable {
    private const int Alignment = 32;
    private readonly object frameLock = new();
    private readonly PixelSize visibleSize;
    private IntPtr allocation;
    private IntPtr alignedPixels;
    private byte[] pixels = [];
    private int width;
    private int height;
    private int stride;
    private long revision;
    private bool hasFrame;

    // получает видимые размеры из кадра, уже подготовленного при открытии файла
    public VideoPlaybackFrames(PixelSize visibleSize) => this.visibleSize = visibleSize;

    // договаривается о BGRA и ограничивает размер буфера предпросмотра
    public uint Configure(ref IntPtr opaque, IntPtr chroma, ref uint pixelWidth, ref uint pixelHeight,
        ref uint pitch, ref uint lines) {
        if (pixelWidth == 0 || pixelHeight == 0) {
            return 0;
        }

        lock (frameLock) {
            // VLC может передать закодированные размеры с добавочными строками за краем изображения.
            PixelSize previewSize = VideoPreviewDimensions.Fit(visibleSize);
            width = previewSize.Width;
            height = previewSize.Height;
            stride = Align(width * 4);
            int scanlines = Align(height);
            try {
                pixels = new byte[stride * height];
                allocation = Marshal.AllocHGlobal(stride * scanlines + Alignment - 1);
            }
            catch (OutOfMemoryException) {
                return 0;
            }

            alignedPixels = new IntPtr((allocation.ToInt64() + Alignment - 1) & ~(Alignment - 1L));
            Marshal.WriteInt32(chroma, 0x32335652); // RV32, упакованные BGR-пиксели с неиспользуемым альфа-байтом
            pixelWidth = (uint)width;
            pixelHeight = (uint)height;
            pitch = (uint)stride;
            lines = (uint)scanlines;
            hasFrame = false;
            return 1;
        }
    }

    // округляет шаг строки и объём буфера до нативной границы
    private static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);

    // отдаёт декодеру выровненный буфер одного кадра
    public IntPtr Lock(IntPtr opaque, IntPtr planes) {
        Marshal.WriteIntPtr(planes, alignedPixels);
        return alignedPixels;
    }

    // копирует кадр по сигналу вывода VLC, после записи всех строк декодером
    public void Display(IntPtr opaque, IntPtr picture) {
        lock (frameLock) {
            if (picture == IntPtr.Zero || alignedPixels == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(picture, pixels, 0, pixels.Length);
            revision++;
            hasFrame = true;
        }
    }

    // переносит актуальный кадр в изображение UI с учётом шага строк
    public bool CopyTo(ref WriteableBitmap? bitmap, ref long shownRevision) {
        lock (frameLock) {
            if (!hasFrame || revision == shownRevision) {
                return false;
            }

            PixelSize size = new(width, height);
            if (bitmap is null || bitmap.PixelSize != size) {
                bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            using ILockedFramebuffer target = bitmap.Lock();
            if (target.RowBytes == stride) {
                Marshal.Copy(pixels, 0, target.Address, stride * height);
            }
            else {
                for (int row = 0; row < height; row++) {
                    Marshal.Copy(pixels, row * stride, target.Address + row * target.RowBytes, width * 4);
                }
            }
            shownRevision = revision;
            return true;
        }
    }

    // освобождает пулы вывода, когда декодер прекращает использовать данный формат
    public void Release(ref IntPtr opaque) {
        lock (frameLock) {
            Marshal.FreeHGlobal(allocation);
            allocation = IntPtr.Zero;
            alignedPixels = IntPtr.Zero;
        }
    }

    // освобождает нативную память после остановки всех потоков декодера
    public void Dispose() {
        IntPtr unused = IntPtr.Zero;
        Release(ref unused);
    }
}
