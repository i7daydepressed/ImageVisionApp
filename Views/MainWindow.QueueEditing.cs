using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private ImageEditStepEditor? queueStepEditor;
    private MainViewModel? queueEditingViewModel;

    private Border CreateAdvancedQueuePanel() { // список занимает остаток высоты, редактор стоит под ним в отдельной строке
        Grid queueContent = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8
        };

        queueContent.Children.Add(new TextBlock {
            Text = "Очередь действий",
            FontSize = 17
        });


        ListBox queueList = new ListBox {//внешеий вид элемента в очереди
            ItemTemplate = new FuncDataTemplate<ImageEditStep>((step, _) => CreateAdvancedQueueRow(step))
        };

        queueList.Bind(// берем действия из AdvancedSteps во ViewModel: при добавлении или удалении шага список обновится
            ItemsControl.ItemsSourceProperty,
            new Avalonia.Data.Binding("AdvancedSteps"));

        Grid.SetRow(queueList, 1);// заголовок остается в строке 0, а список ставим в строку 1 под ним
        queueContent.Children.Add(queueList);

        ImageEditStepEditor stepEditor = CreateQueueStepEditor();
        Grid.SetRow(stepEditor, 2);
        queueContent.Children.Add(stepEditor);

        Border queuePlaceholder = new Border {//левая панель очереди
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = queueContent
        };

        return queuePlaceholder;
    }

    //получает один элемент из AdvancedSteps к примеру step может быть действием яркость +50
    private Grid CreateAdvancedQueueRow(ImageEditStep? step) {
        Grid row = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 8,
            MinHeight = 60
        };

        TextBlock name = new TextBlock {//текст блок шага
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        name.Bind(
            TextBlock.TextProperty,
            new Avalonia.Data.Binding("DisplayName"));//название шага
        Grid.SetColumnSpan(name, 4);
        row.Children.Add(name);

        Button editButton = new Button {
            Content = "Изменить",
            IsEnabled = step is not null && ImageStepParameterRules.CanEdit(step.Type),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        editButton.Click += (_, _) => EditAdvancedStep(step);
        Grid.SetRow(editButton, 1);
        row.Children.Add(editButton);

        Button moveUpButton = new Button {
            Content = "↑",
            MinWidth = 28
        };
        moveUpButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.MoveAdvancedStep(step, -1);
            }
        };
        Grid.SetRow(moveUpButton, 1);
        Grid.SetColumn(moveUpButton, 1);
        row.Children.Add(moveUpButton);

        Button moveDownButton = new Button {
            Content = "↓",
            MinWidth = 28
        };
        moveDownButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.MoveAdvancedStep(step, 1);
            }
        };
        Grid.SetRow(moveDownButton, 1);
        Grid.SetColumn(moveDownButton, 2);
        row.Children.Add(moveDownButton);

        Button removeButton = new Button {//минусик для удаления
            Content = "−",
            MinWidth = 28
        };
        removeButton.Click += (_, _) => {// подписываем минусик
            if (DataContext is MainViewModel viewModel) {
                viewModel.RemoveAdvancedStep(step);
            }
            // запоминает step, для которого создали кнопку при нажатии вызывает MainViewModel.RemoveAdvancedStep(step), то есть удаляет именно это действие
        };

        Grid.SetRow(removeButton, 1);
        Grid.SetColumn(removeButton, 3);// ставит минус справа
        row.Children.Add(removeButton);
        return row;
    }

    private ImageEditStepEditor CreateQueueStepEditor() { // создаем один редактор вместе со страницей и связываем подтверждение с окном
        ImageEditStepEditor editor = new ImageEditStepEditor(ApplyQueueStepParameter);
        queueStepEditor = editor;
        DataContextChanged += QueueEditor_OnDataContextChanged;
        Closed += (_, _) => DetachQueueEditingViewModel();
        AttachQueueEditingViewModel();
        return editor;
    }

    private void QueueEditor_OnDataContextChanged(object? sender, EventArgs e) {
        AttachQueueEditingViewModel();
    }

    private void AttachQueueEditingViewModel() { // переводим подписку на текущую ViewModel, выбор прежней очереди закрываем
        MainViewModel? next = DataContext as MainViewModel;
        if (ReferenceEquals(next, queueEditingViewModel)) {
            return;
        }
        // сначала отписываемся, чтобы события старой очереди не обновляли карточку
        DetachQueueEditingViewModel();
        queueEditingViewModel = next;
        queueStepEditor?.SetStep(null);
        if (queueEditingViewModel is not null) {
            queueEditingViewModel.AdvancedSteps.CollectionChanged += QueueEditor_OnStepsChanged;
        }
    }

    private void DetachQueueEditingViewModel() { // освобождаем подписку при смене DataContext и закрытии окна
        if (queueEditingViewModel is not null) {
            queueEditingViewModel.AdvancedSteps.CollectionChanged -= QueueEditor_OnStepsChanged;
            queueEditingViewModel = null;
        }
    }

    private void QueueEditor_OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        ImageEditStep? step = queueStepEditor?.SelectedStep;
        if (step is null) {
            return;
        }
        int index = queueEditingViewModel?.AdvancedSteps.IndexOf(step) ?? -1;
        // удаление, сброс и новый исходник закрывают редактор, перемещение сохраняет его черновик
        queueStepEditor?.SetStep(index < 0 ? null : step, index);
    }

    private void EditAdvancedStep(ImageEditStep? step) { // открываем конкретный объект из текущей очереди, одинаковые названия не мешают
        if (step is null || DataContext is not MainViewModel viewModel) {
            return;
        }
        int index = viewModel.AdvancedSteps.IndexOf(step);
        if (index >= 0) {
            queueStepEditor?.SetStep(step, index);
        }
    }

    private string? ApplyQueueStepParameter(ImageEditStep step, double value) { // передаем команду текущей ViewModel и возвращаем ошибку карточке
        if (DataContext is not MainViewModel viewModel) {
            return "Очередь недоступна";
        }
        return viewModel.TryUpdateAdvancedStepParameter(step, value, out string error)
            ? null
            : error;
    }
}
