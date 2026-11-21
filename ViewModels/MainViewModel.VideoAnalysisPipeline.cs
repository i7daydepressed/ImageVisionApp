using System;
using System.Collections.Generic;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private readonly record struct VideoAnalysisPipeline(VideoPipelinePlan Queue,
        AnalysisImageSource? EdgeSource, AnalysisImageSource? SegmentationSource, bool Fast);

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
        if (pipeline.EdgeSource is { } edge) {
            (edge == AnalysisImageSource.Edited ? edited : original).Add(IsCannyEdgeMethod ? "Canny" : "Робертс");
        }
        if (pipeline.SegmentationSource is { } segmentation) {
            (segmentation == AnalysisImageSource.Edited ? edited : original).Add("K-means");
        }
        if (pipeline.Fast) {
            original.Add("FAST");
        }
        List<string> lines = [];
        if (edited.Count > 0) {
            lines.Add(DescribeCalculationBranch(VideoPipelineStage.Editing, edited));
        }
        if (original.Count > 0) {
            lines.Add(DescribeCalculationBranch(VideoPipelineStage.Original, original));
        }
        string backdrop = videoAnalysisBackground switch {
            VideoAnalysisBackground.Original => DescribePaths(VideoPipelineStage.Original),
            VideoAnalysisBackground.Edited => DescribePaths(VideoPipelineStage.Editing),
            VideoAnalysisBackground.Segmentation => $"{DescribePaths(VideoPipelinePlan.FromAnalysisSource(
                pipeline.SegmentationSource ?? AnalysisImageSource.Original))} → Сегментация",
            _ => "Без изображения"
        };
        lines.Add($"Подложка: {backdrop}");
        VideoAnalysisPipelineDescription = string.Join(Environment.NewLine, lines);

        // ветвь метода заканчивается анализом и использует фактический путь входного участка
        string DescribeCalculationBranch(VideoPipelineStage source, List<string> methods) =>
            $"Расчёт: {DescribePaths(source)} → {VideoPipelinePlan.GetStageName(VideoPipelineStage.Analysis)} ({string.Join(", ", methods)})";

        // перечисляем подтвержденные пути источника с сохранением их ветвления
        string DescribePaths(VideoPipelineStage source) => string.Join(" или ", pipeline.Queue.DescribePathsTo(source));
    }

    // фиксируем маршруты включенных методов тем же снимком для подготовки кадра и подписи
    private VideoAnalysisPipeline CaptureVideoAnalysisPipeline() {
        AnalysisImageSource? edge = isEdgesAnalysisEnabled ? videoEdgeSource : null;
        AnalysisImageSource? segmentation = IsSegmentationAnalysisEnabled ? videoSegmentationSource : null;
        VideoPipelineStage inputs = isKeypointAnalysisEnabled ? VideoPipelineStage.Original : VideoPipelineStage.None;
        if (edge is { } edgeSource) {
            inputs |= VideoPipelinePlan.FromAnalysisSource(edgeSource);
        }
        if (segmentation is { } segmentationSource) {
            inputs |= VideoPipelinePlan.FromAnalysisSource(segmentationSource);
        }
        if (!VideoPipelinePlan.TryCreate(inputs, VideoPipelineStage.Original, VideoPipelineStage.Analysis,
            out VideoPipelinePlan? plan, out string? error)) {
            throw new InvalidOperationException(error);
        }
        return new(plan, edge, segmentation, isKeypointAnalysisEnabled);
    }
}
