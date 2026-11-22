using System;

namespace ImageVisionApp.Models;

internal enum VideoPreviewMode { Live, Prepared, Strict }
internal enum VideoArrowStep { ThreeSeconds, OneFrame, FiveFrames, OneSecond }

internal readonly record struct VideoPreviewSettings(int MaximumWorkers, int BudgetMiB) {
    public const int WorkerLimit = 12;
    public const int BudgetLimitMiB = 1024;
    public const int AutoWorkerLimit = 4;
    public int AnalysisMaximumWorkers { get; init; } = 1;
    public int AnalysisBudgetMiB { get; init; } = 128;
    public int ForegroundDecoderThreads { get; init; } = 4;
    public int ForegroundBudgetMiB { get; init; } = 128;
    public VideoPreviewMode Mode { get; init; }
    public bool BufferBeforeStrictResume { get; init; }
    public VideoArrowStep ArrowStep { get; init; }
    public static VideoPreviewSettings Default { get; } = new(0, 64);
    public int EffectiveWorkers => MaximumWorkers == 0 ? Math.Min(2, Environment.ProcessorCount) : MaximumWorkers;
    public long ForegroundBudgetBytes => (long)ForegroundBudgetMiB * 1024 * 1024;
    public long AnalysisBudgetBytes => (long)AnalysisBudgetMiB * 1024 * 1024;
    public long BudgetBytes => (long)BudgetMiB * 1024 * 1024;
}
