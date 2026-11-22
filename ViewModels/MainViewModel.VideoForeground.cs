using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const string ForegroundNoVideo = "Откройте видео для вычитания фона";
    private const string ForegroundDisabled = "Оригинал · вычитание фона выключено";
    private const string ForegroundPreparing = "Подготавливаем передний план…";

    private bool videoForegroundVisible;
    private bool videoForegroundEnabled = true;
    private bool videoForegroundFailed;
    private bool videoForegroundApplying;
    private int videoForegroundInputRevision = -1;
    private int videoForegroundViewIndex;
    private int videoForegroundSourceIndex;
    private string? videoForegroundPath;
    private (long Position, long ObservedAt, long Target)? videoForegroundLiveClock;
    private CancellationTokenSource? videoForegroundStop;
    private SemaphoreSlim? videoForegroundWake;
    private VideoForegroundFrame? videoForegroundFrame;
    private WriteableBitmap? videoForegroundOriginal;
    private WriteableBitmap? videoForegroundOutput;
    private string videoForegroundStatus = ForegroundNoVideo;

    // для фото показываем исходный bitmap, для видео предпочитаем оригинал рассчитанного кадра
    public Bitmap? VideoForegroundInputPreview => !videoForegroundVisible || mediaPreviewsDisposed
        ? null
        : CurrentDocument?.Kind switch {
            MediaKind.Image => OriginalImage,
            MediaKind.Video => videoForegroundOriginal ??
                (!VideoForegroundEnabled || VideoForegroundSource == VideoPipelineStage.Original ? VideoPreviewFrame : null),
            _ => null
        };
    public Bitmap? VideoForegroundPreview => !VideoForegroundEnabled && IsVideoOpen
        ? VideoForegroundInputPreview : videoForegroundOutput;
    public bool CanDisplayVideoForegroundEffect => IsVideoOpen && VideoForegroundEnabled;
    public bool CanApplyVideoForeground => IsVideoForegroundOutputSelected && IsVideoOpen &&
        PlaybackStatus.CanPlay && !mediaPreviewsDisposed;
    public string VideoForegroundStatus => videoForegroundStatus;
    public string? VideoForegroundPath {
        get => videoForegroundPath;
        private set => SetProperty(ref videoForegroundPath, value);
    }
    public int VideoForegroundRevision { get; private set; }
    internal bool IsVideoForegroundOutputSelected => videoForegroundVisible && VideoForegroundEnabled;

    // меняем состав результата на той же позиции и отменяем расчет при отключении метода
    public bool VideoForegroundEnabled {
        get => videoForegroundEnabled;
        set {
            if (!SetProperty(ref videoForegroundEnabled, value)) {
                return;
            }
            OnPropertyChanged(nameof(CanDisplayVideoForegroundEffect));
            OnPropertyChanged(nameof(CanApplyVideoForeground));
            RestartVideoForeground();
        }
    }

    private VideoPipelineStage VideoForegroundSource => videoForegroundSourceIndex switch {
        1 => VideoPipelineStage.Editing,
        2 => VideoPipelineStage.Analysis,
        _ => VideoPipelineStage.Original
    };

    // новая цепочка начинает предысторию с нуля на той же позиции общего плеера
    public int SelectedVideoForegroundSourceIndex {
        get => videoForegroundSourceIndex;
        set {
            if (!IsVideoOpen || value is < 0 or > 2 || value == videoForegroundSourceIndex) {
                return;
            }
            VideoPipelineStage candidate = value switch {
                1 => VideoPipelineStage.Editing,
                2 => VideoPipelineStage.Analysis,
                _ => VideoPipelineStage.Original
            };
            if (!TryValidateVideoSequenceSource(candidate, out string? error)) {
                SetVideoForegroundStatus(error ?? "Невозможно выбрать источник");
                OnPropertyChanged(nameof(SelectedVideoForegroundSourceIndex));
                return;
            }
            if (SetProperty(ref videoForegroundSourceIndex, value)) {
                RestartVideoForeground();
            }
        }
    }

    // меняем вид готового результата без перечитывания кадров и переобучения фона
    public int VideoForegroundViewIndex {
        get => videoForegroundViewIndex;
        set {
            if (value is < 0 or > 1 || value == videoForegroundViewIndex) {
                return;
            }
            videoForegroundViewIndex = value;
            OnPropertyChanged(nameof(VideoForegroundViewIndex));
            if (videoForegroundFrame is { } frame && videoForegroundOutput is { } output) {
                PaintVideoForeground(output, frame, result: true, maskOnly: value == 1);
                NotifyVideoForegroundPixelsChanged();
            }
        }
    }

    // отдельное чтение работает только пока открыта вкладка видеоряда
    internal void SetVideoForegroundVisible(bool visible) {
        if (videoForegroundVisible == visible) {
            return;
        }
        videoForegroundVisible = visible;
        OnPropertyChanged(nameof(CanApplyVideoForeground));
        ResetVisibleVideoOutput();
        RestartVideoForeground();
    }

    // вручную выбираем кадр после подтверждения паузы, сохраняя рассчитанную предысторию
    internal void ApplyVideoForeground() {
        if (!CanApplyVideoForeground) {
            return;
        }
        videoForegroundFailed = false;
        videoForegroundApplying = true;
        PauseVideoPlayback();
        if (strictPipelineError is not null && IsCombinedStrictOutputSelected) {
            RestartStrictPipelineOutput();
        }
        RefreshVideoForeground();
    }

    // пересчитываем на текущей позиции при смене метода или его бюджета
    private void RestartVideoForeground() {
        if (videoForegroundVisible) {
            ResetVisibleVideoOutput();
        }
        InvalidateVideoForeground();
        RestartStrictPipelineOutput();
        RefreshVideoForeground();
    }

    // отменяем старый проход сразу, новый начнется после освобождения читателя
    private void InvalidateVideoForeground() {
        videoForegroundFailed = false;
        videoForegroundApplying = false;
        videoForegroundLiveClock = null;
        VideoForegroundPath = null;
        videoForegroundStop?.Cancel();
        ClearVideoForeground();
        ResetPipelineOutputMeasurements();
        SetVideoForegroundStatus(!IsVideoOpen ? ForegroundNoVideo :
            VideoForegroundEnabled ? ForegroundPreparing : ForegroundDisabled);
    }

    // обновляем цель из общей шкалы, а точный кадр берем по номеру при одинаковых PTS
    private bool TryGetVideoForegroundRequest(out VideoFrameReadRequest request) {
        request = default;
        if (!WantsVideoTemporalPipeline || mediaPreviewsDisposed ||
            IsCombinedStrictOutputSelected && videoPinnedFrame is null ||
            videoPlayback?.State is not (VLCState.Playing or VLCState.Paused or VLCState.Ended) ||
            requestedVideoSeek is not null || liveAnalysisSeekTarget is not null || videoFrameStep is not null ||
            preparedOpening is not null || livePreviewHandoff is not null || videoRestarting ||
            preparedSource is not null && (preparedStartPending || preparedNeedsSeek || preparedPauseOnCurrentPosition ||
                preparedWaitingPosition is not null)) {
            return false;
        }
        if (videoForegroundApplying) {
            if (!videoWantsToPlay && (PlaybackStatus.IsPlaying || videoPlayback.State == VLCState.Playing)) {
                return false;
            }
            videoForegroundApplying = false;
        }
        long position = PlaybackStatus.Position;
        bool atEnd = PlaybackStatus.Duration > 0 && position >= PlaybackStatus.Duration;
        VideoFrameCursor? cursor = videoPinnedFrame ??
            (preparedSource is not null ? preparedPresentedFrame : retainedVideoSeekCursor);
        if (cursor is null && !PlaybackStatus.IsPlaying && position == 0 &&
            VideoFirstFrame is { } first && ReferenceEquals(VideoPreviewFrame, first)) {
            cursor = new(new(0, 1), 0, false);
        }
        position = GetVideoForegroundPosition(position, cursor is not null || atEnd);
        request = new(position, atEnd) { Anchor = atEnd && cursor is not { AtEnd: true } ? null : cursor };
        return true;
    }

    // в живом режиме продолжаем часы между обновлениями VLC, а на паузе берем точную общую позицию
    private long GetVideoForegroundPosition(long position, bool confirmed) {
        if (confirmed || preparedSource is not null || !PlaybackStatus.IsPlaying || !videoWantsToPlay ||
            videoLiveFrame is null || PlaybackStatus.Duration <= 0) {
            videoForegroundLiveClock = null;
            return position;
        }

        long now = Environment.TickCount64;
        if (videoForegroundLiveClock is not { } clock) {
            clock = (position, now, position);
        }
        if (clock.Position != position) {
            clock = (position, now, clock.Target);
        }

        // ограничиваем ход при задержанном обновлении VLC и не перематываем вычислитель при малой поправке часов
        long estimate = position + Math.Min(now - clock.ObservedAt, 350);
        long target = Math.Min(Math.Max(estimate, clock.Target), PlaybackStatus.Duration - 1);
        videoForegroundLiveClock = (position, clock.ObservedAt, target);
        return target;
    }

    // оставляем одну цель чтения и будим уже открытый последовательный источник
    private void RefreshVideoForeground() {
        if ((!videoForegroundVisible && !IsCombinedAnalysisOutputSelected) || mediaPreviewsDisposed) {
            return;
        }
        if (videoForegroundOriginal is null) {
            OnPropertyChanged(nameof(VideoForegroundInputPreview));
            if (IsVideoOpen && !VideoForegroundEnabled) {
                OnPropertyChanged(nameof(VideoForegroundPreview));
            }
            if (videoForegroundInputRevision != VideoPreviewRevision) {
                videoForegroundInputRevision = VideoPreviewRevision;
                NotifyVideoForegroundPixelsChanged();
            }
        }
        if (!IsVideoOpen) {
            SetVideoForegroundStatus(ForegroundNoVideo);
            return;
        }
        if (videoPlayback is null && PlaybackStatus.Notice is { } unavailable) {
            SetVideoForegroundStatus(unavailable);
            return;
        }
        if (!VideoForegroundEnabled) {
            SetVideoForegroundStatus(ForegroundDisabled);
            return;
        }
        if (IsCombinedStrictOutputSelected && videoPinnedFrame is null) {
            return;
        }
        if (videoForegroundFailed || !TryGetVideoForegroundRequest(out VideoFrameReadRequest request) ||
            videoForegroundFrame?.Covers(request) == true) {
            return;
        }
        if (!PlaybackStatus.IsPlaying && videoForegroundFrame is not null) {
            ClearVideoForeground();
            SetVideoForegroundStatus(ForegroundPreparing);
        }
        if (videoForegroundWake is { CurrentCount: 0 } wake) {
            wake.Release();
        }
        if (videoForegroundStop is null && CurrentDocument is { SourcePath: { } path } document) {
            _ = ProcessVideoForegroundAsync(document, path, videoFileIdentity);
        }
    }

    // читаем и обучаем модель вне UI; перед показом сверяем документ, проход и последнюю позицию
    private async Task ProcessVideoForegroundAsync(MediaDocument document, string path, VideoFileIdentity? identity) {
        using CancellationTokenSource stop = new();
        using SemaphoreSlim wake = new(0, 1);
        videoForegroundStop = stop;
        videoForegroundWake = wake;
        VideoForegroundSession? session = null;
        try {
            SetVideoForegroundStatus(ForegroundPreparing);
            VideoPreviewSettings settings = PreviewSettings;
            IVideoMediaSource source = await Task.Run(() => FfprobeVideoMediaSource.OpenWithDecoderThreadsAsync(path, identity,
                settings.ForegroundDecoderThreads, stop.Token), stop.Token);
            if (!IsCurrentVideoForegroundWork()) {
                return;
            }
            CombinedVideoWork plan = CaptureCombinedVideoWork(document, source);
            session = new VideoForegroundSession(source, stop.Token, plan.Create);
            while (IsCurrentVideoForegroundWork()) {
                if (!TryGetVideoForegroundRequest(out VideoFrameReadRequest request) ||
                    videoForegroundFrame?.Covers(request) == true) {
                    await wake.WaitAsync(stop.Token);
                    continue;
                }

                VideoPipelineOutput output = await Task.Run(() => session.ReadPipelineAsync(request), stop.Token);
                VideoForegroundFrame frame = output.Foreground ??
                    throw new InvalidOperationException("Видеоряд не вернул кадр переднего плана");
                if (!IsCurrentVideoForegroundWork() || !TryGetVideoForegroundRequest(out VideoFrameReadRequest latest)) {
                    continue;
                }
                // при непрерывном просмотре допустим последний посчитанный кадр с его собственной меткой
                bool inPlaybackOrder = PlaybackStatus.IsPlaying && !latest.AtEnd &&
                    (latest.Anchor is { } anchor ? frame.Ordinal <= anchor.Ordinal :
                        frame.Time.CompareTo(new(latest.PositionMilliseconds, 1000)) <= 0);
                if (frame.Covers(latest) || inPlaybackOrder) {
                    identity?.EnsureCurrent(path);
                    PublishCombinedOutput(output);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception error) when (IsCurrentVideoForegroundWork()) {
            videoForegroundFailed = true;
            ClearVideoForeground();
            SetVideoForegroundStatus($"Не удалось выделить передний план. {error.Message}");
            combinedVideoAnalysisNotice = error.Message;
            if (IsCombinedAnalysisOutputSelected) {
                RefreshVideoAnalysisStatus();
            }
        }
        catch (Exception) when (!IsCurrentVideoForegroundWork()) {
            // ошибка относится к закрытому или замененному проходу
        }
        finally {
            try {
                if (session is not null) {
                    await Task.Run(async () => await session.DisposeAsync());
                }
            }
            catch (Exception error) when (IsCurrentVideoForegroundWork()) {
                videoForegroundFailed = true;
                SetVideoForegroundStatus($"Не удалось закрыть чтение кадра. {error.Message}");
            }
            catch (Exception) when (!IsCurrentVideoForegroundWork()) {
                // поздняя ошибка закрытия не относится к текущему видео
            }
            finally {
                videoForegroundStop = null;
                videoForegroundWake = null;
                RefreshVideoForeground();
            }
        }

        // устаревший проход не публикует даже при одинаковом времени в другом ролике
        bool IsCurrentVideoForegroundWork() => WantsVideoTemporalPipeline && !mediaPreviewsDisposed &&
            (!IsCombinedStrictOutputSelected || videoPinnedFrame is not null) &&
            !stop.IsCancellationRequested && ReferenceEquals(CurrentDocument, document);
    }

    // сохраняем настройки выбранного входа вместе с путём, который получит каждый физический кадр
    private VideoForegroundInputPlan? CaptureVideoForegroundInput(MediaDocument document, VideoPreviewSettings settings) {
        VideoPipelineStage source = VideoForegroundSource;
        VideoPipelineStage analysisInputs = VideoPipelineStage.None;
        VideoPipelineStage backdrop = VideoPipelineStage.Original;
        VideoFrameAnalysisSettings analysis = default;
        AnalysisImageSource edge = videoEdgeSource;
        AnalysisImageSource segmentation = videoSegmentationSource;
        bool segmentedBackdrop = false;

        if (source == VideoPipelineStage.Analysis) {
            if (!AreAllAnalysisParametersValid) {
                throw new InvalidOperationException("Проверьте параметры выбранных методов на вкладке «Анализ»");
            }
            VideoAnalysisPipeline pipeline = CaptureVideoAnalysisPipeline();
            analysis = CaptureVideoFrameAnalysisSettings(pipeline);
            segmentedBackdrop = videoAnalysisBackground == VideoAnalysisBackground.Segmentation;
            backdrop = videoAnalysisBackground switch {
                VideoAnalysisBackground.Edited => VideoPipelineStage.Editing,
                VideoAnalysisBackground.Segmentation => VideoPipelinePlan.FromAnalysisSource(segmentation),
                VideoAnalysisBackground.Black => VideoPipelineStage.None,
                _ => VideoPipelineStage.Original
            };
            analysisInputs = backdrop;
            if (pipeline.EdgeSource is { } edgeSource) {
                analysisInputs |= VideoPipelinePlan.FromAnalysisSource(edgeSource);
            }
            if (pipeline.SegmentationSource is { } segmentationSource) {
                analysisInputs |= VideoPipelinePlan.FromAnalysisSource(segmentationSource);
            }
            if (pipeline.Fast || pipeline.Plate || analysisInputs == VideoPipelineStage.None) {
                analysisInputs |= VideoPipelineStage.Original;
            }
        }

        if (!VideoPipelinePlan.TryCreate(analysisInputs, source, VideoPipelineStage.VideoSequence,
            out VideoPipelinePlan? queue, out string? error)) {
            throw new InvalidOperationException(error);
        }
        VideoForegroundPath = string.Join(Environment.NewLine, queue.DescribePathsTo(VideoPipelineStage.VideoSequence));
        return source == VideoPipelineStage.Original ? null : new(document, source, videoEditorPlan, settings,
            analysis, edge, segmentation, backdrop, segmentedBackdrop, VisiblePlateRegionLimit);
    }

    // показываем пиксели выбранного входа и маску одного кадра в одинаковой геометрии
    private void PublishVideoForeground(VideoForegroundFrame frame) {
        WriteableBitmap? oldOriginal = videoForegroundOriginal;
        WriteableBitmap? oldOutput = videoForegroundOutput;
        WriteableBitmap original = GetPreparedBitmap(oldOriginal, frame.Size);
        WriteableBitmap output = GetPreparedBitmap(oldOutput, frame.Size);
        try {
            PaintVideoForeground(original, frame, result: false, maskOnly: false);
            PaintVideoForeground(output, frame, result: true, maskOnly: VideoForegroundViewIndex == 1);
        }
        catch {
            if (!ReferenceEquals(original, oldOriginal)) {
                original.Dispose();
            }
            if (!ReferenceEquals(output, oldOutput)) {
                output.Dispose();
            }
            throw;
        }
        videoForegroundFrame = frame;
        videoForegroundOriginal = original;
        videoForegroundOutput = output;
        if (PlaybackStatus.IsPlaying && videoWantsToPlay) {
            RecordVisibleVideoOutput();
        }
        OnPropertyChanged(nameof(VideoForegroundInputPreview));
        OnPropertyChanged(nameof(VideoForegroundPreview));
        NotifyVideoForegroundPixelsChanged();
        if (!ReferenceEquals(original, oldOriginal)) {
            oldOriginal?.Dispose();
        }
        if (!ReferenceEquals(output, oldOutput)) {
            oldOutput?.Dispose();
        }
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, frame.Time.Milliseconds));
        string stamp = time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff");
        SetVideoForegroundStatus(frame.Ordinal == 0
            ? $"{stamp} · Первый кадр задаёт модель фона"
            : $"{stamp} · Вычитание фона по {VideoForegroundSource switch {
                VideoPipelineStage.Editing => "результату редактирования",
                VideoPipelineStage.Analysis => "результату анализа",
                _ => "оригиналу"
            }}");
    }

    // переносим RGB и бинарную маску в BGRA-строки предпросмотра
    private static void PaintVideoForeground(WriteableBitmap bitmap, VideoForegroundFrame frame, bool result, bool maskOnly) {
        using ILockedFramebuffer target = bitmap.Lock();
        ReadOnlySpan<byte> rgb = frame.Rgb.Span;
        ReadOnlySpan<byte> mask = frame.Mask.Span;
        int rowBytes = checked(frame.Size.Width * 4);
        byte[] row = ArrayPool<byte>.Shared.Rent(rowBytes);
        try {
            int pixel = 0;
            for (int y = 0; y < frame.Size.Height; y++) {
                for (int x = 0; x < rowBytes; x += 4, pixel++) {
                    if (maskOnly || result && mask[pixel] == 0) {
                        row[x] = row[x + 1] = row[x + 2] = maskOnly ? mask[pixel] : (byte)0;
                    }
                    else {
                        int offset = pixel * 3;
                        row[x] = rgb[offset + 2];
                        row[x + 1] = rgb[offset + 1];
                        row[x + 2] = rgb[offset];
                    }
                    row[x + 3] = 255;
                }
                Marshal.Copy(row, 0, IntPtr.Add(target.Address, y * target.RowBytes), rowBytes);
            }
        }
        finally {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    // снимаем привязки к прежним изображениям до освобождения их ресурсов
    private void ClearVideoForeground() {
        ClearCombinedAnalysisOutput();
        WriteableBitmap? original = videoForegroundOriginal;
        WriteableBitmap? output = videoForegroundOutput;
        videoForegroundFrame = null;
        videoForegroundOriginal = videoForegroundOutput = null;
        OnPropertyChanged(nameof(VideoForegroundInputPreview));
        OnPropertyChanged(nameof(VideoForegroundPreview));
        original?.Dispose();
        output?.Dispose();
    }

    // перерисовываем оба вида после изменения пикселей того же bitmap
    private void NotifyVideoForegroundPixelsChanged() {
        VideoForegroundRevision++;
        OnPropertyChanged(nameof(VideoForegroundRevision));
    }

    // повторное сообщение на тиках общей шкалы не обновляет текст
    private void SetVideoForegroundStatus(string status) => SetProperty(ref videoForegroundStatus, status, nameof(VideoForegroundStatus));
}
