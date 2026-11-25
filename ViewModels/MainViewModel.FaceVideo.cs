using System;
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

public partial class MainViewModel {
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
