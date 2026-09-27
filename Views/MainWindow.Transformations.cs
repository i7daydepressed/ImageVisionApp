using System;
using System.Linq;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private StackPanel CreateTransformationsPanel() {
        StackPanel transformations = new StackPanel { Spacing = 10 };
        transformations.Children.Add(new TextBlock {
            Text = "Преобразования",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        });

        // нопк и ползунк
        TextBlock actionsHeading = CreateSectionHeading("Основные действия");
        actionsHeading.Margin = new Thickness(0, 10, 0, 2);
        transformations.Children.Add(actionsHeading);
        transformations.Children.Add(CreateTransformationCheckBox("Градации серого",
            viewModel => viewModel.Settings.IsGrayscaleEnabled, ConvertToGrayscaleButton_OnClick));
        transformations.Children.Add(CreateSectionSeparator());

        // 0 означает, что яркость пока не изменена
        AddAdjustmentControls(transformations, "Яркость",
            viewModel => viewModel.Settings.BrightnessAdjustment, -100, 100,
            "Сбросить яркость", ResetBrightnessButton_OnClick, new Thickness(0));

        // 100 процентов означает исходную насыщенность;
        // 0 превращает цвета в серые, больше 100 усиливает цвета
        AddAdjustmentControls(transformations, "Насыщенность",
            viewModel => viewModel.Settings.SaturationPercentage, 0, 200,
            "Сбросить насыщенность", ResetSaturationButton_OnClick, new Thickness(0, 6, 0, 0),
            showPercentage: true);

        // 0 означает, что контрастность пока не изменена
        AddAdjustmentControls(transformations, "Контрастность",
            viewModel => viewModel.Settings.ContrastAdjustment, -100, 100,
            "Сбросить контрастность", ResetContrastButton_OnClick, new Thickness(0, 6, 0, 0));
        transformations.Children.Add(CreateActionButton("Повернуть на 90°", RotateClockwiseButton_OnClick));
        transformations.Children.Add(CreateSectionSeparator());

        // отдельный раздел из задания для чёрно-белого изображения
        transformations.Children.Add(CreateSectionHeading("Коррекция ЧБ"));
        transformations.Children.Add(CreateTransformationCheckBox("Линейная коррекция",
            viewModel => viewModel.IsLinearCorrectionEnabled, ApplyLinearGrayscaleCorrectionButton_OnClick));
        transformations.Children.Add(CreateTransformationCheckBox("Нелинейная коррекция",
            viewModel => viewModel.IsNonlinearCorrectionEnabled, ApplyNonlinearGrayscaleCorrectionButton_OnClick));
        transformations.Children.Add(CreateSectionSeparator());

        // сами графики сюда пихать тесно; кнопка открывает отдельное окно гистограмм
        transformations.Children.Add(CreateSectionHeading("Гистограммы"));
        transformations.Children.Add(CreateActionButton("Открыть гистограммы", OpenHistogramsButton_OnClick));
        transformations.Children.Add(CreateSectionSeparator());

        // действия уже не над отдельным параметром, а над всем результатом
        transformations.Children.Add(CreateSectionHeading("Результат"));
        transformations.Children.Add(CreateActionButton("Сбросить изменения", ResetAllTransformationsButton_OnClick));
        transformations.Children.Add(CreateActionButton("Сохранить результат", SaveResultButton_OnClick));
        return transformations;
    }

    private static CheckBox CreateTransformationCheckBox(
        string text,
        Expression<Func<MainViewModel, bool>> expression,
        EventHandler<RoutedEventArgs> onClick) {

        CheckBox checkBox = new CheckBox {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // Состояние меняет ViewModel в обработчике Click; TwoWay переключил бы его дважды.
        checkBox.Bind(ToggleButton.IsCheckedProperty,
            CompiledBinding.Create(expression, mode: BindingMode.OneWay));
        checkBox.Click += onClick;
        return checkBox;
    }

    private static void AddAdjustmentControls(
        StackPanel panel,
        string title,
        Expression<Func<MainViewModel, double>> expression,
        int minimum,
        int maximum,
        string resetText,
        EventHandler<RoutedEventArgs> onReset,
        Thickness titleMargin,
        bool showPercentage = false) {

        panel.Children.Add(new TextBlock {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Margin = titleMargin
        });

        // поле и ползунок смотрят на одну настройку, вручную перекидывать значения не надо
        NumericUpDown numericInput = new NumericUpDown {
            Width = 52,
            Height = 24,
            Padding = new Thickness(2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ShowButtonSpinner = false,
            Minimum = minimum,
            Maximum = maximum,
            Increment = 1,
            FormatString = "0",
            ClipValueToMinMax = true
        };
        // у поля decimal?, у Settings double; перевод типов делает привязка Avalonia
        numericInput.Bind(NumericUpDown.ValueProperty,
            CompiledBinding.Create(expression, mode: BindingMode.TwoWay));

        StackPanel currentValue = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { new TextBlock { Text = "Текущее значение:" }, numericInput }
        };
        if (showPercentage) {
            currentValue.Children.Add(new TextBlock { Text = "%" });
        }
        panel.Children.Add(currentValue);

        Slider slider = new Slider {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };
        slider.Bind(RangeBase.ValueProperty,
            CompiledBinding.Create(expression, mode: BindingMode.TwoWay));
        panel.Children.Add(slider);
        panel.Children.Add(CreateActionButton(resetText, onReset, HorizontalAlignment.Right));
    }

    private void InitializeNumericInputs() {
        // EndInit работает после присоединения к окну. Как в XAML, текст числовых
        // полей должен обновляться даже до первого показа вкладки преобразований.
        foreach (NumericUpDown numericInput in this.GetLogicalDescendants().OfType<NumericUpDown>()) {
            numericInput.BeginInit();
            numericInput.EndInit();
        }
    }

    private static Button CreateActionButton(
        string text,
        EventHandler<RoutedEventArgs> onClick,
        HorizontalAlignment alignment = HorizontalAlignment.Stretch) {

        Button button = new Button { Content = text, HorizontalAlignment = alignment };
        button.Click += onClick;
        return button;
    }
}
