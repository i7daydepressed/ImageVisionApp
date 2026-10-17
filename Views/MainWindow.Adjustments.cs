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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 52,
            Minimum = sliderMinimum,
            Maximum = sliderMaximum,
            Value = initialValue,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };
        TextBlock errorText = new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.OrangeRed,
            IsVisible = false
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
            errorText.IsVisible = !isValid;
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
                errorText.IsVisible = true;
                return;
            }
            if (DataContext is MainViewModel viewModel) {
                // шаг получает число из поля, даже если оно вышло за диапазон ползунка
                viewModel.AddAdvancedStep(operationType, value);
            }
        };

        StackPanel controls = new StackPanel {
            Spacing = 6,
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
            MinHeight = 160, // высота карточки
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
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
        return ImageStepParameterRules.TryReadValue(text, minimum, maximum, out value, out error);
    }
}
