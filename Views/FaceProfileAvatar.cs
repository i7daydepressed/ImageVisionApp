using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal sealed class FaceProfileAvatar : Control {
    private static readonly Color[] Palette = [
        Color.Parse("#2488AA"), Color.Parse("#8C68B9"), Color.Parse("#B9516B"), Color.Parse("#34866D"),
        Color.Parse("#AE6F31"), Color.Parse("#547EC6"), Color.Parse("#96714A"), Color.Parse("#886794")
    ];
    private static readonly IBrush Tile = new SolidColorBrush(Color.Parse("#EEF1F6"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#7E8795"));
    private readonly FaceProfileAvatarPattern? pattern;

    // задаем компактный декоративный элемент рядом с именем
    public FaceProfileAvatar(FaceProfileAvatarPattern? pattern) {
        this.pattern = pattern;
        Width = Height = 32;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    // рисуем зеркальные клетки или нейтральную отметку, если ключ недоступен
    public override void Render(DrawingContext context) {
        base.Render(context);
        if (pattern is not { } icon) {
            context.DrawRectangle(null, new Pen(Neutral, 1.5), new Rect(7, 7, 18, 18), 4, 4);
            return;
        }
        Color color = Palette[icon.PaletteIndex];
        SolidColorBrush ink = new(color);
        context.DrawRectangle(Tile, null, new Rect(0, 0, 32, 32), 5, 5);
        for (int row = 0; row < 7; row++) {
            for (int column = 0; column < 7; column++) {
                if (icon.HasCell(column, row)) {
                    context.FillRectangle(ink, new Rect(4 + column * 3.5, 4 + row * 3.5, 3, 3));
                }
            }
        }
    }
}
