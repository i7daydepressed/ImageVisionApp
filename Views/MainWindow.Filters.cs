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

        Button motionBlurButton = new Button { Content = "Добавить размытие" };
        motionBlurButton.Click += (_, _) => {
            if (DataContext is not MainViewModel viewModel) {
                errorText.Text = "Очередь недоступна";
                errorText.IsVisible = true;
                return;
            }
            bool added = viewModel.TryAddMotionBlurStep(out string error);
            errorText.Text = error;
            errorText.IsVisible = !added;
        };

        Button embossButton = new Button { Content = "Добавить тиснение" };
        embossButton.Click += (_, _) => {
            if (DataContext is not MainViewModel viewModel) {
                errorText.Text = "Очередь недоступна";
                errorText.IsVisible = true;
                return;
            }
            bool added = viewModel.TryAddEmbossStep(out string error);
            errorText.Text = error;
            errorText.IsVisible = !added;
        };

        Button customButton = new Button { Content = "Своя матрица…" };
        customButton.Click += (_, _) => {
            if (DataContext is not MainViewModel viewModel) {
                errorText.Text = "Очередь недоступна";
                errorText.IsVisible = true;
                return;
            }
            errorText.Text = string.Empty;
            errorText.IsVisible = false;
            OpenFilterKernelEditor(viewModel);
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
                        Children = { sharpenButton, motionBlurButton, embossButton, customButton }
                    },
                    new TextBlock {
                        Text = "Резкость усиливает границы. Размытие усредняет 9 пикселей по горизонтали. Тиснение выделяет рельеф на сером фоне. «Своя матрица» открывает числовой редактор; подтверждение добавляет один шаг.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    errorText
                }
            }
        };
    }
}
