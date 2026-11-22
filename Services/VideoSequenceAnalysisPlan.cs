using System;
using System.Threading;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed record VideoSequenceAnalysisPlan(MediaDocument Document, VideoEditorPlan Editor,
    VideoPreviewSettings Resources, VideoPipelineStage SequenceInput, VideoFrameAnalysisSettings Settings,
    AnalysisImageSource EdgeSource, AnalysisImageSource SegmentationSource, VideoPipelineStage Backdrop,
    bool SegmentedBackdrop, int PlateLimit) {

    private VideoPipelineStage Background => SegmentedBackdrop ? VideoPipelinePlan.FromAnalysisSource(SegmentationSource) : Backdrop;
    public bool NeedsEditing => Background == VideoPipelineStage.Editing ||
        Settings.EdgeMethod.HasValue && EdgeSource == AnalysisImageSource.Edited ||
        Settings.Clusters.HasValue && SegmentationSource == AnalysisImageSource.Edited;
    public bool RequiresTemporal => Background == VideoPipelineStage.VideoSequence ||
        Settings.EdgeMethod.HasValue && EdgeSource == AnalysisImageSource.VideoSequence ||
        Settings.Clusters.HasValue && SegmentationSource == AnalysisImageSource.VideoSequence;
    private bool HasMethods => Settings.EdgeMethod.HasValue || Settings.Clusters.HasValue || Settings.FastThreshold.HasValue || Settings.Plate;

    // временной результат наследует ориентацию собственного входа, размер квадрата сам по себе поворот не определяет
    private VideoEditorGeometry Geometry(PixelSize original, VideoPipelineStage stage) =>
        stage == VideoPipelineStage.Editing || stage == VideoPipelineStage.VideoSequence && SequenceInput == VideoPipelineStage.Editing
            ? Editor.GetGeometry(original) : new(original, 0);

    // композитор возвращает конечный растр в координатах выбранной подложки
    public PixelSize GetOutputSize(PixelSize original) => Geometry(original, Background).OutputSize;

    // проверяем одновременные буферы активных участков до запуска декодера
    public void ValidateBudget(PixelSize original) => GetResources(original);

    // одновременно учитываем исходные снимки, расчет видимых методов и пиксели конечного вида
    public (long EditorBytes, long AnalysisBytes, int Workers) GetResources(PixelSize original) {
        if (original.Width <= 0 || original.Height <= 0 || (long)original.Width * original.Height > int.MaxValue / 4) {
            throw new ArgumentOutOfRangeException(nameof(original));
        }
        long frameBytes = VideoEditorFrame.GetBufferBytes(original);
        long editBytes = NeedsEditing ? frameBytes + Editor.GetScratchBytes(original) : 0;
        if (editBytes > Resources.BudgetBytes) {
            throw new InvalidOperationException("Недостаточно бюджета редактора для анализа видеоряда");
        }
        long composition = HasMethods || Background == VideoPipelineStage.None ? frameBytes * 7 / 4 : frameBytes;
        if (Settings.FastThreshold.HasValue && Geometry(original, Background).ClockwiseQuarterTurns != 0) {
            composition += frameBytes * 2;
        }
        if (composition > Resources.AnalysisBudgetBytes) {
            throw new InvalidOperationException("Недостаточно бюджета анализа для композиции видеоряда");
        }
        if (!HasMethods) {
            return (editBytes, composition, 0);
        }
        var fit = VideoFrameAnalysisService.FitPipelineWorkers(original, NeedsEditing ? Editor.GetGeometry(original).OutputSize : null,
            RequiresTemporal ? Geometry(original, VideoPipelineStage.VideoSequence).OutputSize : null,
            EdgeSource, SegmentationSource, Settings,
            Resources.AnalysisMaximumWorkers, Resources.AnalysisBudgetBytes - composition);
        if (fit.Workers == 0) {
            throw new InvalidOperationException("Недостаточно бюджета анализа для выбранных методов видеоряда");
        }
        return (editBytes, composition + fit.Bytes, fit.Workers);
    }

    // вычитаем заимствованные bgra, которые уже учтены у источника и редактора
    internal long GetAnalysisWorkBytes(PixelSize original) {
        long reserved = GetResources(original).AnalysisBytes;
        return HasMethods ? reserved - VideoEditorFrame.GetBufferBytes(original) * (NeedsEditing ? 2 : 1) : reserved;
    }

    // заимствуем нужные растры одного физического кадра и возвращаем собственный результат
    public VideoEditorFrame Transform(TimedVideoInput original, VideoEditorFrame? temporal, CancellationToken token,
        VideoEditorFrame? preparedEdited = null) {
        token.ThrowIfCancellationRequested();
        PixelSize size = original.Pixels.Size;
        VideoEditorGeometry sequenceGeometry = Geometry(size, VideoPipelineStage.VideoSequence);
        if (RequiresTemporal && temporal is null) {
            throw new InvalidOperationException("Для выбранного входа анализа нужен результат видеоряда");
        }
        if (temporal is not null && temporal.Size != sequenceGeometry.OutputSize) {
            throw new InvalidOperationException("Размер видеоряда не соответствует геометрии выбранного входа");
        }
        int workers = GetResources(size).Workers;
        using VideoEditorFrame? ownEdited = NeedsEditing && preparedEdited is null ? original.Pixels.Copy() : null;
        if (ownEdited is not null) {
            Editor.Apply(ownEdited);
            token.ThrowIfCancellationRequested();
        }
        VideoEditorFrame? edited = NeedsEditing ? preparedEdited ?? ownEdited : null;
        if (edited is not null && edited.Size != Editor.GetGeometry(size).OutputSize) {
            throw new InvalidOperationException("Размер редакторского кадра не соответствует принятому плану");
        }
        VideoAnalysisFrameKey key = new(Document, AnalysisImageSource.Original, 0, original.Ordinal, 0, 0,
            (long)original.Time.Milliseconds, new(original.Time, original.Ordinal, original.IsLast), new(size, 0));
        VideoAnalysisFrameKey editedKey = key with { Source = AnalysisImageSource.Edited,
            Geometry = Editor.GetGeometry(size), PlanRevision = Editor.Revision };
        VideoAnalysisFrameKey sequenceKey = key with { Source = AnalysisImageSource.VideoSequence, Geometry = sequenceGeometry };
        var (backgroundKey, background) = Background switch {
            VideoPipelineStage.Original => (key, original.Pixels),
            VideoPipelineStage.Editing => (editedKey, edited),
            VideoPipelineStage.VideoSequence => (sequenceKey, temporal),
            VideoPipelineStage.None => (key, null),
            _ => throw new InvalidOperationException("Неизвестная подложка анализа")
        };
        if (!HasMethods && background is not null) {
            return background.Copy();
        }

        VideoFrameAnalysisResult? result = null;
        if (HasMethods) {
            result = VideoFrameAnalysisService.AnalyzePipeline(
                Capture(key, original.Pixels), edited is null ? null : Capture(editedKey, edited),
                temporal is null ? null : Capture(sequenceKey, temporal),
                EdgeSource, SegmentationSource, Settings, token, workers);
            token.ThrowIfCancellationRequested();
            if (result.PlateError is { } error) {
                throw new InvalidOperationException(error);
            }
        }
        return AnalysisCompositionService.ComposeVideoFrame(backgroundKey, background,
            SegmentedBackdrop ? result?.Segmentation : null, result?.Edges, result?.Keypoints, result?.Plate, PlateLimit, token);

        AnalysisInputSnapshot Capture(VideoAnalysisFrameKey frame, VideoEditorFrame pixels) =>
            new(frame.Source, frame.PlanRevision, new VideoAnalysisCapture(frame, pixels, null));
    }
}
