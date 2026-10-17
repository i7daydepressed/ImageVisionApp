using System;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ImageVisionApp.Controls;

// номер относится к месту в очереди, сам ImageEditStep здесь не изменяем
public sealed class QueueStepPositionControl : UserControl {
    private readonly Action<int> moveToPosition;
    private readonly Button moveUpButton;
    private readonly Button moveDownButton;
    private readonly Button numberButton = new Button {
        Width = 50,
        Height = 26,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(2, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    private readonly TextBox numberInput = new TextBox {
        Width = 50,
        Height = 26,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(2, 0),
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        AcceptsReturn = false,
        IsVisible = false
    };
    private int position;
    private int stepCount;
    private bool editing;
    private bool attached;
    private int editVersion;

    public QueueStepPositionControl(Action<int> moveToPosition) {
        ArgumentNullException.ThrowIfNull(moveToPosition);
        this.moveToPosition = moveToPosition;
        moveUpButton = CreateChevronButton(true);
        moveDownButton = CreateChevronButton(false);
        Content = CreateLayout();
        ToolTip.SetTip(numberButton, "Изменить номер шага");
        ToolTip.SetTip(moveUpButton, "Переместить на шаг вверх");
        ToolTip.SetTip(moveDownButton, "Переместить на шаг вниз");
        numberButton.Click += (_, _) => BeginEditing();
        moveUpButton.Click += (_, _) => MoveByOffset(-1);
        moveDownButton.Click += (_, _) => MoveByOffset(1);
        numberInput.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) {
                e.Handled = true;
                FinishEditing(true, true);
            }
            else if (e.Key == Key.Escape) {
                e.Handled = true;
                FinishEditing(false, true);
            }
        };
        numberInput.LostFocus += (_, _) => FinishEditing(true, false);
        AttachedToVisualTree += (_, _) => attached = true;
        DetachedFromVisualTree += (_, _) => {
            attached = false;
            FinishEditing(false, false);
        };
        SetPosition(-1, 0);
    }

    private Grid CreateLayout() {
        Grid numberHost = new Grid { Height = 26 };
        numberHost.Children.Add(numberButton);
        numberHost.Children.Add(numberInput);
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 2,
            Width = 54,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(moveUpButton);
        Grid.SetRow(numberHost, 1);
        layout.Children.Add(numberHost);
        Grid.SetRow(moveDownButton, 2);
        layout.Children.Add(moveDownButton);
        return layout;
    }

    private Button CreateChevronButton(bool up) {
        Avalonia.Controls.Shapes.Path chevron = new Avalonia.Controls.Shapes.Path {
            Data = Geometry.Parse(up ? "M 1,5 L 5,1 L 9,5" : "M 1,1 L 5,5 L 9,1"),
            Stroke = Brushes.Gray,
            StrokeThickness = 1.5,
            Width = 12,
            Height = 7,
            Stretch = Stretch.Uniform
        };
        Button button = new Button {
            Content = chevron,
            Width = 32,
            Height = 16,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        // отменяем числовой ввод до Click: ListBox может забрать фокус уже при нажатии
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) => {
            if (e.GetCurrentPoint(button).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed) {
                FinishEditing(false, false);
            }
        }, RoutingStrategies.Tunnel);
        return button;
    }

    public void SetPosition(int index, int count) {
        position = index >= 0 && index < count ? index + 1 : 0;
        stepCount = count;
        numberButton.Content = position > 0
            ? position.ToString(CultureInfo.InvariantCulture)
            : "—";
        numberButton.IsEnabled = position > 0;
        moveUpButton.IsEnabled = position > 1;
        moveDownButton.IsEnabled = position > 0 && position < stepCount;
        if (position == 0) {
            FinishEditing(false, false);
        }
    }

    private void BeginEditing() {
        if (editing || position == 0) {
            return;
        }
        editing = true;
        int version = ++editVersion;
        numberInput.Text = position.ToString(CultureInfo.InvariantCulture);
        numberButton.IsVisible = false;
        numberInput.IsVisible = true;
        // фокус ставим после появления поля, устаревший запрос не открывает отмененный ввод
        Dispatcher.UIThread.Post(() => {
            if (!editing || !attached || version != editVersion) {
                return;
            }
            numberInput.Focus();
            numberInput.SelectAll();
        }, DispatcherPriority.Input);
    }

    private bool TryReadPosition(out int targetPosition) {
        targetPosition = position;
        if (stepCount <= 0 || !BigInteger.TryParse(
            numberInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out BigInteger requested)) {
            return false;
        }
        // большое целое тоже идет в конец, преобразуем в int только после ограничения
        targetPosition = requested < BigInteger.One
            ? 1
            : requested > stepCount ? stepCount : (int)requested;
        return true;
    }

    private void FinishEditing(bool apply, bool restoreFocus) {
        if (!editing) {
            return;
        }
        int targetPosition = position;
        bool move = apply && TryReadPosition(out targetPosition) && targetPosition != position;
        // Enter скрывает поле и вызывает LostFocus: закрываем сессию до смены видимости
        editing = false;
        editVersion++;
        numberInput.IsVisible = false;
        numberButton.IsVisible = true;
        if (move) {
            moveToPosition(targetPosition);
        }
        if (restoreFocus && attached && numberButton.IsEnabled) {
            numberButton.Focus();
        }
    }

    private void MoveByOffset(int offset) {
        // шеврон отменяет числовой черновик и выполняет только свое перемещение
        FinishEditing(false, false);
        int targetPosition = position + offset;
        if (position > 0 && targetPosition >= 1 && targetPosition <= stepCount) {
            moveToPosition(targetPosition);
        }
    }
}
