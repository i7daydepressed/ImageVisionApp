using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool canSaveCurrentVideoFrame;
    public bool CanSaveCurrentVideoFrame => canSaveCurrentVideoFrame;

    // обновляем кнопку редактора, когда меняется готовность остановленного вида
    private void RefreshVideoFrameSaveAvailability() =>
        SetProperty(ref canSaveCurrentVideoFrame, videoEditorVisible &&
            CanSaveVideoFrame(VideoEditorPreviewTabIndex == 0 ? VideoExportView.Original : VideoExportView.EditorOutput),
            nameof(CanSaveCurrentVideoFrame));

    // проверяем готовность вида для меню без кодирования снимка
    internal bool CanSaveVideoFrame(VideoExportView view) => GetPausedVideoFrameForSave(view) is not null;

    // выбираем показанный растр, когда плеер подтвердил остановленный кадр
    private Bitmap? GetPausedVideoFrameForSave(VideoExportView view) {
        if (!IsVideoOpen || mediaPreviewsDisposed ||
            videoWantsToPlay || videoWasPlaying || PlaybackStatus.IsPlaying || !PlaybackStatus.CanPlay ||
            videoPlayback?.State is not (VLCState.Paused or VLCState.Ended) ||
            videoRestarting || requestedVideoSeek is not null || videoFrameStep is not null ||
            preparedOpening is not null || livePreviewHandoff is not null ||
            videoEditorWaitingForFrame || videoEditorSourceError is not null ||
            VideoPreviewFrame is not { } original) {
            return null;
        }

        if (preparedSource is not null) {
            if (preparedPresentedFrame is not { } shown ||
                (videoPinnedFrame is { } pin ? shown != pin :
                    preparedStartPending || preparedNeedsSeek || preparedPauseOnCurrentPosition ||
                    preparedWaitingPosition is not null || preparedAtEnd && !shown.AtEnd)) {
                return null;
            }
        }
        else if (NeedsVerifiedLiveAnalysisFrame(original, PlaybackStatus.Position)) {
            return null;
        }

        return view switch {
            VideoExportView.Original => original,
            VideoExportView.EditorOutput when VideoEditorNotice is null &&
                (!videoEditorPlan.HasSteps || preparedSource is not null || videoEditorOutputFrame == videoEditorFrame) =>
                VideoEditorPreviewFrame,
            VideoExportView.AnalysisInput when videoAnalysisVisible &&
                TryResolveReadyVideoAnalysisInput(videoAnalysisDisplayedSource, out VideoAnalysisInput input) => input.Bitmap,
            VideoExportView.AnalysisOutput when videoAnalysisVisible => GetPausedAnalysisFrameForSave(),
            VideoExportView.SequenceInput when IsCurrentSequenceFrameForSave() ||
                ReferenceEquals(VideoForegroundInputPreview, original) => VideoForegroundInputPreview,
            VideoExportView.SequenceOutput when !HasActiveVideoSequenceEffect || IsCurrentSequenceFrameForSave() => VideoForegroundPreview,
            _ => null
        };
    }

    // на точном шаге различаем физические кадры, даже когда их PTS совпадают
    private bool IsCurrentSequenceFrameForSave() {
        if (videoForegroundFrame is not { } frame) {
            return false;
        }
        VideoFrameCursor? presented = videoPinnedFrame ??
            (preparedSource is not null ? preparedPresentedFrame : retainedVideoSeekCursor);
        long position = PlaybackStatus.Position;
        bool atEnd = presented?.AtEnd ?? (PlaybackStatus.Duration > 0 && position >= PlaybackStatus.Duration);
        return frame.Covers(new(position, atEnd) { Anchor = presented });
    }

    // отличаем готовую композицию от временной подложки и от результата предыдущего кадра
    private Bitmap? GetPausedAnalysisFrameForSave() {
        if (VideoAnalysisUsesSequence) {
            return IsCurrentSequenceFrameForSave() ? combinedVideoAnalysisResult : null;
        }
        if (ShowsFrameAnalysisPlayback) {
            return videoAnalysisPlaybackStamp is { } stamp &&
                (stamp.Exact ? stamp.Cursor == (preparedPresentedFrame ?? videoPinnedFrame) :
                    stamp.Cursor.Ordinal == VideoPreviewRevision && stamp.LiveFrame == videoEditorFrame)
                ? videoAnalysisPlaybackBitmap : null;
        }
        if (videoAnalysisApply is not null || videoAnalysisRequest is not null ||
            !TryResolveVideoAnalysisView(out VideoAnalysisView view) || view.WaitForEdited ||
            videoAnalysisBackground == VideoAnalysisBackground.Segmentation && videoAnalysisSegmentationPreview is null) {
            return null;
        }
        return videoAnalysisComposition ?? (!videoAnalysisViewRunning && videoAnalysisCompositionFailure is null ? view.Bitmap : null);
    }

    // кодируем снимок до открытия диалога, пока bitmap еще относится к выбранному кадру
    internal byte[]? CapturePausedVideoFramePng(VideoExportView view) {
        Dispatcher.UIThread.VerifyAccess();
        if (GetPausedVideoFrameForSave(view) is not { } frame) {
            return null;
        }

        using MemoryStream output = new();
        frame.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }
}
