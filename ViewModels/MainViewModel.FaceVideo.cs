using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

internal readonly record struct FaceVideoPreview(MediaDocument Document, Bitmap Bitmap, int Revision, long SourceRevision,
    int PlaybackGeneration, long PreparationGeneration, VideoFrameCursor? Cursor, bool IsPlaying) {
    // новые живые кадры принадлежат тому же запуску до перемотки, паузы или смены источника
    public bool SameRun(FaceVideoPreview other) =>
        ReferenceEquals(Document, other.Document) && SourceRevision == other.SourceRevision &&
        PlaybackGeneration == other.PlaybackGeneration && PreparationGeneration == other.PreparationGeneration &&
        IsPlaying == other.IsPlaying;

    // ссылка на bitmap может сохраняться после замены его пикселей, поэтому проверяем ревизию
    public bool SameFrame(FaceVideoPreview other) =>
        SameRun(other) && Revision == other.Revision && Cursor == other.Cursor && ReferenceEquals(Bitmap, other.Bitmap);
}

internal readonly record struct PresentedFaceVideo(Bitmap Bitmap, FaceVideoResult Result, FaceVideoWork Owner,
    VideoFrameCursor Cursor, long Generation, int Revision);

public partial class MainViewModel {
    private readonly FaceVideoBudget faceVideoBudget = new();
    private FaceRecognitionMode? faceVideoMode;
    private MediaDocument? faceVideoDocument;
    private FaceVideoWork? faceVideoWork;
    private Task retiredFaceWork = Task.CompletedTask;
    private PresentedFaceVideo? presentedFaceVideo;
    internal FaceVideoWork? CurrentFaceVideoWork => faceVideoWork;
    internal bool IsFaceVideoPrepared => preparedSource is not null || preparedOpening is not null;
    internal bool HasFaceVideoWork => faceVideoWork is not null || !retiredFaceWork.IsCompleted;

    // разрешаем результату только одну пару из видео и режима, выключение также отзывает владельца
    internal void SetFaceVideoMode(FaceRecognitionMode? mode, bool settingsChanged = false) {
        MediaDocument? document = mode is not null && CurrentDocument is { Kind: MediaKind.Video } video ? video : null;
        if (!settingsChanged && faceVideoMode == mode && ReferenceEquals(faceVideoDocument, document)) {
            return;
        }
        bool rebuildPrepared = faceVideoWork is not null || document is not null;
        faceVideoMode = mode;
        faceVideoDocument = document;
        if (faceVideoWork is { } previous) {
            previous.Dispose();
            retiredFaceWork = Task.WhenAll(retiredFaceWork, previous.Drained);
        }
        faceVideoWork = document is not null && mode is { } selected ? new FaceVideoWork(faceVideoBudget,
            PreviewSettings.FaceMaximumWorkers, PreviewSettings.FaceBudgetBytes, selected, retiredFaceWork) : null;
        faceVideoBudget.ResetPeaks();
        ClearPreparedFaceVideo();
        OnPropertyChanged(nameof(CurrentFaceVideoWork));
        if (rebuildPrepared && preparedSource is not null && videoFrameStep is null && !mediaPreviewsDisposed &&
            !RecalculatePinnedPreparedFrame()) {
            RequestPreparedVideo(preparedPosition, preparedAtEnd, preservePlay: true,
                strictAnchor: IsStrictVideo && !preparedAtEnd ? preparedPresentedFrame : null);
        }
    }

    // счетчики готовы и при открытом диалоге настроек, без чтения внутренних сетевых буферов
    internal FaceVideoLoad ReadFaceVideoLoad() => faceVideoBudget.Snapshot;

    // закрытие ждет уже отмененные модели до их фактического освобождения
    internal Task DrainFaceVideoWorkAsync() => retiredFaceWork;

    // снимаем заимствованные пиксели до скрытия, повторной записи или освобождения общего bitmap
    private void ClearPreparedFaceVideo() {
        if (presentedFaceVideo is not null) {
            presentedFaceVideo = null;
            OnPropertyChanged(nameof(PreparedFaceVideo));
        }
    }

    // отображаем только слой кадра, который уже выдан общим подготовителем
    internal PresentedFaceVideo? PreparedFaceVideo => presentedFaceVideo is { } face &&
        ReferenceEquals(face.Owner, faceVideoWork) && face.Generation == preparedGeneration &&
        preparedFrom is not null && preparedPresentedFrame == face.Cursor &&
        VideoPreviewRevision == face.Revision && ReferenceEquals(preparedOriginal, face.Bitmap) &&
        requestedVideoSeek is null && videoFrameStep is null ? face : null;

    // публикуем рамки до уведомлений о смене исходного bitmap
    private void PublishPreparedFaceVideo(PreparedVideoFrame frame, Bitmap original) {
        presentedFaceVideo = frame.FaceResult is { } result && frame.FaceOwner is { } owner &&
            ReferenceEquals(owner, faceVideoWork) && preparedPresentedFrame is { } cursor
            ? new PresentedFaceVideo(original, result, owner, cursor, preparedGeneration, VideoPreviewRevision) : null;
        if (presentedFaceVideo is not null && frame.AcceptedAt is { } acceptedAt) {
            faceVideoBudget.Presented(acceptedAt);
        }
        OnPropertyChanged(nameof(PreparedFaceVideo));
    }

    // возвращаем исходный показанный кадр для синхронной копии на UI, ожидая общий переход
    internal FaceVideoPreview? ReadFaceVideoPreview() {
        bool playing = videoWantsToPlay || PlaybackStatus.IsPlaying;
        if (CurrentDocument is not { Kind: MediaKind.Video } document || mediaPreviewsDisposed ||
            requestedVideoSeek is not null || videoFrameStep is not null || livePreviewHandoff is not null ||
            preparedOpening is not null || videoRestarting || videoEditorWaitingForFrame ||
            videoEditorSourceError is not null || VideoPreviewFrame is not { } original ||
            NeedsVerifiedLiveAnalysisFrame(original, PlaybackStatus.Position)) {
            return null;
        }

        if (preparedSource is not null && (preparedPresentedFrame is null || preparedStartPending ||
            !playing && (preparedNeedsSeek || preparedPauseOnCurrentPosition || preparedWaitingPosition is not null))) {
            return null;
        }

        return new FaceVideoPreview(document, original, VideoPreviewRevision, videoEditorSource,
            videoPlaybackGeneration, preparedSource is not null ? preparedGeneration : 0,
            preparedSource is not null ? preparedPresentedFrame : videoPinnedFrame ?? retainedVideoSeekCursor, playing);
    }

    // после мгновенной паузы в живой перемотке подтверждаем кадр через общий точный переход
    internal void ResolveFacePreviewAfterLivePause() {
        if (preparedSource is null && preparedOpening is null && livePreviewHandoff is null &&
            requestedVideoSeek is null && videoFrameStep is null && !videoWantsToPlay && !PlaybackStatus.IsPlaying &&
            videoEditorSourceError is null && PlaybackStatus.CanSeek && videoPlayback?.State is (VLCState.Paused or VLCState.Ended) &&
            VideoPreviewFrame is { } original && NeedsVerifiedLiveAnalysisFrame(original, PlaybackStatus.Position)) {
            RequestVideoSeek(PlaybackStatus.Position);
        }
    }
}
