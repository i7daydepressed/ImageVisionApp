using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ImageVisionApp.Services;

internal static class VideoExportService {
    // читаем полное разрешение, а выбранный вид строим в независимом проходе от физического начала
    public static async Task ExportAsync(VideoExportPlan plan, string destination, IProgress<VideoExportProgress>? progress,
        CancellationToken token) {
        string sourcePath = plan.Document.SourcePath!;
        FfprobeVideoMediaSource preview = await FfprobeVideoMediaSource.OpenAsync(sourcePath, plan.Identity, token).ConfigureAwait(false);
        FfprobeVideoMediaSource source = preview.WithOriginalSize();
        await VideoExportWriter.WriteAsync(sourcePath, plan.Identity, destination, plan.GetOutputSize(source.FrameSize),
            RenderFramesAsync(source, plan, token), progress, token).ConfigureAwait(false);
    }

    // передаём текущий растр писателю до следующего MoveNext, сохраняя точные метки и номер
    private static async IAsyncEnumerable<TimedVideoInput> RenderFramesAsync(IVideoMediaSource source, VideoExportPlan plan,
        [EnumeratorCancellation] CancellationToken token) {
        using VideoExportProcessor processor = plan.OpenProcessor(source.FrameSize, token);
        VideoFrameReadRequest start = new(0) { Anchor = new(new(0, 1), 0, false) };
        await foreach (TimedVideoInput input in source.ReadFramesAsync(start, token).WithCancellation(token).ConfigureAwait(false)) {
            using VideoEditorFrame original = input.Pixels;
            using VideoEditorFrame result = processor.Render(input, token);
            yield return input with { Pixels = result };
        }
    }
}
