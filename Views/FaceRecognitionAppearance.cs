using System;
using System.Collections.Generic;
using Avalonia.Media;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal enum FaceFrameColorMode { Common, KnownAndUnknown, PerProfile }

internal sealed class FaceRecognitionAppearance {
    private readonly Dictionary<Guid, Color> profileColors = new();

    public bool ShowProfileNames { get; set; } = true;
    public bool ShowUnknownLabels { get; set; }
    public FaceFrameColorMode ColorMode { get; set; }
    public Color CommonColor { get; set; } = Colors.Gold;
    public Color KnownColor { get; set; } = Colors.Gold;
    public Color UnknownColor { get; set; } = Colors.Turquoise;

    // при сбросе удаляем настройку ID, чтобы последующий общий цвет тоже применялся к профилю
    public void SetProfileColor(Guid id, Color? color) {
        if (color is { } selected) {
            profileColors[id] = selected;
        }
        else {
            profileColors.Remove(id);
        }
    }

    // различаем собственный цвет профиля и наследование цвета известных
    public Color? ReadProfileColor(Guid id) => profileColors.TryGetValue(id, out Color color) ? color : null;

    // детекция использует общий цвет, профильный режим при необходимости берет настройку по ID
    public Color FrameColor(RecognizedFace face, bool profilesAvailable) {
        if (!profilesAvailable || ColorMode == FaceFrameColorMode.Common) {
            return CommonColor;
        }
        if (face.Match.Profile is not { } profile) {
            return UnknownColor;
        }
        return ColorMode == FaceFrameColorMode.PerProfile ? ReadProfileColor(profile.Id) ?? KnownColor : KnownColor;
    }

    // скрытая подпись не попадает ни на кадр, ни в его доступное имя
    public string? Label(RecognizedFace face, FaceVideoResult result) {
        if (!result.ShowLabels || result.EmptyCatalogue) {
            return null;
        }
        if (face.Match.Profile is { } profile) {
            return ShowProfileNames ? profile.Name : null;
        }
        return ShowUnknownLabels ? "Неизвестное лицо" : null;
    }
}
