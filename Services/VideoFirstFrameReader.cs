using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using OpenCvSharp;

namespace ImageVisionApp.Services;

internal static class VideoFirstFrameReader {
    // готовим первый кадр для открытия документа
    public static byte[] ReadPng(string sourcePath) => ReadPreview(sourcePath, 0, 0).Png;

    // читает один кадр в точке перемотки и возвращает фактическое время этого кадра
    public static (byte[] Png, long Position) ReadPreview(string sourcePath, long position, long duration) {
        var frame = ReadPreviewWithHint(sourcePath, position, duration, captureHint: false);
        return (frame.Png, frame.Position);
    }

    // сохраняем неокругленное время и номер декодера для последующей проверки по шкале контейнера
    public static (byte[] Png, long Position, VideoDecodedFrameHint? Hint) ReadPreviewWithHint(
        string sourcePath, long position, long duration, bool captureHint = true) {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            throw new PlatformNotSupportedException("Открытие видео в этой сборке доступно на macOS arm64");
        }

        using VideoCapture capture = new();
        if (!capture.Open(sourcePath, VideoCaptureAPIs.FFMPEG) || !capture.IsOpened()) {
            throw new InvalidDataException("Не удалось открыть видеопоток");
        }

        bool atEnd = duration > 0 && position >= duration;
        if (position > 0 && !(atEnd ? TrySeekToLastFrame(capture) : capture.Set(VideoCaptureProperties.PosMsec, position))) {
            throw new InvalidDataException("Не удалось перейти к запрошенному моменту");
        }

        using Mat frame = new();
        if (!capture.Read(frame) || frame.Empty()) {
            // запрос между последним кадром и концом иногда округляется за границу потока
            double fps = capture.Get(VideoCaptureProperties.Fps);
            if (position <= 0 || atEnd || !(fps > 0) || duration - position > Math.Ceiling(1000 / fps) ||
                !TrySeekToLastFrame(capture) || !capture.Read(frame) || frame.Empty()) {
                throw new InvalidDataException("Не удалось прочитать кадр видео");
            }
        }

        PixelSize previewSize = VideoPreviewDimensions.Fit(new PixelSize(frame.Width, frame.Height));
        using Mat? reduced = previewSize.Width == frame.Width && previewSize.Height == frame.Height ? null : new Mat();
        if (reduced is not null) {
            Cv2.Resize(frame, reduced, new OpenCvSharp.Size(previewSize.Width, previewSize.Height),
                interpolation: InterpolationFlags.Area);
        }

        if (!Cv2.ImEncode(".png", reduced ?? frame, out byte[] previewPng) || previewPng.Length == 0) {
            throw new InvalidDataException("Не удалось подготовить кадр для показа");
        }

        double timestamp = capture.Get(VideoCaptureProperties.PosMsec);
        long framePosition = double.IsFinite(timestamp) && timestamp >= 0
            ? (long)Math.Round(timestamp) : position;
        double decodedNumber = capture.Get(VideoCaptureProperties.PosFrames) - 1;
        VideoDecodedFrameHint? hint = captureHint && double.IsFinite(timestamp) && timestamp >= 0 &&
            decodedNumber >= 0 && decodedNumber < long.MaxValue && decodedNumber == Math.Truncate(decodedNumber)
            ? new(timestamp, (long)decodedNumber, FromStart: position == 0, Fingerprint: GetBgrFingerprint(reduced ?? frame))
            : null;
        return (previewPng, Math.Clamp(framePosition, 0, Math.Max(0, duration - 1)), hint);
    }

    // проверяем те же цветовые пиксели, которые передаются в PNG, с одной временной строкой
    internal static string GetBgrFingerprint(Mat frame) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] row = new byte[checked(frame.Width * 3)];
        int stride = checked((int)frame.Step());
        int height = frame.Height;
        IntPtr pixels = frame.Data;
        for (int y = 0; y < height; y++) {
            Marshal.Copy(IntPtr.Add(pixels, checked(y * stride)), row, 0, row.Length);
            hash.AppendData(row);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // выбирает последний действительный кадр для правой границы таймлайна
    private static bool TrySeekToLastFrame(VideoCapture capture) {
        double frames = capture.Get(VideoCaptureProperties.FrameCount);
        return frames >= 1 && capture.Set(VideoCaptureProperties.PosFrames, frames - 1);
    }
}
