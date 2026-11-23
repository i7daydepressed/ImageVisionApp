using System.Collections.Generic;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private VideoSequenceCompositionPlan videoSequenceComposition = new(VideoSequenceBackdrop.Original,
        VideoSubtractionRaster.Foreground, VideoPipelineStage.Original, VideoPipelineStage.Original, null,
        VideoPipelineStage.Original, null, VideoPipelineStage.Original, false);

    private VideoPipelineStage VideoSequenceInputs => videoSequenceComposition.RequiredSources;
    private VideoPipelineStage VideoSequenceOutputGeometrySource => videoSequenceComposition.BaseInput;
    private bool HasActiveVideoSequenceEffect => videoSequenceComposition.HasEffects;
    public bool CanUseVideoSequenceAsAnalysisSource => (VideoSequenceInputs & VideoPipelineStage.Analysis) == 0;
    public bool CanUseVideoSubtractionBackdrop => IsVideoOpen && VideoForegroundEnabled;

    public int SelectedVideoSequenceBackdropIndex {
        get => (int)videoSequenceComposition.Backdrop;
        set {
            if (!IsVideoOpen || value is < 0 or > 4 ||
                value == (int)VideoSequenceBackdrop.Subtraction && !CanUseVideoSubtractionBackdrop ||
                value == (int)VideoSequenceBackdrop.Replacement && !CanUseVideoBackgroundReplacement) {
                return;
            }
            TrySetVideoSequenceComposition(videoSequenceComposition with { Backdrop = (VideoSequenceBackdrop)value });
        }
    }

    public int SelectedVideoSubtractionRasterIndex {
        get => (int)videoSequenceComposition.SubtractionRaster;
        set {
            if (IsVideoOpen && value is >= 0 and <= 2) {
                TrySetVideoSequenceComposition(videoSequenceComposition with { SubtractionRaster = (VideoSubtractionRaster)value },
                    nameof(SelectedVideoSubtractionRasterIndex), videoSequenceComposition.Backdrop == VideoSequenceBackdrop.Subtraction);
            }
        }
    }

    public int SelectedVideoSubtractionSourceIndex {
        get => VideoSequenceSourceIndex(videoSequenceComposition.SubtractionSource);
        set => SetVideoSequenceSource(value, nameof(SelectedVideoSubtractionSourceIndex));
    }
    public int SelectedVideoReplacementSourceIndex {
        get => VideoSequenceSourceIndex(videoSequenceComposition.ReplacementSource);
        set => SetVideoSequenceSource(value, nameof(SelectedVideoReplacementSourceIndex));
    }
    public int SelectedVideoBlurSourceIndex {
        get => VideoSequenceSourceIndex(videoSequenceComposition.BlurSource);
        set => SetVideoSequenceSource(value, nameof(SelectedVideoBlurSourceIndex));
    }
    public int SelectedVideoFlowSourceIndex {
        get => VideoSequenceSourceIndex(videoSequenceComposition.FlowSource);
        set => SetVideoSequenceSource(value, nameof(SelectedVideoFlowSourceIndex));
    }

    // обновляем только выбранный метод, перезапускаем проход для его активного слоя
    private void SetVideoSequenceSource(int index, string property) {
        if (!IsVideoOpen || index is < 0 or > 2) {
            return;
        }
        VideoPipelineStage source = (VideoPipelineStage)(1 << index);
        var (plan, active) = property switch {
            nameof(SelectedVideoSubtractionSourceIndex) => (videoSequenceComposition with { SubtractionSource = source },
                videoSequenceComposition.Backdrop == VideoSequenceBackdrop.Subtraction),
            nameof(SelectedVideoReplacementSourceIndex) => (videoSequenceComposition with { ReplacementSource = source },
                videoSequenceComposition.Backdrop == VideoSequenceBackdrop.Replacement),
            nameof(SelectedVideoBlurSourceIndex) => (videoSequenceComposition with { BlurSource = source }, ShowsVideoMovingBlur),
            _ => (videoSequenceComposition with { FlowSource = source }, ShowsVideoOpticalFlow)
        };
        TrySetVideoSequenceComposition(plan, property, active);
    }

    // возвращаем позицию источника в порядке «Оригинал», «Редактирование», «Анализ»
    private static int VideoSequenceSourceIndex(VideoPipelineStage source) => source switch {
        VideoPipelineStage.Editing => 1,
        VideoPipelineStage.Analysis => 2,
        _ => 0
    };

    // меняем общий снимок настроек только после проверки всех связей результата
    private bool TrySetVideoSequenceComposition(VideoSequenceCompositionPlan next, string? property = null, bool refresh = true) {
        if (videoSequenceComposition == next) {
            return true;
        }
        if (!TryValidateVideoSequenceSources(next.RequiredSources, out string? error)) {
            SetVideoForegroundStatus(error!);
            return false;
        }

        bool backdropChanged = next.Backdrop != videoSequenceComposition.Backdrop;
        videoSequenceComposition = next;
        if (property is not null) {
            OnPropertyChanged(property);
        }
        if (backdropChanged) {
            OnPropertyChanged(nameof(SelectedVideoSequenceBackdropIndex));
        }
        OnPropertyChanged(nameof(CanUseVideoSequenceAsAnalysisSource));
        NotifyVideoSequenceEffectAvailability();
        if (refresh) {
            RestartVideoForeground();
        }
        return true;
    }

    // указываем подложку и отдельно показываем источник каждого активного расчета
    private static IEnumerable<string> DescribeVideoSequenceComposition(VideoSequenceCompositionPlan plan) {
        string backdrop = DescribeSequenceBackdrop(plan.Backdrop);
        if (plan.Backdrop == VideoSequenceBackdrop.Subtraction) {
            string raster = plan.SubtractionRaster switch {
                VideoSubtractionRaster.Mask => "маска",
                VideoSubtractionRaster.Background => "задний план",
                _ => "передний план"
            };
            backdrop += $" · {raster} ({VideoPipelinePlan.GetStageName(plan.BaseInput)})";
        }
        else if (plan.Backdrop == VideoSequenceBackdrop.Replacement) {
            backdrop += $" ({VideoPipelinePlan.GetStageName(plan.BaseInput)})";
        }
        yield return $"Подложка: {backdrop}";
        if (plan.BlurRadius is { } radius) {
            yield return $"Размытие {radius} пикс.: {VideoPipelinePlan.GetStageName(plan.BlurSource)}";
        }
        if (plan.ShowFlow) {
            yield return $"Оптический поток: {VideoPipelinePlan.GetStageName(plan.FlowSource)}";
        }
    }

    // выводим в статусе тот же вид, который выбран в общих параметрах отображения
    private static string DescribeSequenceBackdrop(VideoSequenceBackdrop backdrop) => backdrop switch {
        VideoSequenceBackdrop.Editing => "Результат редактирования",
        VideoSequenceBackdrop.Analysis => "Результат анализа",
        VideoSequenceBackdrop.Subtraction => "Вычитание фона",
        VideoSequenceBackdrop.Replacement => "Заменённый фон",
        _ => "Оригинал"
    };
}
