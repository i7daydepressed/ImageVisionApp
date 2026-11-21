using System;

namespace ImageVisionApp.Models;

internal enum VideoPreviewMode { Live, Prepared }
internal enum VideoArrowStep { ThreeSeconds, OneFrame, FiveFrames, OneSecond }

internal readonly record struct VideoPreviewSettings(int MaximumWorkers, int BudgetMiB) {
    public const int WorkerLimit = 12;
    public const int BudgetLimitMiB = 512;
    public const int AutoWorkerLimit = 4;
    public int AnalysisMaximumWorkers { get; init; } = 1;
    public int AnalysisBudgetMiB { get; init; } = 128;
    public VideoPreviewMode Mode { get; init; }
    public VideoArrowStep ArrowStep { get; init; }
    public static VideoPreviewSettings Default { get; } = new(0, 64);
    public int EffectiveWorkers => MaximumWorkers == 0 ? Math.Min(2, Environment.ProcessorCount) : MaximumWorkers;
    public long AnalysisBudgetBytes => (long)AnalysisBudgetMiB * 1024 * 1024;
    public long BudgetBytes => (long)BudgetMiB * 1024 * 1024;
}
