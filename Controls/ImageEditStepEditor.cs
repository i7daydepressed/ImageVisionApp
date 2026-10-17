using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

// хранит только черновик параметра, подтверждение передает владельцу очереди
public sealed class ImageEditStepEditor : UserControl {
    // владелец подтверждает изменение: null означает успех, строка содержит ошибку
    private readonly Func<ImageEditStep, double, string?> applyParameter;
    private ImageEditStep? selectedStep;
    // индекс нужен только для подписи, редактируемый объект хранится в selectedStep
    private int selectedIndex = -1;
    // заполнение поля и ползунка из кода не принимаем за новый ручной ввод
    private bool updatingControls;
    private readonly int[] rotationAngles = new int[] { 90, 180, 270 };
    private readonly TextBlock header = new TextBlock {
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox valueInput = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider slider = new Slider {
        Height = 38,
        TickFrequency = 1,
        IsSnapToTickEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox rotationInput = new ComboBox {
        ItemsSource = new string[] { "90°", "180°", "270°" },
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackPanel numericPanel = new StackPanel { Spacing = 6 };
    private readonly StackPanel rotationPanel = new StackPanel { Spacing = 6 };
    private readonly TextBlock status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button applyButton = new Button { Content = "Изменить шаг", IsEnabled = false };
    private readonly Button cancelButton = new Button { Content = "Отмена", IsEnabled = false };
    private readonly WrapPanel buttons = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };

    public ImageEditStep? SelectedStep => selectedStep;

    public ImageEditStepEditor(Func<ImageEditStep, double, string?> applyParameter) {
        ArgumentNullException.ThrowIfNull(applyParameter);
        this.applyParameter = applyParameter;
        Content = CreateLayout();
        valueInput.TextChanged += (_, _) => UpdateDraft();
        slider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty && !updatingControls && selectedStep is not null) {
                valueInput.Text = slider.Value.ToString("G", CultureInfo.CurrentCulture);
            }
        };
        rotationInput.SelectionChanged += (_, _) => UpdateDraft();
        applyButton.Click += (_, _) => ApplyDraft();
        cancelButton.Click += (_, _) => SetStep(null);
        SetStep(null);
    }

    private Border CreateLayout() {
        Grid valueRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        valueRow.Children.Add(valueInput);
        TextBlock units = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(units, 1);
        valueRow.Children.Add(units);
        numericPanel.Children.Add(new TextBlock { Text = "Значение" });
        numericPanel.Children.Add(valueRow);
        numericPanel.Children.Add(slider);
        rotationPanel.Children.Add(new TextBlock { Text = "Угол поворота" });
        rotationPanel.Children.Add(rotationInput);
        buttons.Children.Add(applyButton);
        buttons.Children.Add(cancelButton);
        return new Border {
            Padding = new Thickness(10),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel {
                Spacing = 8,
                Children = { header, hint, numericPanel, rotationPanel, status, buttons }
            }
        };
    }

    public void SetStep(ImageEditStep? step, int index = -1) { // новый выбор загружает черновик, null возвращает приглашение
        selectedIndex = index;
        if (step is not null && ReferenceEquals(step, selectedStep)) {
            // при перестановке обновляем номер, введенный черновик не сбрасываем
            UpdateHeader();
            return;
        }
        selectedStep = step;
        LoadDraft();
    }

    private void UpdateHeader() {
        header.Text = selectedStep is null
            ? "Изменение шага"
            : selectedIndex >= 0
                ? $"Шаг {selectedIndex + 1}: {selectedStep.DisplayName}"
                : $"Выбранный шаг: {selectedStep.DisplayName}";
    }

    private void LoadDraft() { // берем примененное значение шага и показываем подходящие контролы
        updatingControls = true;
        try {
            UpdateHeader();
            bool hasStep = selectedStep is not null;
            bool numeric = hasStep && ImageStepParameterRules.TryGetNumericRange(
                selectedStep!.Type, out _, out _);
            numericPanel.IsVisible = numeric;
            rotationPanel.IsVisible = hasStep && selectedStep!.Type == ImageOperationType.Rotation;
            buttons.IsVisible = hasStep;
            cancelButton.IsEnabled = hasStep;
            hint.Text = !hasStep
                ? "Нажмите «Изменить» у нужного действия в очереди"
                : ImageStepParameterRules.CanEdit(selectedStep!.Type)
                    ? "Ввод меняет черновик. Подтвердите, чтобы изменить этот шаг."
                    : "У этого действия нет редактируемого числового параметра";
            if (numeric) {
                ImageStepParameterRules.TryGetNumericRange(selectedStep!.Type, out double minimum, out double? maximum);
                slider.Minimum = minimum;
                slider.Maximum = maximum ?? 200;
                valueInput.Text = selectedStep!.Parameter.ToString("G", CultureInfo.CurrentCulture);
                // ограничиваем только ползунок, насыщенность выше 200 остается в числовом поле
                slider.Value = Math.Clamp(selectedStep!.Parameter, slider.Minimum, slider.Maximum);
            }
            else if (rotationPanel.IsVisible) {
                rotationInput.SelectedIndex = Array.FindIndex(rotationAngles,
                    angle => angle == selectedStep!.Parameter);
            }
        }
        finally {
            updatingControls = false;
        }
        UpdateDraft();
    }

    private bool TryReadDraft(out double value, out string error) { // читаем число или угол из контролов, Parameter пока не меняем
        value = 0;
        error = "";
        if (selectedStep is null) {
            return false;
        }
        if (selectedStep.Type == ImageOperationType.Rotation) {
            int index = rotationInput.SelectedIndex;
            if (index < 0 || index >= rotationAngles.Length) {
                error = "Выберите угол поворота";
                return false;
            }
            value = rotationAngles[index];
            return ImageStepParameterRules.TryValidate(selectedStep.Type, value, out error);
        }
        if (!ImageStepParameterRules.TryGetNumericRange(selectedStep.Type, out double minimum, out double? maximum)) {
            return false;
        }
        return ImageStepParameterRules.TryReadValue(valueInput.Text, minimum, maximum, out value, out error);
    }

    private void UpdateDraft() { // проверяем ввод и доступность подтверждения без пересчета изображения
        if (updatingControls) {
            return;
        }
        bool valid = TryReadDraft(out double value, out string error);
        applyButton.IsEnabled = valid;
        SetStatus(error, true);
        if (valid && numericPanel.IsVisible) {
            updatingControls = true;
            try {
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            }
            finally {
                updatingControls = false;
            }
        }
    }

    private void ApplyDraft() { // только подтверждение передает черновик владельцу очереди
        ImageEditStep? step = selectedStep;
        if (step is null || !TryReadDraft(out double value, out _)) {
            return;
        }
        double previousValue = step.Parameter;
        string? applyError = applyParameter(step, value);
        if (applyError is not null) {
            // оставляем введенный черновик, чтобы после ошибки можно было повторить подтверждение
            SetStatus(applyError, true);
            return;
        }
        // после callback обновляем карточку, только если этот шаг все еще выбран
        if (ReferenceEquals(step, selectedStep)) {
            LoadDraft();
            SetStatus(previousValue == value ? "Значение не изменилось" : "Шаг изменен", false);
        }
    }

    private void SetStatus(string message, bool isError) {
        status.Text = message;
        status.IsVisible = message.Length > 0;
        status.Foreground = isError ? Brushes.OrangeRed : Brushes.Gray;
    }
}
