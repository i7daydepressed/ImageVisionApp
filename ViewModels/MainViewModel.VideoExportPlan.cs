using System;
using System.Collections.Generic;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    // фиксируем выбранную подвкладку и принятые параметры до открытия фоновой задачи
    internal VideoExportPlan CaptureVideoExportPlan(VideoExportView view) {
        Dispatcher.UIThread.VerifyAccess();
        if (CurrentDocument is not { Kind: MediaKind.Video, SourcePath: { } path } document ||
            videoFileIdentity is not { } identity) {
            throw new InvalidOperationException("Для экспорта нужен подтверждённый исходный видеофайл");
        }
        identity.EnsureCurrent(path);

        VideoPipelineStage sequenceInput = HasActiveVideoSequenceEffect ? VideoForegroundSource : VideoPipelineStage.Original;
        var (caption, output) = view switch {
            VideoExportView.Original => ("Оригинал", VideoPipelineStage.Original),
            VideoExportView.EditorOutput => ("Редактирование · Изменённое", VideoPipelineStage.Editing),
            VideoExportView.AnalysisInput => ("Анализ · Вход", VideoPipelinePlan.FromAnalysisSource(videoAnalysisDisplayedSource)),
            VideoExportView.AnalysisOutput => ("Анализ · Результат", VideoPipelineStage.Analysis),
            VideoExportView.SequenceInput => ("Работа с видеорядом · Вход", sequenceInput),
            VideoExportView.SequenceOutput => ("Работа с видеорядом · Результат",
                HasActiveVideoSequenceEffect ? VideoPipelineStage.VideoSequence : VideoPipelineStage.Original),
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        };
        bool needsAnalysis = output == VideoPipelineStage.Analysis ||
            output == VideoPipelineStage.VideoSequence && sequenceInput == VideoPipelineStage.Analysis;
        VideoSequenceAnalysisPlan? analysis = needsAnalysis ? CaptureVideoSequenceAnalysisPlan(document, PreviewSettings) : null;
        VideoPipelineStage inputs = VideoPipelineStage.Original;
        if (analysis?.NeedsEditing == true) { inputs |= VideoPipelineStage.Editing; }
        if (analysis?.RequiresTemporal == true) {
            if (!HasActiveVideoSequenceEffect) {
                throw new InvalidOperationException("Для выбранного результата анализа включите метод видеоряда");
            }
            inputs |= VideoPipelineStage.VideoSequence;
        }
        if (!VideoPipelinePlan.TryCreate(inputs, sequenceInput, output, out VideoPipelinePlan? route, out string? error)) {
            throw new InvalidOperationException(error);
        }

        VideoExportEffect effect = VideoForegroundViewIndex switch {
            1 => VideoExportEffect.Mask,
            ReplacedBackgroundView => VideoExportEffect.Background,
            MovingBlurView => VideoExportEffect.MotionBlur,
            OpticalFlowView => VideoExportEffect.OpticalFlow,
            _ => VideoExportEffect.Foreground
        };
        List<string> details = [caption];
        if (view is VideoExportView.AnalysisInput or VideoExportView.SequenceInput ||
            view == VideoExportView.SequenceOutput && output == VideoPipelineStage.Original) {
            details.Add($"Источник: {VideoPipelinePlan.GetStageName(output)}");
        }
        if (analysis is not null) {
            details.Add(DescribeExportAnalysis(analysis));
        }
        if (route.Stages.Contains(VideoPipelineStage.VideoSequence)) {
            string kind = effect switch {
                VideoExportEffect.Mask => "Маска",
                VideoExportEffect.Background => $"Заменённый фон ({VideoBackgroundImageName})",
                VideoExportEffect.MotionBlur => $"Размытие движущихся областей (радиус {VideoMovingBlurRadius})",
                VideoExportEffect.OpticalFlow => "Оптический поток",
                _ => "Передний план"
            };
            details.Add($"Видеоряд: {kind}. Источник: {VideoPipelinePlan.GetStageName(sequenceInput)}");
        }
        return new(string.Join(Environment.NewLine, details), document, identity, route, output, videoEditorPlan,
            analysis, sequenceInput, effect, route.Stages.Contains(VideoPipelineStage.VideoSequence) &&
                effect == VideoExportEffect.Background ? videoBackgroundImage : null,
            VideoMovingBlurRadius);
    }

    // показываем подложку и фактические источники всех включённых слоёв в подтверждении записи
    private static string DescribeExportAnalysis(VideoSequenceAnalysisPlan analysis) {
        string background = analysis.SegmentedBackdrop ? $"K-means ({Source(analysis.SegmentationSource)})" :
            analysis.Backdrop == VideoPipelineStage.None ? "чёрная" : VideoPipelinePlan.GetStageName(analysis.Backdrop);
        List<string> layers = [];
        if (analysis.Settings.EdgeMethod is { } edge) {
            string method = edge == AnalysisEdgeMethod.Canny ? "Canny" : "Робертс";
            layers.Add($"{method} ({Source(analysis.EdgeSource)})");
        }
        if (analysis.Settings.FastThreshold.HasValue) { layers.Add("FAST (оригинал)"); }
        if (analysis.Settings.Plate) {
            string count = analysis.PlateLimit == int.MaxValue ? "все" : $"до {analysis.PlateLimit}";
            layers.Add($"области номера (оригинал, {count})");
        }
        string visible = layers.Count == 0 ? "нет" : string.Join(", ", layers);
        return $"Подложка анализа: {background}. Слои: {visible}";

        static string Source(AnalysisImageSource input) =>
            VideoPipelinePlan.GetStageName(VideoPipelinePlan.FromAnalysisSource(input));
    }
}
