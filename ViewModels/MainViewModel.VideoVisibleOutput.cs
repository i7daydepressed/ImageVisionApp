using System;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private readonly VideoPreviewMeasurements visibleVideoOutput = new();
    private long visibleVideoOutputSequence;

    internal bool IsVideoAnalysisOutputSelected => videoAnalysisVisible;
    private bool IsOriginalVideoOutputSelected =>
        !IsVideoForegroundOutputSelected && !videoAnalysisVisible && (!videoEditorVisible || VideoEditorPreviewTabIndex == 0 || !videoEditorPlan.HasSteps);

    // измеряем только публикации реально выбранного вида, без задержки декодирования или рендера
    private void RecordVisibleOriginalFrame(bool live) {
        if (live && IsOriginalVideoOutputSelected && VideoPreviewFrame is not null) {
            RecordVisibleVideoOutput();
        }
    }

    // измененный кадр учитываем только при предъявлении в редакторе
    private void RecordVisibleEditedFrame(bool live) {
        if (live && videoEditorVisible && !videoAnalysisVisible && !IsOriginalVideoOutputSelected && videoEditorOutputVisible) {
            RecordVisibleVideoOutput();
        }
    }

    // ведем отдельное короткое окно, поскольку исполнитель может считать и скрытые результаты
    private void RecordVisibleVideoOutput() {
        long now = Environment.TickCount64;
        if (!visibleVideoOutput.IsActive) {
            visibleVideoOutput.Start(now, settleMs: 0);
        }
        visibleVideoOutput.TryComplete(now, default, out _);
        visibleVideoOutput.RecordOutput(visibleVideoOutput.Id, ++visibleVideoOutputSequence, now, now);
    }

    // во время Play учитываем и ожидание кадра, на паузе сохраняем только законченное окно
    private void StopVisibleVideoOutputOnPause() {
        if (videoAnalysisVisible && !IsCombinedAnalysisOutputSelected && !IsFrameAnalysisOutputSelected ||
            !IsVideoOpen || mediaPreviewsDisposed) {
            return;
        }
        long now = Environment.TickCount64;
        if (videoWantsToPlay) {
            if (!visibleVideoOutput.IsActive) {
                visibleVideoOutput.Start(now, settleMs: 0);
            }
            visibleVideoOutput.TryComplete(now, default, out _);
        }
        else if (visibleVideoOutput.IsActive && !PlaybackStatus.IsPlaying) {
            visibleVideoOutput.TryComplete(now, default, out _);
            visibleVideoOutput.Stop(keepLast: true);
        }
    }

    // смена документа, зависимости или видимой картинки начинает новое измерение
    private void ResetVisibleVideoOutput() => visibleVideoOutput.Stop(keepLast: false);

    // сообщаем известные рабочие буферы только того пути, который выдает выбранную картинку
    internal (long Bytes, string Path) ReadVisibleVideoOutputBuffers() {
        if (preparedSource is not null) {
            return (PreparedVideo?.ReservedBytes ?? 0, "Подготовленный участок");
        }

        long source = videoPlayback?.PreviewBufferBytes ?? 0;
        return IsOriginalVideoOutputSelected
            ? (source, "Передача кадра плеера")
            : (source + (videoPreviewExecutor?.Metrics.ReservedBytes ?? 0), "Передача кадра и редактор");
    }

    // показываем частоту непосредственных публикаций выбранного вида, а на паузе указываем состояние
    internal (VideoPreviewMeasurements.Window? Window, string Status) ReadVisibleVideoOutput() {
        if (!IsVideoOpen || mediaPreviewsDisposed) {
            return (null, "Видео не открыто");
        }
        StopVisibleVideoOutputOnPause();
        VideoPreviewMeasurements.Window? window = visibleVideoOutput.Last;
        string picture = IsCombinedAnalysisOutputSelected ? "Результат видеоряда" :
            IsFrameAnalysisOutputSelected ? "Покадровый результат" :
            IsVideoForegroundOutputSelected ? "Вычитание фона" : IsOriginalVideoOutputSelected ? "Оригинал" : "Изменённое";
        string stage = IsCombinedAnalysisOutputSelected || IsFrameAnalysisOutputSelected ? "Анализ" :
            videoForegroundVisible ? "Работа с видеорядом" :
            videoEditorVisible ? "Редактирование" : "Информация";
        string? notice = strictPipelineError ?? (videoForegroundVisible && videoForegroundFailed ? VideoForegroundStatus :
            videoAnalysisVisible && videoForegroundFailed ? combinedVideoAnalysisNotice :
            videoEditorVisible ? IsOriginalVideoOutputSelected ? VideoEditorSourceNotice : VideoEditorNotice : null);
        string state = notice ?? (visibleVideoOutput.IsActive
            ? window is null ? "Измеряем публикации…" : "Воспроизведение"
            : videoWantsToPlay ? "Ожидаем публикацию" : "Пауза");
        string duration = window is { } sample ? $" · последний замер {sample.DurationMs / 1000.0:0.##} с" : "";
        return (window, $"{stage} · {picture}\n{state}{duration}");
    }
}
