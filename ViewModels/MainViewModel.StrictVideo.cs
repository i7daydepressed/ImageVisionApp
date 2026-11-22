using System;
using System.Diagnostics;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const int StrictResumeMilliseconds = 200;
    private VideoFrameCursor? strictStartAnchor;
    private long? strictClockStartedAt;
    private double strictClockOrigin;
    private double strictPosition;
    private bool strictBuffering;

    // отдаем замер только действующего строгого запроса, сохраняя результат после паузы и конца
    internal (VideoPreviewMeasurements.Window? Window, long InputTotal, long OutputTotal, long AutomaticWaitMs)
        ReadStrictVideoPreviewState() =>
        IsStrictVideo && !preparedStartPending && videoFrameStep is null
            ? preparedVideo?.ReadStrictMeasurements(preparedGeneration) ?? default : default;

    // новая команда задает начальную позицию и подтвержденный номер, если пользователь пришел из покадрового режима
    private void ResetStrictVideoClock(long position, VideoFrameCursor? anchor = null) {
        strictStartAnchor = anchor;
        strictClockStartedAt = null;
        strictPosition = position;
        strictBuffering = false;
    }

    // первый кадр может быть выдан до запуска звука, в том числе при запросе точного начала или конца
    private bool TryShowFirstStrictFrame() {
        if (strictPipelineError is not null) {
            return false;
        }
        if (preparedFrom is not null) {
            return true;
        }
        if (preparedVideo is not { } coordinator) {
            return false;
        }
        PreparedVideoWindow window = coordinator.Snapshot;
        if (window.Error is { } error) {
            FailPreparedVideo(error.Message);
            return false;
        }
        if (window.Generation != preparedGeneration || window.ReadyFrames == 0) {
            return false;
        }
        using PreparedVideoFrame? first = coordinator.TakeStrict(preparedGeneration, preparedPosition);
        if (first is null) {
            return false;
        }
        if (!preparedAtEnd && strictStartAnchor is null &&
            (first.Time.CompareTo(new(preparedPosition, 1000)) > 0 ||
             preparedPosition != 0 && !first.Contains(new(preparedPosition, 1000)))) {
            FailPreparedVideo("нет кадра для запрошенной позиции");
            return false;
        }
        ShowStrictFrame(first);
        return true;
    }

    // двигаем картинку максимум на одну готовую пару за тик, а не выбираем кадр с последней позы VLC
    private void RefreshStrictVideo(VideoPlaybackSession playback) {
        VLCState state = playback.State;
        long duration = Math.Max(PlaybackStatus.Duration, playback.Duration);
        if (strictPipelineError is { } unavailable) {
            ReportStrictVideo(playback, state, duration, unavailable);
            return;
        }
        if (videoPinnedFrame is not null && preparedFrom is not null && !videoWantsToPlay) {
            ReportStrictVideo(playback, state, duration, videoFrameStepNotice);
            return;
        }

        try {
            bool first = preparedFrom is null;
            if (!TryShowFirstStrictFrame()) {
                if (IsStrictVideo) {
                    if (state == VLCState.Playing) {
                        playback.SetPaused(true);
                    }
                    ReportStrictVideo(playback, state, duration, strictPipelineError ?? "Подготовка строгого кадра…");
                }
                return;
            }
            PreparedVideoWindow window = preparedVideo!.Snapshot;
            if (window.Error is { } error) {
                FailPreparedVideo(error.Message);
                return;
            }
            if (!videoWantsToPlay || preparedAtEnd) {
                strictClockStartedAt = null;
                if (state == VLCState.Playing) {
                    playback.SetPaused(true);
                }
                bool positioned = ConfirmPreparedPosition(playback, state, duration);
                if (IsStrictVideo) {
                    ReportStrictVideo(playback, state, duration, positioned ? null : "Подтверждаем позицию…");
                }
                return;
            }
            if (first) {
                ReportStrictVideo(playback, state, duration, null);
                return;
            }

            bool nativeEnded = state == VLCState.Ended && !preparedNeedsSeek;
            if (strictBuffering) {
                if (!CanResumeStrictVideo(window)) {
                    preparedVideo.SetStrictWaiting(preparedGeneration, true);
                    ReportStrictVideo(playback, state, duration, PreviewSettings.BufferBeforeStrictResume
                        ? "Копим непрерывный запас кадров…" : "Ждем следующий кадр…");
                    return;
                }
                strictBuffering = false;
                preparedVideo.SetStrictWaiting(preparedGeneration, false);
                if (!nativeEnded) {
                    preparedNeedsSeek = true;
                    preparedSeekDeadline = 0;
                }
            }
            if (!ConfirmPreparedPosition(playback, state, duration)) {
                if (IsStrictVideo) {
                    ReportStrictVideo(playback, state, duration, "Подтверждаем позицию…");
                }
                return;
            }

            double playhead = strictPosition;
            if (state == VLCState.Playing) {
                if (strictClockStartedAt is not { } started) {
                    strictClockStartedAt = started = Stopwatch.GetTimestamp();
                    strictClockOrigin = strictPosition;
                }
                playhead = strictClockOrigin + Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            else {
                strictClockStartedAt = null;
            }

            if (preparedUntil is { } boundary && (nativeEnded || boundary.Milliseconds <= playhead)) {
                if (window.ReadyFrames == 0) {
                    HoldStrictVideo(playback, state);
                    ReportStrictVideo(playback, state, duration, "Ждем следующий кадр…");
                    return;
                }
                if (preparedPresentedFrame is not { } shown || window.FirstOrdinal != checked(shown.Ordinal + 1)) {
                    FailPreparedVideo("пропуск порядкового номера");
                    return;
                }
                using PreparedVideoFrame? next = preparedVideo!.TakeStrict(preparedGeneration,
                    StrictTimelinePosition(Math.Max(strictPosition, boundary.Milliseconds), duration));
                if (next is null) {
                    return;
                }
                ShowStrictFrame(next);
                if (nativeEnded) {
                    // конец звука не лишает строгую очередь права предъявить остаток по порядку
                    strictPosition = Math.Max(strictPosition, next.Time.Milliseconds);
                    preparedPosition = StrictTimelinePosition(strictPosition, duration);
                }
                else if (next.Until is { } after && after.Milliseconds <= playhead) {
                    // при опоздании UI показываем только этот кадр и снова согласуем звук с остановленной шкалой
                    strictPosition = Math.Max(strictPosition, next.Time.Milliseconds);
                    preparedPosition = StrictTimelinePosition(strictPosition, duration);
                    if (state == VLCState.Playing) {
                        HoldStrictVideo(playback, state);
                    }
                    ReportStrictVideo(playback, state, duration, null);
                    return;
                }
            }
            if (nativeEnded && preparedUntil is null) {
                videoWantsToPlay = false;
                preparedAtEnd = true;
                strictClockStartedAt = null;
                strictPosition = preparedPosition = Math.Max(0, duration);
                preparedVideo.StopStrictMeasurement(preparedGeneration);
            }
            else if (state == VLCState.Playing || nativeEnded) {
                strictPosition = Math.Max(strictPosition, playhead);
                preparedPosition = StrictTimelinePosition(strictPosition, duration);
            }
            else if (state == VLCState.Paused) {
                playback.SetPaused(false);
            }
            ReportStrictVideo(playback, state, duration, null);
        }
        catch (Exception failure) {
            FailPreparedVideo(failure.Message);
        }
    }

    // условие запаса не может требовать больше кадров, чем допускает бюджет или остаток до конца
    private bool CanResumeStrictVideo(PreparedVideoWindow window) =>
        window.ReadyFrames > 0 && (!PreviewSettings.BufferBeforeStrictResume || window.Ended ||
            window.ReadyFrames >= window.ResumeCapacity ||
            window.First is { } first && window.Through is { } through &&
            through.Milliseconds - Math.Max(strictPosition, first.Milliseconds) >= StrictResumeMilliseconds);

    // пауза затрагивает настоящий плеер, но сохраняет последний bitmap и логическую позицию
    private void HoldStrictVideo(VideoPlaybackSession playback, VLCState state) {
        strictBuffering = true;
        strictClockStartedAt = null;
        preparedVideo?.SetStrictWaiting(preparedGeneration, true);
        if (state == VLCState.Playing) {
            playback.SetPaused(true);
        }
    }

    // измеряем успешную выдачу выбранного вида после общего копирования пары в интерфейс
    private void ShowStrictFrame(PreparedVideoFrame frame) {
        long generation = preparedGeneration;
        PublishPreparedVideo(frame);
        preparedVideo?.RecordStrictPublication(generation, frame);
        if (!videoWantsToPlay) {
            preparedVideo?.StopStrictMeasurement(generation);
        }
        RecordVisibleOriginalFrame(videoWantsToPlay);
        RecordVisibleEditedFrame(videoWantsToPlay);
    }

    // округляем только показания шкалы, точный порядок сохраняют номер и исходный PTS
    private static long StrictTimelinePosition(double position, long duration) =>
        (long)Math.Clamp(position, 0, Math.Max(0, duration));

    // оставляем Play доступным для отмены намерения, даже пока декодер и звук стоят
    private void ReportStrictVideo(VideoPlaybackSession playback, VLCState state, long duration, string? notice) {
        bool ready = state is VLCState.Playing or VLCState.Paused or VLCState.Ended;
        UpdatePlaybackStatus(new(preparedPosition, duration, videoWantsToPlay, ready || videoWantsToPlay,
            duration > 0 && (playback.CanSeek || state == VLCState.Ended),
            notice ?? (preparedAtEnd ? "Конец видео" : DescribePreparedVideo())));
        if (ready) {
            UpdateVideoInformation(playback, duration);
        }
    }
}
