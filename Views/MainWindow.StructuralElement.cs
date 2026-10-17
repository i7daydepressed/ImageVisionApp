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
    private StructuralElementEditor? structuralElementEditor;

    private Border CreateStructuralElementCard() {
        TextBlock description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        description.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.StructuralElementDescription)));

        StructuralElement initialElement = DataContext is MainViewModel viewModel
            ? viewModel.SelectedStructuralElement
            : StructuralElement.CreateCross();
        StructuralElementEditor editor =
            new StructuralElementEditor(initialElement, description);
        structuralElementEditor = editor;
        editor.FilterApplyRequested += kernel => {
            if (DataContext is not MainViewModel currentViewModel) {
                return "Очередь недоступна";
            }
            // ядро берем из проверенного черновика, прежний валидный выбор не подставляем
            if (!currentViewModel.TryAddCustomFilterStep(kernel, out string error)) {
                return error;
            }
            currentViewModel.SetFilterKernel(kernel);
            return null;
        };
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

        return new Border {
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = editor
        };
    }
}
