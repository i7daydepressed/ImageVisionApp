using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Border CreateAdjustmentCard(
        string title,
        ImageOperationType operationType,
        double sliderMinimum,
        double sliderMaximum,
        double initialValue,
        double inputMinimum,
        double? inputMaximum,
        string buttonText) {

        TextBox valueInput = new TextBox {
            Width = 120,
            Text = initialValue.ToString("G", CultureInfo.CurrentCulture)
        };
        StackPanel valueRow = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = {
                new TextBlock { Text = "Значение:", VerticalAlignment = VerticalAlignment.Center },
                valueInput,
                new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center }
            }
        };
        Slider slider = new Slider {
            Width = 260,
            Minimum = sliderMinimum,
            Maximum = sliderMaximum,
            Value = initialValue,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };
        TextBlock errorText = new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.OrangeRed
        };
        Button addButton = new Button {
            Content = buttonText,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        bool updatingSliderFromInput = false;
        valueInput.TextChanged += (_, _) => {
            bool isValid = TryReadAdjustmentValue(
                valueInput.Text, inputMinimum, inputMaximum,
                out double value, out string error);
            errorText.Text = error;
            addButton.IsEnabled = isValid;
            if (!isValid) {
                return;
            }

            // ограничиваем только положение ползунка, число в поле сохраняем полностью
            updatingSliderFromInput = true;
            try {
                slider.Value = Math.Clamp(value, sliderMinimum, sliderMaximum);
            }
            finally {
                updatingSliderFromInput = false;
            }
        };
        slider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty && !updatingSliderFromInput) {
                // движение ползунка меняет выбранное число, но еще не меняет изображение
                valueInput.Text = slider.Value.ToString("G", CultureInfo.CurrentCulture);
            }
        };
        addButton.Click += (_, _) => {
            if (!TryReadAdjustmentValue(
                valueInput.Text, inputMinimum, inputMaximum,
                out double value, out string error)) {
                errorText.Text = error;
                return;
            }
            if (DataContext is MainViewModel viewModel) {
                // шаг получает число из поля, даже если оно вышло за диапазон ползунка
                viewModel.AddAdvancedStep(operationType, value);
            }
        };

        StackPanel controls = new StackPanel {
            Spacing = 8,
            Children = {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                valueRow,
                slider,
                errorText,
                addButton
            }
        };
        return new Border {
            Width = 286,
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = controls
        };
    }

    private static bool TryReadAdjustmentValue(
        string? text,
        double minimum,
        double? maximum,
        out double value,
        out string error) {

        // точку и запятую принимаем как десятичный разделитель, разделители тысяч не используем
        string normalizedText = (text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(normalizedText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !double.IsFinite(value)) {
            error = "Введите конечное число, например 12,5 или 12.5";
            return false;
        }
        if (value < minimum || (maximum.HasValue && value > maximum.Value)) {
            error = maximum.HasValue
                ? $"Допустимое значение: от {minimum} до {maximum.Value}"
                : $"Допустимое значение: не меньше {minimum}";
            return false;
        }
        error = "";
        return true;
    }
}
