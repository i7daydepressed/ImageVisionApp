using System;

namespace ImageVisionApp.Models;

internal enum VideoPreviewMode { Live, Prepared }

internal readonly record struct VideoPreviewSettings(int MaximumWorkers, int BudgetMiB) {
    public VideoPreviewMode Mode { get; init; }
    public static VideoPreviewSettings Default { get; } = new(0, 64);
    public int EffectiveWorkers => MaximumWorkers == 0 ? Math.Min(2, Environment.ProcessorCount) : MaximumWorkers;
    public long BudgetBytes => (long)BudgetMiB * 1024 * 1024;
}
