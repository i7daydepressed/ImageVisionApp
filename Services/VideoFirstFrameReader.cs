using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ImageVisionApp.Services;

internal static class VideoFirstFrameReader {
    // готовим первый кадр для открытия документа
    public static byte[] ReadPng(string sourcePath) => ReadPreview(sourcePath, 0, 0).Png;

    // читает один кадр в точке перемотки и возвращает фактическое время этого кадра
    public static (byte[] Png, long Position) ReadPreview(string sourcePath, long position, long duration) {
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

        if (!Cv2.ImEncode(".png", frame, out byte[] previewPng) || previewPng.Length == 0) {
            throw new InvalidDataException("Не удалось подготовить кадр для показа");
        }

        double timestamp = capture.Get(VideoCaptureProperties.PosMsec);
        long framePosition = double.IsFinite(timestamp) && timestamp >= 0
            ? (long)Math.Round(timestamp) : position;
        return (previewPng, Math.Clamp(framePosition, 0, Math.Max(0, duration - 1)));
    }

    // выбирает последний действительный кадр для правой границы таймлайна
    private static bool TrySeekToLastFrame(VideoCapture capture) {
        double frames = capture.Get(VideoCaptureProperties.FrameCount);
        return frames >= 1 && capture.Set(VideoCaptureProperties.PosFrames, frames - 1);
    }
}
