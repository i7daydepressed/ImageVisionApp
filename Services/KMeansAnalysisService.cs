using System;
using System.Runtime.InteropServices;
using System.Threading;
using ImageVisionApp.Models;
using OpenCvSharp;

namespace ImageVisionApp.Services;

public static class KMeansAnalysisService {
    public static AnalysisSegmentationResult Analyze(
        AnalysisInputSnapshot input, int clusterCount, CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(input);
        ValidateClusterCount(clusterCount);
        (int width, int height, byte[] rgb) = AnalysisImageDecoder.Decode(input, cancellationToken);
        (byte[] segmented, int[] labels, float[] centers, double compactness) =
            CalculateFromRgb(rgb, width, height, clusterCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisSegmentationResult(input, width, height, clusterCount,
            rgb, segmented, labels, centers, compactness);
    }

    // берем RGB полного входа: каждый пиксель становится строкой из трех float в диапазоне 0..255
    // native K-means объединяет похожие цвета, а мы заменяем пиксели округленными центрами их групп
    // метки сохраняем отдельно от цвета; одинаковые цвета могут занять несколько групп при большом N
    public static (byte[] Rgb, int[] Labels, float[] CentersRgb, double Compactness) CalculateFromRgb(
        byte[] rgb, int width, int height, int clusterCount, CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0 || rgb.Length != checked(width * height * 3)) {
            throw new ArgumentException("Размер RGB-входа не совпадает с шириной и высотой");
        }
        ValidateClusterCount(clusterCount);
        int pixels = checked(width * height);
        if (pixels < clusterCount) {
            throw new ArgumentException($"Число пикселей входа: {pixels}; для N={clusterCount} их недостаточно");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            throw new PlatformNotSupportedException("K-means этой версии рассчитан на macOS arm64");
        }

        using Mat samples = new Mat(pixels, 3, MatType.CV_32FC1);
        if (!samples.IsContinuous()) {
            throw new InvalidOperationException("Матрица признаков должна иметь непрерывный буфер");
        }
        float[] row = new float[checked(width * 3)];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = y * row.Length;
            for (int channel = 0; channel < row.Length; channel++) {
                row[channel] = rgb[offset + channel];
            }
            // строка картинки занимает width последовательных строк матрицы P на 3
            Marshal.Copy(row, 0, samples.Ptr(y * width), row.Length);
        }
        using Mat nativeLabels = new Mat();
        using Mat nativeCenters = new Mat();
        cancellationToken.ThrowIfCancellationRequested();
        // генератор OpenCV привязан к потоку, seed задается здесь же перед native-вызовом
        _ = Cv2.SetTheRNG(123456UL);
        // REVIEW-SOURCE: OpenCV 4.13 :: kmeans — инициализация K-means++ и критерии остановки: <добавить при ревью>
        double compactness = Cv2.Kmeans(samples, clusterCount, nativeLabels,
            new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 100, 0.1),
            1, KMeansFlags.PpCenters, nativeCenters);
        cancellationToken.ThrowIfCancellationRequested();

        int labelRows = nativeLabels.Rows;
        int labelColumns = nativeLabels.Cols;
        int centerRows = nativeCenters.Rows;
        int centerColumns = nativeCenters.Cols;
        if (labelRows != pixels || labelColumns != 1 || nativeLabels.Type() != MatType.CV_32SC1 ||
            centerRows != clusterCount || centerColumns != 3 || nativeCenters.Type() != MatType.CV_32FC1 ||
            !double.IsFinite(compactness) || compactness < 0) {
            throw new InvalidOperationException("K-means вернул данные неожиданной формы или типа");
        }
        int[] labels = new int[pixels];
        if (nativeLabels.IsContinuous()) {
            Marshal.Copy(nativeLabels.Data, labels, 0, pixels);
        }
        else {
            for (int pixel = 0; pixel < pixels; pixel++) {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(nativeLabels.Ptr(pixel), labels, pixel, 1);
            }
        }
        float[] centers = new float[clusterCount * 3];
        for (int center = 0; center < centerRows; center++) {
            Marshal.Copy(nativeCenters.Ptr(center), centers, center * 3, 3);
        }
        for (int channel = 0; channel < centers.Length; channel++) {
            if (!float.IsFinite(centers[channel])) {
                throw new InvalidOperationException("K-means вернул неконечный цвет центра");
            }
        }
        byte[] result = new byte[rgb.Length];
        for (int y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int pixel = y * width + x;
                int label = labels[pixel];
                if (label < 0 || label >= clusterCount) {
                    throw new InvalidOperationException("Номер кластера за пределами выбранного N");
                }
                for (int channel = 0; channel < 3; channel++) {
                    // половины округляются вверх, например 10 и 11 дают центр 10.5 и канал 11
                    result[pixel * 3 + channel] = (byte)Math.Clamp(
                        (int)Math.Floor(centers[label * 3 + channel] + 0.5), 0, 255);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (result, labels, centers, compactness);
    }

    private static void ValidateClusterCount(int clusterCount) {
        if (clusterCount < 2 || clusterCount > 20) {
            throw new ArgumentOutOfRangeException(nameof(clusterCount), "N должно быть целым числом от 2 до 20");
        }
    }
}
