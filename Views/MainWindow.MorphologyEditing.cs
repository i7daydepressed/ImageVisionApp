using System;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool morphologySlotsDialogOpen;

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
                editor.GetSavedSlotSnapshot,
                (index, element) => {
                    if (!ReferenceEquals(DataContext, viewModel) || !viewModel.AdvancedSteps.Contains(step)) {
                        return "Выбранный шаг больше не находится в текущей очереди";
                    }
                    return editor.TrySaveElementToSlot(index, element, out string error) ? null : error;
                },
                element => {
                    if (!ReferenceEquals(DataContext, viewModel)) {
                        return "Очередь изменилась, откройте шаг заново";
                    }
                    return viewModel.TryUpdateMorphologyStepElement(step, element, out string error) ? null : error;
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
