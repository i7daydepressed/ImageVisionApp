using System;
using System.Threading;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed class VideoForegroundInputPlan {
    private readonly MediaDocument document;
    private readonly VideoPipelineStage source;
    private readonly VideoEditorPlan editor;
    private readonly VideoPreviewSettings resources;
    private readonly VideoFrameAnalysisSettings settings;
    private readonly AnalysisImageSource edgeSource;
    private readonly AnalysisImageSource segmentationSource;
    private readonly VideoPipelineStage backdrop;
    private readonly bool segmentedBackdrop;
    private readonly int plateLimit;

    // сохраняем настройки всего прохода, сегментацию считаем только для выбранной подложки
    public VideoForegroundInputPlan(MediaDocument document, VideoPipelineStage source, VideoEditorPlan editor,
        VideoPreviewSettings resources, VideoFrameAnalysisSettings analysisSettings, AnalysisImageSource edgeSource,
        AnalysisImageSource segmentationSource, VideoPipelineStage backdrop, bool segmentedBackdrop, int plateLimit) {
        if (source is not (VideoPipelineStage.Original or VideoPipelineStage.Editing or VideoPipelineStage.Analysis)) {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        this.document = document;
        this.source = source;
        this.editor = editor;
        this.resources = resources;
        this.edgeSource = edgeSource;
        this.segmentationSource = segmentationSource;
        this.segmentedBackdrop = segmentedBackdrop;
        this.settings = segmentedBackdrop ? analysisSettings : analysisSettings with { Clusters = null };
        this.backdrop = source == VideoPipelineStage.Analysis && segmentedBackdrop
            ? VideoPipelinePlan.FromAnalysisSource(segmentationSource) : backdrop;
        this.plateLimit = plateLimit;
        if (source == VideoPipelineStage.Analysis &&
            (this.backdrop is not (VideoPipelineStage.Original or VideoPipelineStage.Editing or VideoPipelineStage.None) ||
             segmentedBackdrop && analysisSettings.Clusters is null ||
             settings.EdgeMethod is not null && edgeSource is not (AnalysisImageSource.Original or AnalysisImageSource.Edited))) {
            throw new ArgumentException("Не определен совместимый вход композиции анализа");
        }
    }

    private bool HasMethods => settings.EdgeMethod is not null || settings.Clusters is not null ||
        settings.FastThreshold is not null || settings.Plate;
    private bool UsesEditedBackdrop => source == VideoPipelineStage.Editing ||
        source == VideoPipelineStage.Analysis && backdrop == VideoPipelineStage.Editing;
    public bool NeedsEditing => UsesEditedBackdrop || source == VideoPipelineStage.Analysis &&
        (settings.EdgeMethod is not null && edgeSource == AnalysisImageSource.Edited ||
         settings.Clusters is not null && segmentationSource == AnalysisImageSource.Edited);

    public bool UsesAnalysis => source == VideoPipelineStage.Analysis;

    // размеры модели определяет растр подложки, включая поворот до сегментации
    public PixelSize GetOutputSize(PixelSize original) =>
        UsesEditedBackdrop ? editor.GetGeometry(original).OutputSize : original;

    // проверяем лимиты участков до открытия последовательного прохода
    public void ValidateBudget(PixelSize original) => GetResources(original);

    // оставляем место для композиции и переноса точек на повернутую подложку
    public (long EditorBytes, long AnalysisBytes, int Workers) GetResources(PixelSize original) {
        long frameBytes = VideoEditorFrame.GetBufferBytes(original);
        long editorBytes = NeedsEditing ? frameBytes + editor.GetScratchBytes(original) : 0;
        if (editorBytes > resources.BudgetBytes) {
            throw new InvalidOperationException("Недостаточно бюджета редактора для входа видеоряда");
        }
        if (!UsesAnalysis) {
            return (editorBytes, 0, 0);
        }
        if (!HasMethods && backdrop != VideoPipelineStage.None) {
            if (frameBytes > resources.AnalysisBudgetBytes) {
                throw new InvalidOperationException("Недостаточно бюджета анализа для подложки видеоряда");
            }
            return (editorBytes, frameBytes, 0);
        }
        long compositionBytes = frameBytes + frameBytes / 4 * 3;
        if (settings.FastThreshold is not null && UsesEditedBackdrop && editor.GetGeometry(original).ClockwiseQuarterTurns != 0) {
            compositionBytes += frameBytes / 4 * 8;
        }
        long available = resources.AnalysisBudgetBytes - compositionBytes;
        if (available < 0) {
            throw new InvalidOperationException("Недостаточно бюджета анализа для композиции видеоряда");
        }
        if (!HasMethods) {
            return (editorBytes, compositionBytes, 0);
        }
        PixelSize? editedSize = NeedsEditing ? editor.GetGeometry(original).OutputSize : null;
        (int workers, long bytes) = VideoFrameAnalysisService.FitWorkers(original, editedSize, edgeSource, segmentationSource,
            settings, resources.AnalysisMaximumWorkers, available);
        if (workers == 0) {
            throw new InvalidOperationException("Недостаточно бюджета анализа для выбранных методов видеоряда");
        }
        return (editorBytes, checked(compositionBytes + bytes), workers);
    }

    // вход принадлежит читателю, наружу передаем отдельный кадр с пикселями выбранного участка
    public VideoEditorFrame Transform(TimedVideoInput input, CancellationToken token, VideoEditorFrame? preparedEdited = null) {
        token.ThrowIfCancellationRequested();
        VideoEditorFrame original = input.Pixels;
        int workers = GetResources(original.Size).Workers;
        if (source != VideoPipelineStage.Analysis || !HasMethods && backdrop != VideoPipelineStage.None) {
            return UsesEditedBackdrop && preparedEdited is not null
                ? preparedEdited.Copy() : CopyInput(original, UsesEditedBackdrop, token);
        }

        using VideoEditorFrame? owned = preparedEdited is null && NeedsEditing ? CopyInput(original, true, token) : null;
        VideoEditorFrame? edited = preparedEdited ?? owned;
        VideoAnalysisFrameKey originalKey = new(document, AnalysisImageSource.Original, 0, input.Ordinal, 0, 0,
            (long)input.Time.Milliseconds, new(input.Time, input.Ordinal, input.IsLast), new(original.Size, 0));
        VideoAnalysisFrameKey editedKey = originalKey with {
            Source = AnalysisImageSource.Edited, PlanRevision = editor.Revision, Geometry = editor.GetGeometry(original.Size)
        };
        VideoFrameAnalysisResult? result = null;
        if (HasMethods) {
            AnalysisInputSnapshot originalInput = new(AnalysisImageSource.Original, 0,
                new VideoAnalysisCapture(originalKey, original, null));
            AnalysisInputSnapshot? editedInput = edited is null ? null :
                new(AnalysisImageSource.Edited, editor.Revision, new VideoAnalysisCapture(editedKey, edited, null));
            result = VideoFrameAnalysisService.Analyze(originalInput, editedInput, edgeSource, segmentationSource,
                settings, token, workers);
            token.ThrowIfCancellationRequested();
            if (result.PlateError is { } error) {
                throw new InvalidOperationException(error);
            }
        }
        VideoEditorFrame? background = backdrop switch {
            VideoPipelineStage.Original => original,
            VideoPipelineStage.Editing => edited,
            _ => null
        };
        return AnalysisCompositionService.ComposeVideoFrame(UsesEditedBackdrop ? editedKey : originalKey, background,
            segmentedBackdrop ? result?.Segmentation : null, result?.Edges, result?.Keypoints, result?.Plate, plateLimit, token);
    }

    // редактор меняет только свою копию, при отмене до передачи буфер освобождаем здесь
    private VideoEditorFrame CopyInput(VideoEditorFrame original, bool applyEditor, CancellationToken token) {
        VideoEditorFrame copy = original.Copy();
        try {
            if (applyEditor && editor.HasSteps) {
                editor.Apply(copy);
            }
            token.ThrowIfCancellationRequested();
            return copy;
        }
        catch {
            copy.Dispose();
            throw;
        }
    }
}
