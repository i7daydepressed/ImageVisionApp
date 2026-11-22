using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private sealed class VideoFrameStepWork(MediaDocument document, VideoPlaybackSession playback,
        long position, bool atEnd, VideoFrameCursor? anchor, VideoDecodedFrameHint? decodedAnchor, bool prepared) {
        public MediaDocument Document { get; } = document;
        public VideoPlaybackSession Playback { get; } = playback;
        public long Position { get; } = position;
        public bool AtEnd { get; } = atEnd;
        public VideoFrameCursor? Anchor { get; } = anchor;
        public VideoDecodedFrameHint? DecodedAnchor { get; } = decodedAnchor;
        public bool Prepared { get; } = prepared;
        public List<int> Pending { get; } = [];
        public CancellationTokenSource Stop { get; } = new();
        public Task? Cancellation { get; set; }

        // объединяем соседние шаги одного направления, сохраняя порядок разворотов у края
        public void Add(int distance) {
            if (distance != 0 && Pending.Count > 0 && Math.Sign(Pending[^1]) == Math.Sign(distance)) {
                Pending[^1] = (int)Math.Clamp((long)Pending[^1] + distance, int.MinValue, int.MaxValue);
            }
            else {
                Pending.Add(distance);
            }
        }
    }

    private static readonly VideoEditorPlan ExactOriginalOnly = new(0, []);
    private VideoFrameStepWork? videoFrameStep;
    private VideoFrameCursor? videoPinnedFrame;
    private VideoFrameCursor? preparedPresentedFrame;
    private string? videoFrameStepNotice;

    internal VideoFrameCursor? VideoExactFrame => videoPinnedFrame;

    // переводим стрелку в прежний временной переход или запрос точного кадра
    internal void SeekVideoByKeyboard(int direction, VideoArrowStep step) {
        int sign = Math.Sign(direction);
        if (sign == 0) {
            return;
        }
        switch (step) {
            case VideoArrowStep.OneFrame:
            case VideoArrowStep.FiveFrames:
                RequestVideoFrameStep(sign * (step == VideoArrowStep.OneFrame ? 1 : 5));
                break;
            default:
                SeekVideoByKeyboard((long)sign * (step == VideoArrowStep.OneSecond ? 1000 : 3000));
                break;
        }
    }

    // используем номер показанного кадра, а без него определяем кадр по остановленному времени
    private void RequestVideoFrameStep(int distance) {
        if (videoPlayback is not { } playback || !PlaybackStatus.CanSeek ||
            CurrentDocument is not { Kind: MediaKind.Video, SourcePath: not null } document) {
            return;
        }
        CancelVideoAnalysisApply();
        ResetVisibleVideoOutput();
        liveAnalysisSeekTarget = null;
        preparedPauseOnCurrentPosition = false;
        if (videoFrameStep is { } step) {
            step.Add(distance);
            return;
        }
        if (preparedOpening is not null || preparedStartPending || livePreviewHandoff is not null) {
            UpdatePlaybackStatus(PlaybackStatus with { Notice = "Покадровый переход доступен после переключения предпросмотра" });
            return;
        }

        InvalidateVideoForeground();
        VideoFrameCursor? anchor = videoPinnedFrame;
        bool atEnd = preparedSource is not null ? preparedAtEnd :
            videoRestartOnPlay || playback.State == VLCState.Ended ||
            PlaybackStatus.Position >= PlaybackStatus.Duration;
        if (anchor is null) {
            if (preparedSource is not null && preparedFrom is not null && preparedWaitingPosition is null) {
                anchor = preparedPresentedFrame;
            }
            else if (PlaybackStatus.Position == 0 && ReferenceEquals(VideoPreviewFrame, VideoFirstFrame)) {
                anchor = new(new(0, 1), 0, false);
            }
        }
        BeginVideoFrameStep(document, playback, distance, anchor, atEnd);
    }

    // закрепленный кадр пересчитываем по его номеру при изменении плана или режима
    private bool RecalculatePinnedPreparedFrame() {
        if (videoPinnedFrame is not { } pin || videoPlayback is not { } playback ||
            CurrentDocument is not { SourcePath: not null } document) {
            return false;
        }
        BeginVideoFrameStep(document, playback, 0, pin, pin.AtEnd);
        return true;
    }

    // после отмены возвращаем уже выбранный кадр, если до Pause он еще не был перенесен или обработан
    private void RestorePausedPreparedFrame() {
        if (videoPinnedFrame is not { } pin) {
            RequestPreparedVideo(preparedPosition, preparedAtEnd);
        }
        else if (preparedFrom is null || preparedPresentedFrame != pin ||
            videoEditorPlan.HasSteps && !videoEditorOutputVisible) {
            RecalculatePinnedPreparedFrame();
        }
    }

    // останавливаем общий плеер и возвращаем буферы прежних заявок до чтения точной пары
    private void BeginVideoFrameStep(MediaDocument document, VideoPlaybackSession playback, int distance,
        VideoFrameCursor? anchor, bool atEnd) {
        CancelVideoFrameStep();
        requestedVideoSeek = null;
        verifiedVideoSeekStop?.Cancel();
        videoHandoffReplay = false;
        videoWantsToPlay = false;
        videoWasPlaying = false;
        videoFrameStepNotice = null;
        playback.SetPaused(true);
        StopVideoPreviewMonitoring(keepLast: true);
        InvalidateVideoEditorRequests();
        preparedVideo?.Cancel();
        SetVideoEditorProcessingTime(null);

        bool prepared = preparedSource is not null;
        VideoFrameStepWork work = new(document, playback, PlaybackStatus.Position, atEnd, anchor,
            prepared ? null : videoLiveSeekHint, prepared);
        work.Add(distance);
        videoFrameStep = work;
        UpdatePlaybackStatus(PlaybackStatus with { IsPlaying = false, Notice = "Ищем точный видеокадр…" });
        EnsureVideoPlaybackTimer();
        _ = ReadVideoFrameStepsAsync(work, videoFileIdentity, preparedSource as FfprobeVideoMediaSource);
    }

    // новая команда исключает позднюю публикацию, а старый работник сам возвращает удержанные буферы
    private bool CancelVideoFrameStep(bool clearPin = false) {
        if (clearPin) {
            videoPinnedFrame = null;
        }
        videoFrameStepNotice = null;
        VideoFrameStepWork? work = videoFrameStep;
        videoFrameStep = null;
        if (work is null) {
            return false;
        }
        preparedVideo?.Cancel();
        work.Cancellation = work.Stop.CancelAsync();
        return true;
    }

    // разбираем заявки по порядку, а пиксели выводим только после последней цели
    private async Task ReadVideoFrameStepsAsync(VideoFrameStepWork work, VideoFileIdentity? identity,
        FfprobeVideoMediaSource? source) {
        CancellationToken token = work.Stop.Token;
        try {
            source ??= await Task.Run(() =>
                FfprobeVideoMediaSource.OpenAsync(work.Document.SourcePath!, identity, token), token);
            VideoFrameCursor? cursor = work.Anchor;
            while (work.Pending.Count != 0) {
                token.ThrowIfCancellationRequested();
                int distance = work.Pending[0];
                work.Pending.RemoveAt(0);
                while (HasLivePreviewWork) {
                    await Task.Delay(25, token);
                }

                VideoEditorPlan plan = work.Prepared ? videoEditorPlan : ExactOriginalOnly;
                preparedVideo ??= new PreviewCoordinator();
                long generation = preparedVideo.Request(source, new(work.Position, work.AtEnd) {
                    Anchor = cursor,
                    DecodedAnchor = cursor is null ? work.DecodedAnchor : null,
                    FrameOffset = distance,
                    SingleFrame = true
                }, plan, PreviewSettings.BudgetBytes);

                using PreparedVideoFrame frame = await TakeVideoFrameStepAsync(preparedVideo, generation, token);
                cursor = new(frame.Time, frame.Ordinal, frame.Until is null);
                if (work.Pending.Count != 0) {
                    continue;
                }
                long duration = PlaybackStatus.Duration;
                long position = Math.Clamp((long)Math.Round(frame.Time.Milliseconds), 0, Math.Max(0, duration));
                await PositionPlayerForFrameStepAsync(work.Playback, Math.Min(position, Math.Max(0, duration - 1)), token);
                token.ThrowIfCancellationRequested();
                if (work.Pending.Count != 0) {
                    continue;
                }
                if (work.Prepared && plan.Revision != videoEditorPlan.Revision) {
                    work.Add(0);
                    continue;
                }

                PublishVideoFrameStep(work, frame, cursor.Value, position);
            }
        }
        catch (Exception error) {
            if (ReferenceEquals(videoFrameStep, work)) {
                videoFrameStep = null;
                preparedVideo?.Cancel();
                if (work.Prepared && videoPinnedFrame is null) {
                    RequestPreparedVideo(preparedPosition, preparedAtEnd);
                }
                else if (work.Prepared && preparedFrom is null) {
                    SwitchPreparedPreviewToLive(work.Position, error.Message);
                }
                else if (!work.Prepared) {
                    RequestVideoEditorFrame();
                }
                videoFrameStepNotice = $"Покадровый переход недоступен: {error.Message}";
                UpdatePlaybackStatus(PlaybackStatus with { IsPlaying = false, Notice = videoFrameStepNotice });
            }
        }
        finally {
            if (work.Cancellation is { } pending) {
                try {
                    await pending;
                }
                catch (Exception) {
                    // ошибка закрытия завершенного запроса не относится к новой команде
                }
            }
            work.Stop.Dispose();
        }
    }

    // забираем точный номер из единственной готовой пары, не применяя поиск по миллисекундам
    private static async Task<PreparedVideoFrame> TakeVideoFrameStepAsync(PreviewCoordinator coordinator,
        long generation, CancellationToken token) {
        while (true) {
            token.ThrowIfCancellationRequested();
            if (coordinator.TakeFirst(generation) is { } frame) {
                return frame;
            }
            PreparedVideoWindow snapshot = coordinator.Snapshot;
            if (snapshot.Error is { } error) {
                throw error;
            }
            if (snapshot.Ended) {
                throw new InvalidDataException("Источник не вернул запрошенный видеокадр");
            }
            await Task.Delay(20, token);
        }
    }

    // позиционируем тот же плеер для звука и следующего Play, пиксели уже получены по номеру
    private async Task PositionPlayerForFrameStepAsync(VideoPlaybackSession playback, long position, CancellationToken token) {
        if (playback.State == VLCState.Ended && !TryRestartCurrentVideo(playback)) {
            throw new InvalidDataException("Не удалось повторно открыть видео");
        }
        await WaitForFrameStepPauseAsync(playback, token);
        playback.Seek(position);
        await WaitForFrameStepPauseAsync(playback, token, position);
    }

    // ждем подтвержденную паузу с ограничением времени без блокировки окна
    private static async Task WaitForFrameStepPauseAsync(VideoPlaybackSession playback, CancellationToken token,
        long? position = null) {
        long deadline = Environment.TickCount64 + 3000;
        while (true) {
            token.ThrowIfCancellationRequested();
            if (playback.State == VLCState.Paused && playback.CanSeek &&
                (position is null || playback.Position >= 0 && Math.Abs(playback.Position - position.Value) <= 25)) {
                return;
            }
            if (playback.State == VLCState.Error || Environment.TickCount64 >= deadline) {
                throw new InvalidDataException("Плеер не подтвердил позицию покадрового перехода");
            }
            await Task.Delay(25, token);
        }
    }

    // сохраняем точную метку вместе с показанными пикселями и отдаем живой цепочке тот же исходник
    private void PublishVideoFrameStep(VideoFrameStepWork work, PreparedVideoFrame frame,
        VideoFrameCursor cursor, long position) {
        if (work.Prepared) {
            preparedPosition = position;
            preparedAtEnd = cursor.AtEnd;
            preparedWaitingPosition = null;
            preparedNeedsSeek = false;
            preparedSeekDeadline = 0;
            PublishPreparedVideo(frame);
        }
        else {
            WriteableBitmap bitmap = GetPreparedBitmap(null, frame.Original.Size);
            try {
                using ILockedFramebuffer target = bitmap.Lock();
                frame.Original.CopyTo(target);
            }
            catch {
                bitmap.Dispose();
                throw;
            }
            livePreviewAnchor = position;
            videoRestartOnPlay = cursor.AtEnd;
            videoLiveSeekHint = new(cursor.Time.Milliseconds, cursor.Ordinal, FromStart: cursor.Ordinal == 0);
            SetVideoSeekPreview(bitmap);
        }

        videoPinnedFrame = cursor;
        videoFrameStep = null;
        UpdatePlaybackStatus(PlaybackStatus with {
            Position = position, IsPlaying = false, CanPlay = true, Notice = null
        });
        if (!work.Prepared) {
            AcceptVideoEditorFrame();
        }
    }
}
