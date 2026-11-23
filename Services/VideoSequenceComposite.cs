using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed class VideoSequenceComposite : IDisposable {
    private static readonly VideoPipelineStage[] Sources = [
        VideoPipelineStage.Original, VideoPipelineStage.Editing, VideoPipelineStage.Analysis
    ];

    private readonly VideoSequenceCompositionPlan plan;
    private readonly VideoEditorGeometry baseGeometry;
    private readonly VideoEditorGeometry blurProjection;
    private readonly VideoEditorGeometry flowGeometry;
    private readonly Dictionary<VideoPipelineStage, VideoForegroundModel> masks = new();
    private readonly VideoOpticalFlowModel? flow;
    private VideoBackgroundRaster? replacement;

    // закрепляем по одной модели за каждым входом, которому нужна маска движения
    public VideoSequenceComposite(VideoSequenceCompositionPlan plan, Func<VideoPipelineStage, VideoEditorGeometry> geometry) {
        this.plan = plan;
        baseGeometry = geometry(plan.BaseInput);
        try {
            foreach (VideoPipelineStage source in Sources) {
                if ((plan.MaskSources & source) != 0) {
                    masks.Add(source, new(geometry(source).OutputSize));
                }
            }
            if (plan.BlurRadius.HasValue) {
                blurProjection = geometry(plan.BlurSource).RelativeTo(baseGeometry);
                if (blurProjection.OutputSize != baseGeometry.OutputSize) {
                    throw new InvalidDataException("Геометрия размытия не соответствует подложке видеоряда");
                }
            }
            if (plan.ShowFlow) {
                flowGeometry = geometry(plan.FlowSource);
                flow = new(flowGeometry.OutputSize);
            }
        }
        catch {
            Dispose();
            throw;
        }
    }

    // прогреваем каждую модель и собираем слои только у кадра, который пойдет на показ
    public VideoForegroundFrame? Process(TimedVideoInput input, Func<VideoPipelineStage, VideoEditorFrame> pixels,
        bool publish, CancellationToken token) {
        VideoForegroundFrame? baseMask = null;
        VideoForegroundFrame? blurMask = null;
        foreach (var (source, model) in masks) {
            VideoForegroundFrame? frame = model.Process(input, pixels(source), publish, token);
            if (source == plan.BaseInput) {
                baseMask = frame;
            }
            if (source == plan.BlurSource) {
                blurMask = frame;
            }
        }
        if (!publish) {
            flow?.Process(input, pixels(plan.FlowSource), false, token);
            return null;
        }

        VideoEditorFrame basePixels = pixels(plan.BaseInput);
        if (basePixels.Size != baseGeometry.OutputSize) {
            throw new InvalidDataException("Полученный растр подложки не соответствует геометрии видеоряда");
        }
        bool plainBase = plan.Backdrop is not (VideoSequenceBackdrop.Subtraction or VideoSequenceBackdrop.Replacement);
        VideoEditorFrame? flowInput = flow is null ? null : pixels(plan.FlowSource);
        if (flowInput is not null && plainBase && !plan.BlurRadius.HasValue) {
            VideoForegroundFrame arrows = RenderFlow(input, flowInput, basePixels, token);
            return ReferenceEquals(basePixels, flowInput) ? arrows :
                arrows with { Size = basePixels.Size, Rgb = basePixels.CopyRgb(token) };
        }

        VideoForegroundFrame original = new(input.Time, input.Until, input.Ordinal, basePixels.Size,
            baseMask?.Rgb ?? basePixels.CopyRgb(token), ReadOnlyMemory<byte>.Empty);
        byte[] composition = ComposeBase(original, baseMask, plainBase, token);
        if (plan.BlurRadius is { } radius) {
            OverlayBlur(composition, blurMask ?? throw new InvalidOperationException("Не рассчитана маска размытия"), radius, token);
        }
        if (flowInput is null) {
            return original with { Composition = composition };
        }

        using VideoEditorFrame ownedBackdrop = VideoEditorFrame.TakeBgra(original.Size, composition);
        return original with { OpticalFlow = RenderFlow(input, flowInput, ownedBackdrop, token).OpticalFlow };
    }

    // рисуем стрелки после размытия, а у простого входа используем его готовый снимок rgb
    private VideoForegroundFrame RenderFlow(TimedVideoInput input, VideoEditorFrame source, VideoEditorFrame backdrop,
        CancellationToken token) {
        return flow!.Process(input, source, true, token, backdrop, flowGeometry, baseGeometry) ??
            throw new InvalidOperationException("Не рассчитан оптический поток");
    }

    // выбираем растр вычитания либо заполняем фон картинкой через маску того же кадра
    private byte[] ComposeBase(VideoForegroundFrame original, VideoForegroundFrame? baseMask,
        bool plainBase, CancellationToken token) {
        VideoForegroundFrame source = plainBase ? original : baseMask ??
            throw new InvalidOperationException("Не рассчитана маска выбранной подложки");
        if (plan.Backdrop == VideoSequenceBackdrop.Replacement) {
            replacement ??= (plan.ReplacementImage ?? throw new InvalidOperationException("Не выбрана картинка для замены фона"))
                .PrepareCover(original.Size, token);
        }
        bool subtraction = plan.Backdrop == VideoSequenceBackdrop.Subtraction;
        int stride = checked(source.Size.Width * 4);
        byte[] result = GC.AllocateUninitializedArray<byte>(checked(stride * source.Size.Height));
        for (int y = 0; y < source.Size.Height; y++) {
            token.ThrowIfCancellationRequested();
            VideoForegroundCompositor.CopyRow(source, y, result.AsSpan(y * stride, stride),
                maskOnly: subtraction && plan.SubtractionRaster == VideoSubtractionRaster.Mask,
                background: replacement, rawInput: plainBase,
                invertMask: subtraction && plan.SubtractionRaster == VideoSubtractionRaster.Background);
        }
        return result;
    }

    // переносим только размытые пиксели под маской, оставляя остальные пиксели подложки
    private void OverlayBlur(Span<byte> composition, VideoForegroundFrame source, int radius, CancellationToken token) {
        VideoMotionBlurRaster blur = VideoMotionBlurRaster.Create(source, radius, token);
        if (blur.Bgra.IsEmpty) {
            return;
        }
        ReadOnlySpan<byte> mask = source.Mask.Span;
        ReadOnlySpan<byte> pixels = blur.Bgra.Span;
        int width = source.Size.Width;
        int destinationWidth = baseGeometry.OutputSize.Width;
        for (int y = 0; y < source.Size.Height; y++) {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int offset = y * width + x;
                if (mask[offset] == 0) {
                    continue;
                }
                PixelPoint target = blurProjection.MapPixel(new(x, y));
                pixels.Slice(offset * 4, 4).CopyTo(composition.Slice((target.Y * destinationWidth + target.X) * 4, 4));
            }
        }
    }

    // считаем постоянные модели, одновременно живущие маски и одну пару растров результата
    public static (long PersistentBytes, long ScratchBytes, long FrameBytes) GetResources(
        VideoSequenceCompositionPlan plan, Func<VideoPipelineStage, VideoEditorGeometry> geometry) {
        PixelSize baseSize = geometry(plan.BaseInput).OutputSize;
        long baseBgra = VideoEditorFrame.GetBufferBytes(baseSize);
        long frameBytes = checked(baseBgra + baseBgra / 4 * 3);
        long persistent = 0;
        long scratch = checked(3 * baseBgra + baseSize.Width * 4L + frameBytes);
        foreach (VideoPipelineStage source in Sources) {
            if ((plan.MaskSources & source) == 0) {
                continue;
            }
            PixelSize size = geometry(source).OutputSize;
            long model = VideoForegroundModel.GetPersistentBytes(size);
            long bgra = VideoEditorFrame.GetBufferBytes(size);
            persistent = checked(persistent + model);
            // из прежней оценки исключаем общий вход и экран, оставляем рабочие строки и снимок rgb с маской
            scratch = checked(scratch + VideoForegroundModel.GetRequiredBufferBytes(size, 0) -
                model - 5 * bgra - size.Width * 4L);
        }
        if (plan.Backdrop == VideoSequenceBackdrop.Replacement && plan.ReplacementImage is { } image) {
            persistent = checked(persistent + image.BufferBytes + baseBgra / 4 * 3);
        }
        if (plan.BlurRadius is { } radius) {
            PixelSize size = geometry(plan.BlurSource).OutputSize;
            scratch = checked(scratch + VideoMotionBlurRaster.GetScratchBytes(size, radius) +
                VideoMotionBlurRaster.GetBufferBytes(size));
        }
        if (plan.ShowFlow) {
            PixelSize size = geometry(plan.FlowSource).OutputSize;
            long model = VideoOpticalFlowModel.GetPersistentBytes(size);
            long bgra = VideoEditorFrame.GetBufferBytes(size);
            bool hasComposedBackdrop = plan.Backdrop is VideoSequenceBackdrop.Subtraction or VideoSequenceBackdrop.Replacement ||
                plan.BlurRadius.HasValue;
            persistent = checked(persistent + model);
            // готовый растр уже учтен, здесь остаются матрицы расчета, снимок входа и подложка до стрелок
            scratch = checked(scratch + VideoOpticalFlowModel.GetRequiredBufferBytes(size, 0) -
                model - 3 * bgra - 2 * VideoOpticalFlowModel.GetFrameBytes(size) - size.Width * 4L +
                (hasComposedBackdrop || plan.FlowSource != plan.BaseInput ? bgra / 4 * 3 : 0) +
                (hasComposedBackdrop ? baseBgra : 0));
        }
        return (persistent, scratch, frameBytes);
    }

    // освобождаем модели одного прохода после остановки его читателя
    public void Dispose() {
        flow?.Dispose();
        foreach (VideoForegroundModel model in masks.Values) {
            model.Dispose();
        }
    }
}
