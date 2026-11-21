using System;
using System.Diagnostics;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    internal readonly record struct VideoAnalysisMeasurement(VideoAnalysisFrameKey Frame, int Workers, int PeakWorkers,
        long ReservedBytes, TimeSpan Calculation, TimeSpan? Composition, TimeSpan? Presentation);

    private long videoAnalysisReservedBytes;
    private int videoAnalysisWorkerLimit;
    private long? videoAnalysisStartedAt;
    private long videoAnalysisPresentedRevision;
    private VideoAnalysisMeasurement? videoAnalysisMeasurement;

    // начинаем отсчет при приеме команды, включая ожидание подтвержденного стоп-кадра
    private void BeginVideoAnalysisMeasurement() => videoAnalysisStartedAt = Stopwatch.GetTimestamp();

    // убираем измерение предыдущей команды или версии результата
    private void ClearVideoAnalysisMeasurement() {
        videoAnalysisStartedAt = null;
        videoAnalysisMeasurement = null;
        videoAnalysisEditorStage = null;
    }

    // запоминаем отдельно время работы методов и фактический максимум независимых ветвей
    private void RecordVideoAnalysisCalculation(VideoAnalysisFrameKey frame, int workers, long bytes,
        VideoFrameAnalysisResult result) {
        videoAnalysisMeasurement = new(frame, workers, result.PeakWorkers, bytes, result.CalculationTime, null, null);
    }

    // фиксируем время после публикации итогового растра в модели интерфейса
    private void RecordVideoAnalysisPresentation(long revision, VideoAnalysisFrameKey frame, TimeSpan composition) {
        if (!videoAnalysisResultTabSelected || videoAnalysisStartedAt is not { } started ||
            videoAnalysisMeasurement is not { Presentation: null } measurement ||
            !measurement.Frame.IsSameFrame(frame)) {
            return;
        }
        videoAnalysisPresentedRevision = revision;
        videoAnalysisMeasurement = measurement with {
            Presentation = Stopwatch.GetElapsedTime(started),
            Composition = composition
        };
    }

    // результат старой позиции или предыдущего выбранного вида не участвует в метриках всего выхода
    internal (int Workers, long Bytes, VideoAnalysisMeasurement? Sample, string Status) ReadVideoAnalysisState(bool displayed) {
        VideoAnalysisMeasurement? sample = videoAnalysisMeasurement is { } measurement && IsCurrentAnalysisLayer(measurement.Frame)
            ? measurement : null;
        string status = !videoAnalysisVisible ? "Вкладка «Анализ» не активна" :
            displayed && !videoAnalysisResultTabSelected ? DescribeVideoAnalysisInput() : AnalysisStatusMessage;
        if (displayed && (!videoAnalysisVisible || !videoAnalysisResultTabSelected)) {
            sample = null;
        }
        else if (videoAnalysisPresentedRevision != videoAnalysisViewRevision) {
            sample = sample is { } current ? current with { Presentation = null, Composition = null } : null;
        }
        return (videoAnalysisWorkerLimit, videoAnalysisReservedBytes, sample, status);
    }
}
