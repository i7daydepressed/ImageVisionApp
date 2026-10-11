using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Border CreateStructuralElementCard() {
        TextBlock description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        description.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.StructuralElementDescription)));

        StructuralElement initialElement = DataContext is MainViewModel viewModel
            ? viewModel.SelectedStructuralElement
            : StructuralElement.CreateCross();
        StructuralElementEditor editor = new StructuralElementEditor(initialElement);
        editor.SelectionApplied += selectedElement => {
            if (DataContext is MainViewModel currentViewModel) {
                // редактор передает готовый элемент, очередь и предпросмотр не меняем
                currentViewModel.SetStructuralElement(selectedElement);
            }
        };
        editor.DataContextChanged += (_, _) => {
            if (editor.DataContext is MainViewModel currentViewModel) {
                editor.SetElement(currentViewModel.SelectedStructuralElement);
            }
        };

        StackPanel controls = new StackPanel {
            Spacing = 8,
            Children = {
                new TextBlock {
                    Text = "Структурный элемент",
                    FontWeight = FontWeight.SemiBold
                },
                description,
                new TextBlock {
                    Text = "0 — белая ячейка, сосед исключен. 1 — черная ячейка, сосед включен. Красная рамка — опорная точка, координаты начинаются с 1. Выбор сохраняется кнопкой «Применить матрицу».",
                    TextWrapping = TextWrapping.Wrap
                },
                editor
            }
        };
        return new Border {
            Margin = new Thickness(12),
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = controls
        };
    }
}
