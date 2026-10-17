using System;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool morphologySlotsDialogOpen;

    // прежнее имя сохранено, окно теперь обменивает и булевые, и числовые снимки
    private async void OpenMorphologyStepSlots(ImageEditStep step, MainViewModel viewModel) {
        if (morphologySlotsDialogOpen) {
            return;
        }
        if (structuralElementEditor is not StructuralElementEditor editor) {
            viewModel.StatusMessage = "Редактор сохраненных матриц недоступен";
            return;
        }
        morphologySlotsDialogOpen = true;
        try {
            MorphologyStepSlotsWindow dialog = new MorphologyStepSlotsWindow(
                step,
                editor.SavedSlotCapacity,
                editor.GetSavedMatrixSlotSnapshot,
                (index, element) => {
                    if (!ReferenceEquals(DataContext, viewModel) || !viewModel.AdvancedSteps.Contains(step)) {
                        return "Выбранный шаг больше не находится в текущей очереди";
                    }
                    return editor.TrySaveMatrixToSlot(index, element, out string error) ? null : error;
                },
                element => {
                    if (!ReferenceEquals(DataContext, viewModel)) {
                        return "Очередь изменилась, откройте шаг заново";
                    }
                    if (step.Type == ImageOperationType.CustomFilter && element.Kernel is FilterKernel kernel) {
                        return viewModel.TryUpdateCustomFilterKernel(step, kernel, out string filterError) ? null : filterError;
                    }
                    if (step.Type != ImageOperationType.CustomFilter && element.Element is StructuralElement morphology) {
                        return viewModel.TryUpdateMorphologyStepElement(step, morphology, out string error) ? null : error;
                    }
                    return "Тип матрицы ячейки не подходит для выбранного шага";
                });
            // новый экземпляр на каждое открытие, закрытый Window повторно не показываем
            await dialog.ShowDialog(this);
        }
        catch (Exception exception) {
            viewModel.StatusMessage = $"Не удалось открыть матрицу шага: {exception.Message}";
        }
        finally {
            morphologySlotsDialogOpen = false;
        }
    }
}
