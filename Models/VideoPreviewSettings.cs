using System;

namespace ImageVisionApp.Models;

internal enum VideoPreviewMode { Live, Prepared, Strict }
internal enum VideoArrowStep { ThreeSeconds, OneFrame, FiveFrames, OneSecond }

internal readonly record struct VideoPreviewSettings(int MaximumWorkers, int BudgetMiB) {
    public const int WorkerLimit = 12;
    public const int BudgetLimitMiB = 1024;
    public const int AutoWorkerLimit = 4;
    public int AnalysisMaximumWorkers { get; init; } = WorkerLimit;
    public int AnalysisBudgetMiB { get; init; } = BudgetLimitMiB;
    public int ForegroundDecoderThreads { get; init; } = WorkerLimit;
    public int ForegroundBudgetMiB { get; init; } = BudgetLimitMiB;
    public int FaceMaximumWorkers { get; init; } = WorkerLimit;
    public int FaceBudgetMiB { get; init; } = BudgetLimitMiB;
    public VideoPreviewMode Mode { get; init; } = VideoPreviewMode.Strict;
    public bool BufferBeforeStrictResume { get; init; }
    public VideoArrowStep ArrowStep { get; init; }
    public static VideoPreviewSettings Default { get; } = new(WorkerLimit, BudgetLimitMiB);
    public int EffectiveWorkers => MaximumWorkers == 0 ? Math.Min(2, Environment.ProcessorCount) : MaximumWorkers;
    public long ForegroundBudgetBytes => (long)ForegroundBudgetMiB * 1024 * 1024;
    public long AnalysisBudgetBytes => (long)AnalysisBudgetMiB * 1024 * 1024;
    public long FaceBudgetBytes => (long)FaceBudgetMiB * 1024 * 1024;
    public long BudgetBytes => (long)BudgetMiB * 1024 * 1024;
}
