using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoFrameAnalysisSettings(AnalysisEdgeMethod? EdgeMethod, double Lower, double Upper,
    int? Clusters, int? FastThreshold, bool FastNonmaxSuppression) {
    public bool Plate { get; init; }
}

internal sealed record VideoFrameAnalysisResult(AnalysisEdgeResult? Edges, AnalysisSegmentationResult? Segmentation,
    AnalysisKeypointResult? Keypoints) {
    public int PeakWorkers { get; init; }
    public TimeSpan CalculationTime { get; init; }
    public AnalysisPlateResult? Plate { get; init; }
    public string? PlateError { get; init; }
}

internal static class VideoFrameAnalysisService {
    // учитываем снимки, общие каналы, выходные карты и одновременно занятые рабочие буферы
    public static (int Workers, long Bytes) FitWorkers(PixelSize original, PixelSize? edited,
        AnalysisImageSource edgeSource, AnalysisImageSource segmentationSource, VideoFrameAnalysisSettings settings,
        int limit, long budget) {
        long sourcePixels = (long)original.Width * original.Height;
        long editedPixels = edited is { } size ? (long)size.Width * size.Height : 0;
        long baseBytes = 4 * (sourcePixels + editedPixels);
        bool originalEdges = settings.EdgeMethod is not null && edgeSource == AnalysisImageSource.Original;
        bool originalSegmentation = settings.Clusters is not null && segmentationSource == AnalysisImageSource.Original;
        bool fast = settings.FastThreshold is not null;
        if (originalEdges || originalSegmentation || fast || settings.Plate) {
            baseBytes += 3 * sourcePixels;
        }
        if (editedPixels > 0) {
            baseBytes += 3 * editedPixels;
        }
        if (originalEdges || fast || settings.Plate) {
            baseBytes += sourcePixels;
        }
        if (settings.EdgeMethod is not null && !originalEdges) {
            baseBytes += editedPixels;
        }

        Span<long> scratch = stackalloc long[4];
        int count = 0;
        if (settings.EdgeMethod is { } method) {
            long pixels = originalEdges ? sourcePixels : editedPixels;
            baseBytes += pixels;
            // Canny создает три явные одноканальные Mat, у Робертса общая яркость уже учтена
            scratch[count++] = method == AnalysisEdgeMethod.Canny ? 3 * pixels : 0;
        }
        if (settings.Clusters is { } clusters) {
            long pixels = originalSegmentation ? sourcePixels : editedPixels;
            int width = originalSegmentation ? original.Width : edited!.Value.Width;
            baseBytes += 7 * pixels + 12L * clusters;
            scratch[count++] = 16 * pixels + 12L * (width + clusters);
        }
        if (fast) {
            // до одной точки на пиксель, включая явный массив KeyPoint OpenCVSharp и карту координат
            baseBytes += 8 * sourcePixels;
            scratch[count++] = 29 * sourcePixels;
        }

        if (settings.Plate) {
            scratch[count++] = PlateAnalysisService.EstimateVideoScratchBytes(original.Width, original.Height);
        }

        // любая группа из N задач помещается, даже если первыми запустятся самые дорогие
        scratch[..count].Sort();
        int workers = 0;
        while (workers < count && workers < limit && baseBytes + scratch[count - 1 - workers] <= budget) {
            baseBytes += scratch[count - 1 - workers];
            workers++;
        }
        return workers == 0 ? default : (workers, baseBytes);
    }

