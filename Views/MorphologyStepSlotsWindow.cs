using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

public sealed class MorphologyStepSlotsWindow : Window {
    private readonly ImageEditStep step;
    private readonly int slotCount;
    private readonly Func<int, MatrixSlot?> readSlot;
    private readonly Func<int, MatrixSlot, string?> saveSlot;
    private readonly Func<MatrixSlot, string?> applyElement;
    private MatrixSlot? selectedSlotSnapshot;
    private readonly TextBlock stepDetails = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock slotDetails = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox slotInput = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button saveButton = new Button { Content = "Сохранить в ячейку" };
    private readonly Button loadButton = new Button { Content = "Загрузить из ячейки", IsEnabled = false };

    public MorphologyStepSlotsWindow(
        ImageEditStep step, int slotCount,
        Func<int, MatrixSlot?> readSlot,
        Func<int, MatrixSlot, string?> saveSlot,
        Func<MatrixSlot, string?> applyElement) {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(readSlot);
        ArgumentNullException.ThrowIfNull(saveSlot);
        ArgumentNullException.ThrowIfNull(applyElement);
        if (slotCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }
        this.step = step;
        this.slotCount = slotCount;
        this.readSlot = readSlot;
        this.saveSlot = saveSlot;
        this.applyElement = applyElement;
        Title = "Матрица шага";
        Width = 520;
        Height = 420;
        MinWidth = 440;
        MinHeight = 340;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        string[] labels = new string[slotCount];
        for (int index = 0; index < slotCount; index++) {
            labels[index] = $"Ячейка {index + 1}";
        }
        slotInput.ItemsSource = labels;
        slotInput.SelectedIndex = 0;
        slotInput.SelectionChanged += (_, _) => {
            RefreshSelectedSlot();
            SetStatus(string.Empty);
        };
        saveButton.Click += (_, _) => SaveStepToSlot();
        loadButton.Click += (_, _) => LoadSlotIntoStep();
        Button closeButton = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => Close();
        Content = new ScrollViewer {
            Content = new StackPanel {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = {
                    new TextBlock { Text = "Выбранный шаг", FontWeight = FontWeight.SemiBold },
                    stepDetails,
                    new TextBlock { Text = "Ячейка матрицы", FontWeight = FontWeight.SemiBold },
                    slotInput,
                    slotDetails,
                    new TextBlock {
                        Text = "Сохранение заменяет совместимую ячейку. Загрузка совместимой матрицы изменяет только этот шаг и пересчитывает результат; другой тип не применяется.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { saveButton, loadButton } },
                    statusText,
                    closeButton
                }
            }
        };
        RefreshStepDetails();
        RefreshSelectedSlot();
    }

    private void RefreshStepDetails() {
        stepDetails.Text = step.DisplayName;
        saveButton.IsEnabled = GetStepSnapshot() is not null;
    }

    private void RefreshSelectedSlot() {
        int index = slotInput.SelectedIndex;
        selectedSlotSnapshot = index >= 0 && index < slotCount ? readSlot(index) : null;
        loadButton.IsEnabled = selectedSlotSnapshot is not null;
        slotDetails.Text = selectedSlotSnapshot?.Description ?? "Ячейка пуста";
    }

    private void SaveStepToSlot() {
        int index = slotInput.SelectedIndex;
        if (index < 0 || index >= slotCount || GetStepSnapshot() is not MatrixSlot element) {
            SetStatus("Выберите ячейку и шаг с матрицей", true);
            return;
        }
        // сохраняем снимок шага, не текущий общий выбор и не черновик основного редактора
        if (!MatrixSlot.CanReplace(readSlot(index), element, out string typeError)) {
            SetStatus(typeError, true);
            return;
        }
        string? error = saveSlot(index, element);
        if (error is not null) {
            SetStatus(error, true);
            return;
        }
        RefreshSelectedSlot();
        SetStatus($"Матрица шага сохранена в ячейку {index + 1}");
    }

    private void LoadSlotIntoStep() {
        if (selectedSlotSnapshot is not MatrixSlot element) {
            SetStatus("Выбранная ячейка пуста", true);
            return;
        }
        MatrixSlot? stepSnapshot = GetStepSnapshot();
        if (stepSnapshot is null || element.Kind != stepSnapshot.Kind) {
            SetStatus("Тип матрицы ячейки не подходит для выбранного шага. Загрузка не выполнена.", true);
            return;
        }
        // команда сама копирует модель и возвращает прежний снимок при ошибке пересчета
        string? error = applyElement(element);
        RefreshStepDetails();
        SetStatus(error ?? "Матрица ячейки применена к выбранному шагу", error is not null);
    }

    private MatrixSlot? GetStepSnapshot() {
        // снимок берем из выбранного объекта очереди, общий редактор здесь не читается
        if (step.Type == ImageOperationType.CustomFilter) {
            return step.KernelSnapshot is FilterKernel kernel ? MatrixSlot.ForKernel(kernel) : null;
        }
        return step.ElementSnapshot is StructuralElement element ? MatrixSlot.ForElement(element) : null;
    }

    private void SetStatus(string message, bool isError = false) {
        statusText.Text = message;
        statusText.Foreground = isError ? Brushes.OrangeRed : Brushes.Gray;
    }
}
