using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using ImageVisionApp.Models;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ImageVisionApp.Services;

internal sealed class FacePipelineService : IDisposable {
    public const string PipelineVersion = "yunet-2023mar.sface-2021dec.bgr8-black-letterbox640-align112-l2.v1";

    private const int DetectionSize = 640;
    private const int AlignedSize = 112;
    private const int EmbeddingLength = 128;
    private const string YuNetHash = "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4";
    private const string SFaceHash = "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79";

    private readonly object gate = new();
    private readonly object sessionKey = new();
    private readonly FaceDetectorYN detector;
    private readonly Net recognizer;
    private bool disposed;

    // проверяем заданные файлы, затем поднимаем одну пару моделей на CPU
    public FacePipelineService(string yuNetPath, string sFacePath) {
        byte[] yuNet = ReadVerifiedModel("YuNet", yuNetPath, 232589, YuNetHash);
        byte[] sFace = ReadVerifiedModel("SFace", sFacePath, 38696353, SFaceHash);

        FaceDetectorYN? loadedDetector = null;
        Net? loadedRecognizer = null;
        string loadingModel = "YuNet";
        try {
            loadedDetector = LoadDetector(yuNet);
            loadingModel = "SFace";
            loadedRecognizer = Net.ReadNetFromONNX(sFace.AsSpan())
                ?? throw new InvalidDataException("OpenCV вернул пустую модель SFace.");
            loadedRecognizer.SetPreferableBackend(Backend.OPENCV);
            loadedRecognizer.SetPreferableTarget(Target.CPU);
            detector = loadedDetector;
            recognizer = loadedRecognizer;
        }
        catch (Exception error) {
            try {
                loadedRecognizer?.Dispose();
            }
            finally {
                loadedDetector?.Dispose();
            }
            throw new InvalidOperationException($"Не удалось загрузить модель {loadingModel} на CPU.", error);
        }
    }

    // выдаем рамки и точки без SFace, сохраняя точный вход для последующего выбора
    public FaceDetectionResult Detect(FaceImage source, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            FaceDetectionResult result = DetectCore(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    // берем точки и BGR8 из одного результата детекции, индекс начинается с нуля
    public FaceEmbedding ExtractEmbedding(FaceDetectionResult detection, int index,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(detection);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            FaceDetection face = detection.Select(sessionKey, index);
            using Mat input = detection.Source.BorrowBgr8();
            using Mat transform = CreateAlignmentTransform(face.Landmarks);
            using Mat aligned = new();
            Size alignedSize = new(AlignedSize, AlignedSize);
            Cv2.WarpAffine(input, aligned, transform, alignedSize, InterpolationFlags.Linear);
            cancellationToken.ThrowIfCancellationRequested();

            // SFace принимает RGB с множителем 1, нулевым средним и без обрезки
            using Mat blob = CvDnn.BlobFromImage(aligned, 1, alignedSize, Scalar.All(0), swapRB: true, crop: false);
            recognizer.SetInput(blob);
            using Mat output = recognizer.Forward();
            cancellationToken.ThrowIfCancellationRequested();

            float[] embedding = NormalizeEmbedding(output);
            byte[] pixels = new byte[FaceImage.GetBgr8Length(AlignedSize, AlignedSize)];
            Marshal.Copy(aligned.Data, pixels, 0, pixels.Length);
            var result = new FaceEmbedding(PipelineVersion, FaceImage.TakeBgr8(AlignedSize, AlignedSize, pixels), embedding);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    // освобождаем обе модели после окончания текущего вызова OpenCV
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }
            disposed = true;
            try {
                recognizer.Dispose();
            }
            finally {
                detector.Dispose();
            }
        }
    }

    // вписываем BGR в квадрат и переносим выход YuNet в координаты снимка
    private FaceDetectionResult DetectCore(FaceImage source, CancellationToken cancellationToken) {
        double fit = Math.Min((double)DetectionSize / source.Width, (double)DetectionSize / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * fit));
        int height = Math.Max(1, (int)Math.Round(source.Height * fit));
        int padX = (DetectionSize - width) / 2;
        int padY = (DetectionSize - height) / 2;
        double scaleX = (double)width / source.Width;
        double scaleY = (double)height / source.Height;
        using Mat input = source.BorrowBgr8();
        using Mat letterbox = new(DetectionSize, DetectionSize, MatType.CV_8UC3, Scalar.All(0));
        using Mat region = new(letterbox, new Rect(padX, padY, width, height));
        Cv2.Resize(input, region, new Size(width, height), interpolation: InterpolationFlags.Linear);
        cancellationToken.ThrowIfCancellationRequested();

        using Mat rows = new();
        detector.Detect(letterbox, rows);
        cancellationToken.ThrowIfCancellationRequested();

        int count = rows.Rows;
        var detections = new List<(FaceDetection Face, float CenterX, float CenterY)>(count);
        for (int index = 0; index < count; index++) {
            FacePoint start = MapPoint(index, 0);
            float boxWidth = (float)(rows.At<float>(index, 2) / scaleX);
            float boxHeight = (float)(rows.At<float>(index, 3) / scaleY);
            FaceBounds bounds = new(Math.Clamp(start.X, 0, source.Width), Math.Clamp(start.Y, 0, source.Height),
                Math.Clamp(start.X + boxWidth, 0, source.Width), Math.Clamp(start.Y + boxHeight, 0, source.Height));

            // рамки, попавшие целиком на поля, не относятся к пикселям снимка
            if (!(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)) {
                continue;
            }
            FaceLandmarks landmarks = new(MapPoint(index, 4), MapPoint(index, 6), MapPoint(index, 8),
                MapPoint(index, 10), MapPoint(index, 12));
            FaceDetection face = new(bounds, landmarks, rows.At<float>(index, 14));
            detections.Add((face, start.X + boxWidth / 2, start.Y + boxHeight / 2));
        }

