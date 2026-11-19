using System;

namespace ImageVisionApp.Models;

internal readonly record struct VideoPreviewSettings(int MaximumWorkers, int BudgetMiB) {
    public static VideoPreviewSettings Default { get; } = new(0, 64);
    public int EffectiveWorkers => MaximumWorkers == 0 ? Math.Min(2, Environment.ProcessorCount) : MaximumWorkers;
    public long BudgetBytes => (long)BudgetMiB * 1024 * 1024;
}
