using System.Collections.Generic;
using ImageVisionApp.Controls;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    // держим только показанные контролы, при уходе строки из дерева удаляем ее запись
    private readonly Dictionary<QueueStepPositionControl, ImageEditStep?> queueStepPositions =
        new Dictionary<QueueStepPositionControl, ImageEditStep?>();

    private QueueStepPositionControl CreateQueueStepPositionControl(ImageEditStep? step) {
        QueueStepPositionControl control = new QueueStepPositionControl(
            targetPosition => MoveQueueStepToPosition(step, targetPosition));
        control.AttachedToVisualTree += (_, _) => {
            queueStepPositions[control] = step;
            MainViewModel? viewModel = DataContext as MainViewModel;
            int index = step is null ? -1 : viewModel?.AdvancedSteps.IndexOf(step) ?? -1;
            control.SetPosition(index, viewModel?.AdvancedSteps.Count ?? 0);
        };
        control.DetachedFromVisualTree += (_, _) => queueStepPositions.Remove(control);
        return control;
    }

    private void RefreshQueueStepPositions() {
        MainViewModel? viewModel = DataContext as MainViewModel;
        int count = viewModel?.AdvancedSteps.Count ?? 0;
        foreach (KeyValuePair<QueueStepPositionControl, ImageEditStep?> entry in queueStepPositions) {
            int index = entry.Value is null ? -1 : viewModel?.AdvancedSteps.IndexOf(entry.Value) ?? -1;
            entry.Key.SetPosition(index, count);
        }
    }

    private void MoveQueueStepToPosition(ImageEditStep? step, int position) {
        if (DataContext is not MainViewModel viewModel) {
            return;
        }
        if (!viewModel.TryMoveAdvancedStepToPosition(step, position, out string error)) {
            viewModel.ShowError(error);
        }
    }
}
