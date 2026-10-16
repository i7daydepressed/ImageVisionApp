using System;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool filterKernelDialogOpen;

    // прежний маршрут открытия FilterKernelWindow, действующие кнопки его больше не вызывают
    // метод сохранен вместе с диалогом, ввод работает в карточках, изменение шага через ячейки
    private async void OpenFilterKernelEditor(MainViewModel viewModel, ImageEditStep? step = null) {
        if (filterKernelDialogOpen || !ReferenceEquals(DataContext, viewModel)) {
            return;
        }
        bool editing = step is not null;
        FilterKernel initialKernel;
        if (editing) {
            if (!viewModel.AdvancedSteps.Contains(step!) || step!.Type != ImageOperationType.CustomFilter ||
                step.KernelSnapshot is not FilterKernel snapshot) {
                viewModel.StatusMessage = "Пользовательское ядро выбранного шага недоступно";
                return;
            }
            initialKernel = snapshot;
        }
        else {
            initialKernel = viewModel.SelectedFilterKernel;
        }
        filterKernelDialogOpen = true;
        try {
            FilterKernelWindow dialog = new FilterKernelWindow(
                initialKernel,
                editing ? "Матрица выбранного шага" : "Пользовательская матрица",
                editing ? "Изменить шаг" : "Добавить в очередь",
                kernel => {
                    if (!ReferenceEquals(DataContext, viewModel)) {
                        return "Очередь изменилась, откройте редактор заново";
                    }
                    if (step is not null) {
                        return viewModel.TryUpdateCustomFilterKernel(step, kernel, out string editError)
                            ? null : editError;
                    }
                    // сначала применяем черновик, будущий выбор запоминаем только после успеха
                    if (!viewModel.TryAddCustomFilterStep(kernel, out string addError)) {
                        return addError;
                    }
                    viewModel.SetFilterKernel(kernel);
                    return null;
                });
            // отдельный экземпляр на каждое открытие, владелец блокируется до закрытия
            await dialog.ShowDialog(this);
        }
        catch (Exception exception) {
            viewModel.StatusMessage = $"Не удалось открыть редактор фильтра: {exception.Message}";
        }
        finally {
            filterKernelDialogOpen = false;
        }
    }
}
