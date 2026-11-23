using System;
using System.IO;
using System.Threading;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal enum VideoExportView { Original, EditorOutput, AnalysisInput, AnalysisOutput, SequenceInput, SequenceOutput }
internal enum VideoExportEffect { Foreground, Mask, Background, MotionBlur, OpticalFlow }

internal sealed record VideoExportPlan(string Caption, MediaDocument Document, VideoFileIdentity Identity,
    VideoPipelinePlan Route, VideoPipelineStage Output, VideoEditorPlan Editor, VideoSequenceAnalysisPlan? Analysis,
    VideoPipelineStage SequenceInput, VideoExportEffect Effect, VideoBackgroundImage? Background, int BlurRadius) {
    // подложка определяет конечные размеры, даже если отдельные слои считались по повёрнутому кадру
    public PixelSize GetOutputSize(PixelSize sourceSize) => GetStageSize(Output, sourceSize);

    internal PixelSize GetStageSize(VideoPipelineStage stage, PixelSize sourceSize) => stage switch {
        VideoPipelineStage.Original => sourceSize,
        VideoPipelineStage.Editing => Editor.GetGeometry(sourceSize).OutputSize,
        VideoPipelineStage.Analysis => Analysis!.GetOutputSize(sourceSize),
        VideoPipelineStage.VideoSequence => GetStageSize(SequenceInput, sourceSize),
        _ => throw new InvalidOperationException("Неизвестный участок экспорта")
    };

    // предыстория и подогнанный фон принадлежат только этому проходу от начала ролика
    public VideoExportProcessor OpenProcessor(PixelSize sourceSize, CancellationToken token = default) => new(this, sourceSize, token);
}

internal sealed class VideoExportProcessor : IDisposable {
    private readonly VideoExportPlan plan;
    private readonly PixelSize sourceSize;
    private readonly VideoForegroundModel? foreground;
    private readonly VideoOpticalFlowModel? flow;
    private readonly VideoBackgroundRaster? background;
    private long nextOrdinal;
    private VideoPresentationTime? nextTime;
    private bool disposed;

    internal VideoExportProcessor(VideoExportPlan plan, PixelSize sourceSize, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        this.plan = plan;
        this.sourceSize = sourceSize;
        if (plan.Route.Stages.Contains(VideoPipelineStage.VideoSequence)) {
            PixelSize size = plan.GetStageSize(plan.SequenceInput, sourceSize);
            if (plan.Effect == VideoExportEffect.OpticalFlow) {
                flow = new(size);
            }
            else {
                if (plan.Effect == VideoExportEffect.Background) {
                    background = (plan.Background ?? throw new InvalidOperationException("Не выбран файл фона")).PrepareCover(size, token);
                }
                foreground = new(size);
            }
        }
    }

    // выполняем только предков выбранного выхода и передаём писателю собственный буфер этого кадра
    public VideoEditorFrame Render(TimedVideoInput input, CancellationToken token) {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (input.Pixels.Size != sourceSize || input.Ordinal != nextOrdinal || !input.Time.IsKnown ||
            (nextOrdinal == 0 ? input.Time.Value != 0 : nextTime is not { } expected || expected.CompareTo(input.Time) != 0) ||
            input.Until is { } until && (!until.IsKnown || until.CompareTo(input.Time) < 0)) {
            throw new InvalidDataException("Нарушен порядок, время или размер исходных кадров экспорта");
        }
        VideoEditorFrame? edited = null;
        VideoEditorFrame? analysis = null;
        VideoEditorFrame? sequence = null;
        VideoEditorFrame? transferred = null;
        try {
            foreach (VideoPipelineStage stage in plan.Route.Stages) {
                token.ThrowIfCancellationRequested();
                switch (stage) {
                    case VideoPipelineStage.Editing:
                        edited = input.Pixels.Copy();
                        plan.Editor.Apply(edited);
                        break;
                    case VideoPipelineStage.Analysis:
                        analysis = plan.Analysis!.Transform(input, sequence, token, edited, analysisWorkers: 1);
                        break;
                    case VideoPipelineStage.VideoSequence:
                        sequence = RenderSequence(input, Pixels(plan.SequenceInput), token);
                        break;
                }
            }
            token.ThrowIfCancellationRequested();
            VideoEditorFrame result = Pixels(plan.Output);
            if (result.Size != plan.GetOutputSize(sourceSize)) {
                throw new InvalidDataException("Геометрия готового вида не совпадает с планом экспорта");
            }
            transferred = ReferenceEquals(result, input.Pixels) ? result.Copy() : result;
            nextTime = input.Until;
            nextOrdinal++;
            return transferred;
        }
        finally {
            if (!ReferenceEquals(edited, transferred)) { edited?.Dispose(); }
            if (!ReferenceEquals(analysis, transferred)) { analysis?.Dispose(); }
            if (!ReferenceEquals(sequence, transferred)) { sequence?.Dispose(); }
        }

        VideoEditorFrame Pixels(VideoPipelineStage stage) => stage switch {
            VideoPipelineStage.Original => input.Pixels,
            VideoPipelineStage.Editing when edited is not null => edited,
            VideoPipelineStage.Analysis when analysis is not null => analysis,
            VideoPipelineStage.VideoSequence when sequence is not null => sequence,
            _ => throw new InvalidOperationException("Не готов обязательный участок выбранного вида")
        };
    }

    // рассчитываем модель по каждому физическому кадру и собираем тот же вид, что в предпросмотре
    private VideoEditorFrame RenderSequence(TimedVideoInput input, VideoEditorFrame pixels, CancellationToken token) {
        VideoForegroundFrame frame = (flow is null ? foreground!.Process(input, pixels, true, token) :
            flow.Process(input, pixels, true, token)) ?? throw new InvalidDataException("Не готов результат видеоряда");
        bool blur = plan.Effect == VideoExportEffect.MotionBlur;
        if (blur) {
            frame = frame with { MotionBlur = VideoMotionBlurRaster.Create(frame, plan.BlurRadius, token) };
        }
        return VideoEditorFrame.FromForeground(frame, plan.Effect == VideoExportEffect.Mask, token, background, blur);
    }

    // освобождаем накопленное состояние независимо от исхода записи
    public void Dispose() {
        if (disposed) { return; }
        disposed = true;
        flow?.Dispose();
        foreground?.Dispose();
    }
}
