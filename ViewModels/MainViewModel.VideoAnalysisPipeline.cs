using System;
using System.Collections.Generic;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private readonly record struct VideoAnalysisPipeline(VideoPipelinePlan Queue,
        AnalysisImageSource? EdgeSource, AnalysisImageSource? SegmentationSource, bool Fast, bool Plate);

    internal bool VideoAnalysisUsesSequence => IsVideoOpen && (videoAnalysisBackground == VideoAnalysisBackground.VideoSequence ||
        isEdgesAnalysisEnabled && videoEdgeSource == AnalysisImageSource.VideoSequence ||
        IsSegmentationAnalysisEnabled && videoSegmentationSource == AnalysisImageSource.VideoSequence);

    // проверяем и сохраненные входы выключенных методов, чтобы их включение не замыкало очередь
    private bool TryValidateVideoSequenceSources(VideoPipelineStage candidate, out string? error) =>
        TryValidateAnalysisSources(videoEdgeSource, videoSegmentationSource, videoAnalysisBackground, candidate, out error);

    // при неудаче оставляем прежние источники и показываем тот же путь цикла, что проверил граф
    private bool AcceptVideoAnalysisChoice(AnalysisImageSource edge, AnalysisImageSource segmentation, VideoAnalysisBackground background) {
        if (TryValidateAnalysisSources(edge, segmentation, background, VideoSequenceInputs, out string? error)) {
            return true;
        }
        AnalysisStatusMessage = error!;
        return false;
    }

    // проверяем предполагаемую связь до смены состояния и отмены нужного пользователю расчета
    private static bool TryValidateAnalysisSources(AnalysisImageSource edge, AnalysisImageSource segmentation,
        VideoAnalysisBackground background, VideoPipelineStage sequenceInputs, out string? error) {
        VideoPipelineStage inputs = VideoPipelinePlan.FromAnalysisSource(edge) | VideoPipelinePlan.FromAnalysisSource(segmentation) |
            GetAnalysisBackdropStage(background, segmentation);
        return VideoPipelinePlan.TryCreate(inputs, sequenceInputs, VideoPipelineStage.Analysis, out _, out error);
    }

    // источник сегментированной подложки задает ее систему координат
    private static VideoPipelineStage GetAnalysisBackdropStage(VideoAnalysisBackground background, AnalysisImageSource segmentation) =>
        background switch {
            VideoAnalysisBackground.Original => VideoPipelineStage.Original,
            VideoAnalysisBackground.Edited => VideoPipelineStage.Editing,
            VideoAnalysisBackground.Segmentation => VideoPipelinePlan.FromAnalysisSource(segmentation),
            VideoAnalysisBackground.VideoSequence => VideoPipelineStage.VideoSequence,
            _ => VideoPipelineStage.None
        };

    // фиксируем все настройки одного последовательного прохода без обращения рабочего потока к экрану
    private VideoSequenceAnalysisPlan CaptureVideoSequenceAnalysisPlan(MediaDocument document, VideoPreviewSettings resources) {
        if (!AreAllAnalysisParametersValid) {
            throw new InvalidOperationException("Проверьте параметры выбранных методов анализа");
        }
        VideoAnalysisPipeline pipeline = CaptureVideoAnalysisPipeline();
        return new(document, videoEditorPlan, resources, VideoSequenceOutputGeometrySource, CaptureVideoFrameAnalysisSettings(pipeline),
            pipeline.EdgeSource ?? AnalysisImageSource.Original, pipeline.SegmentationSource ?? AnalysisImageSource.Original,
            GetAnalysisBackdropStage(videoAnalysisBackground, videoSegmentationSource),
            videoAnalysisBackground == VideoAnalysisBackground.Segmentation, VisiblePlateRegionLimit);
    }

    private string? videoAnalysisPipelineDescription;
    public string? VideoAnalysisPipelineDescription {
        get => videoAnalysisPipelineDescription;
        private set => SetProperty(ref videoAnalysisPipelineDescription, value);
    }

    // строим подпись каждого активного входа и отдельно указываем подложку результата
    private void UpdateVideoAnalysisPipelineDescription() {
        if (!IsVideoOpen || !videoAnalysisResultTabSelected) {
            VideoAnalysisPipelineDescription = null;
            return;
        }

        VideoAnalysisPipeline pipeline = CaptureVideoAnalysisPipeline();
        List<string> original = [];
        List<string> edited = [];
        List<string> sequence = [];
        if (pipeline.EdgeSource is { } edge) {
            Branch(edge).Add(IsCannyEdgeMethod ? "Canny" : "Робертс");
        }
        if (pipeline.SegmentationSource is { } segmentation) {
            Branch(segmentation).Add("K-means");
        }
        if (pipeline.Fast) {
            original.Add("FAST");
        }
        if (pipeline.Plate) {
            original.Add("Область номера");
        }
        List<string> lines = [];
        if (edited.Count > 0) {
            lines.Add(DescribeCalculationBranch(VideoPipelineStage.Editing, edited));
        }
        if (original.Count > 0) {
            lines.Add(DescribeCalculationBranch(VideoPipelineStage.Original, original));
        }
        if (sequence.Count > 0) {
            lines.Add(DescribeCalculationBranch(VideoPipelineStage.VideoSequence, sequence));
        }
        string backdrop = videoAnalysisBackground switch {
            VideoAnalysisBackground.Original => DescribePaths(VideoPipelineStage.Original),
            VideoAnalysisBackground.Edited => DescribePaths(VideoPipelineStage.Editing),
            VideoAnalysisBackground.VideoSequence => DescribePaths(VideoPipelineStage.VideoSequence),
            VideoAnalysisBackground.Segmentation => $"{DescribePaths(VideoPipelinePlan.FromAnalysisSource(
                pipeline.SegmentationSource ?? AnalysisImageSource.Original))} → Сегментация",
            _ => "Без изображения"
        };
        lines.Add($"Подложка: {backdrop}");
        VideoAnalysisPipelineDescription = string.Join(Environment.NewLine, lines);

        List<string> Branch(AnalysisImageSource source) => source switch {
            AnalysisImageSource.Edited => edited, AnalysisImageSource.VideoSequence => sequence, _ => original
        };

        // ветвь метода заканчивается анализом и использует фактический путь входного участка
        string DescribeCalculationBranch(VideoPipelineStage source, List<string> methods) =>
            $"Расчёт: {DescribePaths(source)} → {VideoPipelinePlan.GetStageName(VideoPipelineStage.Analysis)} ({string.Join(", ", methods)})";

        // перечисляем подтвержденные пути источника с сохранением их ветвления
        string DescribePaths(VideoPipelineStage source) => string.Join(" или ", pipeline.Queue.DescribePathsTo(source));
    }

    // берем те же методы и параметры для ручного анализа и кадров комбинированной очереди
    private VideoFrameAnalysisSettings CaptureVideoFrameAnalysisSettings(VideoAnalysisPipeline pipeline) {
        TryGetCannyThresholds(out double lower, out double upper);
        TryGetSegmentationClusterCount(out int clusters);
        TryGetFastThreshold(out int threshold);
        return new(pipeline.EdgeSource is not null ? selectedEdgeMethod : null, lower, upper,
            pipeline.SegmentationSource is not null ? clusters : null, pipeline.Fast ? threshold : null,
            fastNonmaxSuppression) { Plate = pipeline.Plate };
    }

    // фиксируем маршруты включенных методов тем же снимком для подготовки кадра и подписи
    private VideoAnalysisPipeline CaptureVideoAnalysisPipeline() {
        AnalysisImageSource? edge = isEdgesAnalysisEnabled ? videoEdgeSource : null;
        AnalysisImageSource? segmentation = IsSegmentationAnalysisEnabled ? videoSegmentationSource : null;
        VideoPipelineStage inputs = isKeypointAnalysisEnabled || isPlateAnalysisEnabled
            ? VideoPipelineStage.Original : VideoPipelineStage.None;
        if (edge is { } edgeSource) {
            inputs |= VideoPipelinePlan.FromAnalysisSource(edgeSource);
        }
        if (segmentation is { } segmentationSource) {
            inputs |= VideoPipelinePlan.FromAnalysisSource(segmentationSource);
        }
        inputs |= GetAnalysisBackdropStage(videoAnalysisBackground, videoSegmentationSource);
        if (!VideoPipelinePlan.TryCreate(inputs, VideoSequenceInputs, VideoPipelineStage.Analysis,
            out VideoPipelinePlan? plan, out string? error)) {
            throw new InvalidOperationException(error);
        }
        return new(plan, edge, segmentation, isKeypointAnalysisEnabled, isPlateAnalysisEnabled);
    }
}
