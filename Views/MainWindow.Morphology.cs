using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Border CreateMorphologyCard() {
        // карточка стоит среди инструментов, матрицу по-прежнему подтверждаем в редакторе
        return new Border {
            Width = 286,
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
                    new TextBlock { Text = "Эрозия", FontWeight = FontWeight.SemiBold },
                    CreateMorphologyActions()
                }
            }
        };
    }

    private StackPanel CreateMorphologyActions() {
        TextBlock errorText = new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Red,
            IsVisible = false
        };
        Button erosionButton = new Button { Content = "Добавить эрозию" };
        erosionButton.Click += (_, _) => {
            if (DataContext is not MainViewModel viewModel) {
                errorText.Text = "Очередь недоступна";
                errorText.IsVisible = true;
                return;
            }
            bool added = viewModel.TryAddErosionStep(out string error);
            errorText.Text = error;
            errorText.IsVisible = !added;
        };
        return new StackPanel {
            Spacing = 6,
            Children = {
                erosionButton,
                new TextBlock {
                    Text = "Эрозия использует примененную матрицу. После редактирования сначала подтвердите изменения.",
                    TextWrapping = TextWrapping.Wrap
                },
                errorText
            }
        };
    }
}