    // готовим общие каналы один раз, затем выполняем независимые методы на неизменяемых входах
    public static VideoFrameAnalysisResult Analyze(AnalysisInputSnapshot original, AnalysisInputSnapshot? edited,
        AnalysisImageSource edgeSource, AnalysisImageSource segmentationSource, VideoFrameAnalysisSettings settings,
        CancellationToken token, int workers = 1) {
        long started = Stopwatch.GetTimestamp();
        VideoAnalysisCapture source = original.Video ?? throw new ArgumentException("Отсутствует снимок оригинала");
        if (original.Source != AnalysisImageSource.Original || edited is not null &&
            (edited.Video is not { } processed || !source.Key.IsSameFrame(processed.Key))) {
            throw new InvalidOperationException("Входы анализа относятся к разным кадрам");
        }
        AnalysisInputSnapshot edgeInput = edgeSource == AnalysisImageSource.Edited ? edited ?? original : original;
        AnalysisInputSnapshot segInput = segmentationSource == AnalysisImageSource.Edited ? edited ?? original : original;
        if (settings.EdgeMethod is not null && edgeInput.Source != edgeSource ||
            settings.Clusters is not null && segInput.Source != segmentationSource) {
            throw new InvalidOperationException("Нет снимка для выбранного источника метода");
        }

        byte[]? originalRgb = null;
        byte[]? editedRgb = null;
        byte[]? originalGray = null;
        byte[]? editedGray = null;
        AnalysisEdgeResult? edges = null;
        AnalysisSegmentationResult? segmentation = null;
        AnalysisKeypointResult? keypoints = null;
        AnalysisPlateResult? plate = null;
        string? plateError = null;
        Action[] methods = new Action[4];
        int count = 0;
        if (settings.EdgeMethod is { } method) {
            PixelSize size = edgeInput.Video!.Pixels.Size;
            byte[] rgb = ReadRgb(edgeInput);
            byte[] gray = ReadGray(edgeInput, rgb);
            methods[count++] = () => {
                byte[] result = method == AnalysisEdgeMethod.Canny
                    ? CannyAnalysisService.CalculateFromGray(gray, size.Width, size.Height, settings.Lower, settings.Upper, token)
                    : RobertsAnalysisService.CalculateFromGray(gray, size.Width, size.Height, token);
                edges = method == AnalysisEdgeMethod.Canny
                    ? new(edgeInput, size.Width, size.Height, rgb, result, method, settings.Lower, settings.Upper)
                    : new(edgeInput, size.Width, size.Height, rgb, result);
            };
        }
        if (settings.Clusters is { } clusters) {
            PixelSize size = segInput.Video!.Pixels.Size;
            byte[] rgb = ReadRgb(segInput);
            methods[count++] = () => {
                var (segmented, labels, centers, compactness) =
                    KMeansAnalysisService.CalculateFromRgb(rgb, size.Width, size.Height, clusters, token);
                segmentation = new(segInput, size.Width, size.Height, clusters, rgb, segmented, labels, centers, compactness);
            };
        }
        if (settings.FastThreshold is { } threshold) {
            PixelSize size = source.Pixels.Size;
            byte[] rgb = ReadRgb(original);
            byte[] gray = ReadGray(original, rgb);
            methods[count++] = () => {
                keypoints = new(original, size.Width, size.Height, threshold, settings.FastNonmaxSuppression, rgb,
                    FastAnalysisService.CalculateFromGray(gray, size.Width, size.Height, threshold, settings.FastNonmaxSuppression, token));
            };
        }

        if (settings.Plate) {
            PixelSize size = source.Pixels.Size;
            byte[] rgb = ReadRgb(original);
            byte[] gray = ReadGray(original, rgb);
            methods[count++] = () => {
                try {
                    plate = new(original, size.Width, size.Height, rgb,
                        PlateAnalysisService.FindPlateRegions(gray, size.Width, size.Height, token));
                }
                catch (Exception error) when (!token.IsCancellationRequested) {
                    plateError = "Поиск номера: " + error.Message;
                }
            };
        }

        int active = 0;
        int peak = 0;
        object peakGate = new();
        if (workers == 1) {
            for (int index = 0; index < count; index++) {
                token.ThrowIfCancellationRequested();
                methods[index]();
            }
            peak = count > 0 ? 1 : 0;
        }
        else {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, index => {
                lock (peakGate) {
                    peak = Math.Max(peak, ++active);
                }
                try {
                    methods[index]();
                }
                finally {
                    lock (peakGate) {
                        active--;
                    }
                }
            });
        }
        return new(edges, segmentation, keypoints) {
            PeakWorkers = peak, CalculationTime = Stopwatch.GetElapsedTime(started), Plate = plate, PlateError = plateError
        };

        // проверяем геометрию до выделения и храним один RGB на исходный растр
        byte[] ReadRgb(AnalysisInputSnapshot input) {
            VideoAnalysisCapture capture = input.Video!;
            if (capture.Pixels.Size != capture.Key.Geometry.OutputSize) {
                throw new InvalidOperationException("Размер кадра не соответствует выбранному источнику");
            }
            return input.Source == AnalysisImageSource.Original
                ? originalRgb ??= capture.Pixels.CopyRgb(token)
                : editedRgb ??= capture.Pixels.CopyRgb(token);
        }

        // Робертс, Canny и FAST на общем источнике делят один яркостный буфер
        byte[] ReadGray(AnalysisInputSnapshot input, byte[] rgb) {
            PixelSize size = input.Video!.Pixels.Size;
            return input.Source == AnalysisImageSource.Original
                ? originalGray ??= AnalysisImageDecoder.ToLuminance(rgb, size.Width, size.Height, token)
                : editedGray ??= AnalysisImageDecoder.ToLuminance(rgb, size.Width, size.Height, token);
        }
    }
}
