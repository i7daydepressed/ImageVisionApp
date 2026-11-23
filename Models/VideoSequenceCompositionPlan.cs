using ImageVisionApp.Services;

namespace ImageVisionApp.Models;

internal enum VideoSequenceBackdrop { Original, Editing, Analysis, Subtraction, Replacement }
internal enum VideoSubtractionRaster { Foreground, Mask, Background }

internal sealed record VideoSequenceCompositionPlan(VideoSequenceBackdrop Backdrop, VideoSubtractionRaster SubtractionRaster,
    VideoPipelineStage SubtractionSource, VideoPipelineStage ReplacementSource, VideoBackgroundImage? ReplacementImage,
    VideoPipelineStage BlurSource, int? BlurRadius, VideoPipelineStage FlowSource, bool ShowFlow) {
    public VideoPipelineStage BaseInput => Backdrop switch {
        VideoSequenceBackdrop.Editing => VideoPipelineStage.Editing,
        VideoSequenceBackdrop.Analysis => VideoPipelineStage.Analysis,
        VideoSequenceBackdrop.Subtraction => SubtractionSource,
        VideoSequenceBackdrop.Replacement => ReplacementSource,
        _ => VideoPipelineStage.Original
    };

    public VideoPipelineStage MaskSources =>
        (Backdrop is VideoSequenceBackdrop.Subtraction or VideoSequenceBackdrop.Replacement ? BaseInput : VideoPipelineStage.None) |
        (BlurRadius.HasValue ? BlurSource : VideoPipelineStage.None);
    public VideoPipelineStage RequiredSources => BaseInput | MaskSources | (ShowFlow ? FlowSource : VideoPipelineStage.None);
    public bool HasEffects => Backdrop != VideoSequenceBackdrop.Original || BlurRadius.HasValue || ShowFlow;
    public bool CanSeekFromPreviousFrame => MaskSources == VideoPipelineStage.None;
}