        FaceDetection[] ordered = detections.OrderBy(item => item.CenterX).ThenBy(item => item.CenterY)
            .Select(item => item.Face).ToArray();
        return new FaceDetectionResult(sessionKey, source, ordered);

        // используем масштабы после округления и оставляем точки без обрезки
        FacePoint MapPoint(int row, int column) => new(
            (float)((rows.At<float>(row, column) - padX) / scaleX),
            (float)((rows.At<float>(row, column + 1) - padY) / scaleY));
    }

    // ищем поворот, общий масштаб и перенос пяти точек к шаблону SFace 112×112
    private static Mat CreateAlignmentTransform(FaceLandmarks landmarks) {
        ReadOnlySpan<FacePoint> source = [
            landmarks.RightEye, landmarks.LeftEye, landmarks.NoseTip, landmarks.RightMouthCorner, landmarks.LeftMouthCorner
        ];
        // порядок и центр шаблона совпадают с FaceRecognizerSF из OpenCV 4.13
        ReadOnlySpan<FacePoint> target = [
            new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f),
            new(41.5493f, 92.3655f), new(70.7299f, 92.2041f)
        ];
        const float targetX = 56.0262f;
        const float targetY = 71.9008f;
        float sourceX = 0, sourceY = 0;
        foreach (FacePoint point in source) {
            sourceX += point.X;
            sourceY += point.Y;
        }
        sourceX /= source.Length;
        sourceY /= source.Length;

        double a = 0, b = 0, spreadX = 0, spreadY = 0, crossSpread = 0;
        // по центрированным точкам накапливаем числители масштаба с поворотом и проверяем двумерный разброс
        for (int i = 0; i < source.Length; i++) {
            float sx = source[i].X - sourceX;
            float sy = source[i].Y - sourceY;
            float tx = target[i].X - targetX;
            float ty = target[i].Y - targetY;
            a += (double)tx * sx + (double)ty * sy;
            b += (double)ty * sx - (double)tx * sy;
            spreadX += (double)sx * sx;
            spreadY += (double)sy * sy;
            crossSpread += (double)sx * sy;
        }
        double variance = spreadX + spreadY;
        if (!(spreadX * spreadY - crossSpread * crossSpread > 1e-12 * variance * variance)
            || !(a * a + b * b > 0)) {
            throw new InvalidDataException("Ориентиры лица вырождены. Выровнять фрагмент невозможно.");
        }
        a /= variance;
        b /= variance;
        return Mat.FromArray(new double[,] {
            { a, -b, targetX - a * sourceX + b * sourceY },
            { b, a, targetY - b * sourceX - a * sourceY }
        });
    }

    // проверяем, что SFace выдал один конечный вектор, и приводим его L2-норму к единице
    private static float[] NormalizeEmbedding(Mat output) {
        if (output.Empty() || output.Type() != MatType.CV_32FC1 || output.Total() != EmbeddingLength) {
            throw new InvalidDataException("SFace должен вернуть 128 чисел float.");
        }
        using Mat row = output.Reshape(1, 1);
        float[] values = new float[EmbeddingLength];
        double sumSquares = 0;
        for (int i = 0; i < values.Length; i++) {
            float value = row.At<float>(0, i);
            values[i] = value;
            sumSquares += (double)value * value;
        }
        double norm = Math.Sqrt(sumSquares);
        if (!double.IsFinite(norm) || norm <= 0) {
            throw new InvalidDataException("SFace вернул нулевой или неконечный вектор.");
        }
        for (int i = 0; i < values.Length; i++) {
            values[i] = (float)(values[i] / norm);
        }
        return values;
    }

    // передаем проверенные байты YuNet через временный ONNX для API, которое принимает путь
    private static FaceDetectorYN LoadDetector(byte[] verifiedModel) {
        string directory = Directory.CreateTempSubdirectory("ImageVisionApp-YuNet-").FullName;
        FaceDetectorYN? model = null;
        try {
            string snapshot = Path.Combine(directory, "yunet.onnx");
            File.WriteAllBytes(snapshot, verifiedModel);
            model = FaceDetectorYN.Create(snapshot, "",
                new Size(DetectionSize, DetectionSize), 0.75f, 0.3f, 5000, Backend.OPENCV, Target.CPU);
        }
        finally {
            try {
                Directory.Delete(directory, recursive: true);
            }
            catch {
                model?.Dispose();
                throw;
            }
        }
        return model;
    }

    // проверяем размер и SHA-256 тех же байтов, которые получит OpenCV
    private static byte[] ReadVerifiedModel(string name, string path, int bytes, string sha256) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        try {
            using FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != bytes) {
                throw new InvalidDataException($"Размер модели {name} не совпадает с проверенным ONNX: {fullPath}");
            }
            byte[] data = new byte[bytes];
            stream.ReadExactly(data);
            if (stream.ReadByte() != -1 ||
                !Convert.ToHexString(SHA256.HashData(data)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"Размер или SHA-256 модели {name} не совпадает с проверенным ONNX: {fullPath}");
            }
            return data;
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) {
            throw new FileNotFoundException($"Файл модели {name} отсутствует: {fullPath}", fullPath, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw new IOException($"Не удалось открыть файл модели {name}: {fullPath}", error);
        }
    }
}
