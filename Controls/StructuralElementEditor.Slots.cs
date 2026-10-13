using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    private void RestoreSavedSlots() { // восстанавливаем снимки после создания кнопок, не загружаем их в черновик
        if (!slotStorage.TryLoad(out StructuralElement?[] restored, out string error)) {
            SetSlotsStatus(error, true);
            return;
        }
        Array.Copy(restored, savedSlots, SavedSlotCount);
        UpdateSavedSlots();
        int occupied = 0;
        foreach (StructuralElement? element in savedSlots) {
            if (element is not null) {
                occupied++;
            }
        }
        SetSlotsStatus(occupied > 0 ? $"Восстановлено сохраненных матриц: {occupied}" : "");
    }

    private bool TryStoreSlot(int index, StructuralElement? element) { // меняем слот в памяти только после успешной записи полного документа
        StructuralElement?[] proposed = (StructuralElement?[])savedSlots.Clone();
        proposed[index] = element;
        if (!slotStorage.TrySave(proposed, out string error)) {
            SetSlotsStatus(error, true);
            return false;
        }
        Array.Copy(proposed, savedSlots, SavedSlotCount);
        UpdateSavedSlots();
        return true;
    }

    private Border CreateSavedElementsCard() { // собираем четыре слота отдельно от настроек, чтобы они не увеличивали поле матрицы
        StackPanel content = new StackPanel {
            Spacing = 10,
            Children = {
                new TextBlock {
                    Text = "Сохраненные матрицы",
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock {
                    Text = "Сохраняются на этом компьютере. Загрузка меняет черновик; применение выполняется отдельно.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        Grid slots = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12
        };
        for (int index = 0; index < SavedSlotCount; index++) {
            Border slotCard = CreateSavedSlotCard(index);
            Grid.SetColumn(slotCard, index);
            slots.Children.Add(slotCard);
        }
        content.Children.Add(slots);
        content.Children.Add(slotsStatusText);
        UpdateSavedSlots();
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = content
        };
    }

    private Border CreateSavedSlotCard(int index) { // кнопки запоминают свой индекс и работают только с этим слотом
        int slotIndex = index;
        TextBlock details = new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            MinHeight = 42
        };
        Button saveButton = new Button { Content = "Сохранить" };
        Button loadButton = new Button { Content = "Загрузить", IsEnabled = false };
        Button clearButton = new Button { Content = "Очистить", IsEnabled = false };
        saveButton.Click += (_, _) => SaveDraftToSlot(slotIndex);
        loadButton.Click += (_, _) => LoadSavedSlot(slotIndex);
        clearButton.Click += (_, _) => ClearSavedSlot(slotIndex);
        slotDescriptions[index] = details;
        slotSaveButtons[index] = saveButton;
        slotLoadButtons[index] = loadButton;
        slotClearButtons[index] = clearButton;
        return new Border {
            Padding = new Thickness(10),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel {
                Spacing = 6,
                Children = {
                    new TextBlock {
                        Text = $"Ячейка {index + 1}",
                        FontWeight = FontWeight.SemiBold
                    },
                    details,
                    new WrapPanel {
                        ItemSpacing = 6,
                        LineSpacing = 6,
                        Children = { saveButton, loadButton, clearButton }
                    }
                }
            }
        };
    }

    private void UpdateSavedSlots() { // описания и доступность кнопок берем из снимков, а не из текущего черновика
        for (int index = 0; index < SavedSlotCount; index++) {
            StructuralElement? element = savedSlots[index];
            bool occupied = element is not null;
            slotSaveButtons[index].Content = occupied ? "Заменить" : "Сохранить";
            slotLoadButtons[index].IsEnabled = occupied;
            slotClearButtons[index].IsEnabled = occupied;
            slotDescriptions[index].Text = element is null
                ? "Пусто — сохраните текущий черновик"
                : $"Строк (Y): {element.Rows}, столбцов (X): {element.Columns}\n" +
                  $"Активных: {element.ActiveCellCount}\n" +
                  $"Опора: X = {element.AnchorColumn + 1}, Y = {element.AnchorRow + 1}";
        }
    }

    private void SetSlotsStatus(string message, bool isError = false) { // показываем результат операции рядом со слотами
        slotsStatusText.Text = message;
        slotsStatusText.Foreground = isError ? Brushes.OrangeRed : Brushes.Gray;
    }

    private void SaveDraftToSlot(int index) { // кладем независимый снимок только после проверки, прежний слот при ошибке остается
        if (!TryCreateDraftSnapshot(out StructuralElement? snapshot) || snapshot is null) {
            SetSlotsStatus(statusText.Text ?? "Не удалось сохранить матрицу", true);
            return;
        }
        if (!TryStoreSlot(index, snapshot)) {
            return;
        }
        SetSlotsStatus($"Черновик сохранен в ячейку {index + 1}");
    }

    private void LoadSavedSlot(int index) { // загружаем копию без применения, при новом размере включаем вписывание
        StructuralElement? element = savedSlots[index];
        if (element is null) {
            SetSlotsStatus($"Ячейка {index + 1} пуста", true);
            return;
        }
        bool dimensionsChanged = element.Rows != cells.GetLength(0) ||
            element.Columns != cells.GetLength(1);
        // загрузка меняет только черновик, последний примененный savedElement остается прежним
        LoadDraft(element);
        if (dimensionsChanged) {
            FitMatrix();
        }
        SetSlotsStatus($"Ячейка {index + 1} загружена в черновик");
    }

    private void ClearSavedSlot(int index) { // очищаем один слот, черновик и примененный элемент не трогаем
        if (!TryStoreSlot(index, null)) {
            return;
        }
        SetSlotsStatus($"Ячейка {index + 1} очищена");
    }
}
