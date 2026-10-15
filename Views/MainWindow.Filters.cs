using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Border CreateFiltersCard() {
        TextBlock errorText = new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Red,
            IsVisible = false
        };
        Button sharpenButton = new Button { Content = "Добавить резкость" };
        sharpenButton.Click += (_, _) => {
            if (DataContext is not MainViewModel viewModel) {
                errorText.Text = "Очередь недоступна";
                errorText.IsVisible = true;
                return;
            }
            bool added = viewModel.TryAddSharpenStep(out string error);
            errorText.Text = error;
            errorText.IsVisible = !added;
        };

        return new Border {
            Width = 584,
            MinHeight = 160,
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel {
                Spacing = 6,
                Children = {
                    new TextBlock { Text = "Фильтры", FontWeight = FontWeight.SemiBold },
                    new WrapPanel {
                        Orientation = Orientation.Horizontal,
                        ItemSpacing = 8,
                        LineSpacing = 8,
                        Children = { sharpenButton }
                    },
                    new TextBlock {
                        Text = "Резкость усиливает границы и мелкие детали. Повторное добавление применяет фильтр еще раз.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    errorText
                }
            }
        };
    }
}
