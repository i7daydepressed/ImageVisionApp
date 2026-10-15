using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Border CreateAdvancedToolsPanel() {
        Border quickActionsCard = CreateQuickActionsCard();
        Border brightnessCard = CreateBrightnessCard();
        Border saturationCard = CreateSaturationCard();
        Border contrastCard = CreateContrastCard();
        Border morphologyCard = CreateMorphologyCard();
        Border filtersCard = CreateFiltersCard();
        Border[] cards = new Border[] {
            quickActionsCard, brightnessCard, saturationCard,
            contrastCard, morphologyCard, filtersCard
        };
        Grid toolsLayout = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (Border card in cards) {
            // шириной управляет общая сетка, высота строки учитывает содержимое карточек
            card.Width = double.NaN;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            toolsLayout.Children.Add(card);
        }

        int currentColumns = 0;
        void UpdateToolsLayout(double availableWidth) {
            // пороги UI1 сохраняем, первая колонка подстраивается под текст кнопок
            int columns = availableWidth >= 1180 ? 4 :
                availableWidth >= 882 ? 3 : availableWidth >= 584 ? 2 : 1;
            if (columns == currentColumns) {
                return;
            }
            currentColumns = columns;
            toolsLayout.ColumnDefinitions = new ColumnDefinitions(columns switch {
                4 => "Auto,*,*,*",
                3 => "Auto,*,*",
                2 => "Auto,*",
                _ => "*"
            });
            toolsLayout.RowDefinitions = new RowDefinitions(columns switch {
                4 => "Auto,Auto",
                3 => "Auto,Auto,Auto",
                2 => "Auto,Auto,Auto,Auto",
                _ => "Auto,Auto,Auto,Auto,Auto,Auto"
            });
            quickActionsCard.MinHeight = columns == 1 ? 0 : 332;

            // каждый режим задает положение заново, чтобы прежние span не оставались при сужении
            switch (columns) {
                case 4:
                    PlaceToolsCard(quickActionsCard, 0, 0, rowSpan: 2);
                    PlaceToolsCard(brightnessCard, 0, 1);
                    PlaceToolsCard(saturationCard, 0, 2);
                    PlaceToolsCard(contrastCard, 0, 3);
                    PlaceToolsCard(morphologyCard, 1, 1, columnSpan: 2);
                    PlaceToolsCard(filtersCard, 1, 3);
                    break;
                case 3:
                    PlaceToolsCard(quickActionsCard, 0, 0, rowSpan: 2);
                    PlaceToolsCard(brightnessCard, 0, 1);
                    PlaceToolsCard(saturationCard, 0, 2);
                    PlaceToolsCard(morphologyCard, 1, 1, columnSpan: 2);
                    PlaceToolsCard(contrastCard, 2, 0);
                    PlaceToolsCard(filtersCard, 2, 1, columnSpan: 2);
                    break;
                case 2:
                    PlaceToolsCard(quickActionsCard, 0, 0, rowSpan: 2);
                    PlaceToolsCard(brightnessCard, 0, 1);
                    PlaceToolsCard(saturationCard, 1, 1);
                    PlaceToolsCard(contrastCard, 2, 0);
                    PlaceToolsCard(filtersCard, 2, 1);
                    PlaceToolsCard(morphologyCard, 3, 0, columnSpan: 2);
                    break;
                default:
                    for (int index = 0; index < cards.Length; index++) {
                        PlaceToolsCard(cards[index], index, 0);
                    }
                    break;
            }
        }

        // контролы создаются один раз, при изменении ширины меняются только координаты сетки
        toolsLayout.SizeChanged += (_, _) => UpdateToolsLayout(toolsLayout.Bounds.Width);
        UpdateToolsLayout(0);

        // общая рамка охватывает сетку всех шести карточек
        return new Border {
            Margin = new Thickness(12),
            Padding = new Thickness(12),
            MinHeight = 180,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = toolsLayout
        };
    }

    private static void PlaceToolsCard(
        Control card, int row, int column, int rowSpan = 1, int columnSpan = 1) {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetRowSpan(card, rowSpan);
        Grid.SetColumnSpan(card, columnSpan);
    }

    private Border CreateQuickActionsCard() {
        Button addRotationButton = new Button {//ротейшен батен
            Content = new TextBlock { Text = "Поворот 90°", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addRotationButton.Click += (_, _) => {//подписываем на ад
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.Rotation, 90);
            }
        };

        // собираем быстрые действия в прежнем порядке, затем сброс и сохранение
        StackPanel toolsContent = new StackPanel {
            Spacing = 8 // расстояние между заголовком и кнопкой
        };
        toolsContent.Children.Add(new TextBlock {
            Text = "Инструменты",
            FontWeight = FontWeight.SemiBold
        });
        toolsContent.Children.Add(addRotationButton);

        Button addGrayscaleButton = new Button {//кнопградации
            Content = new TextBlock { Text = "Градации серого", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addGrayscaleButton.Click += (_, _) => {//подписываем кнопк
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.Grayscale);
            }
        };
        toolsContent.Children.Add(addGrayscaleButton);
        // линейная коррекция добавляется как отдельный шаг после уже выбранных действий
        Button addLinearCorrectionButton = new Button {
            Content = new TextBlock { Text = "Линейная коррекция ЧБ", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addLinearCorrectionButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.LinearGrayscaleCorrection);
            }
        };
        toolsContent.Children.Add(addLinearCorrectionButton);
        // нелинейная коррекция тоже становится отдельным шагом очереди
        Button addNonlinearCorrectionButton = new Button {
            Content = new TextBlock { Text = "Нелинейная коррекция ЧБ", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addNonlinearCorrectionButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.NonlinearGrayscaleCorrection);
            }
        };
        toolsContent.Children.Add(addNonlinearCorrectionButton);
        // сохраняем результат текущей очереди через обработчик расширенного режима
        Button saveAdvancedButton = new Button {
            Content = new TextBlock { Text = "Сохранить результат", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        saveAdvancedButton.Click += SaveAdvancedResultButton_OnClick;
        // сбрасываем всю очередь; результат снова строится из исходного изображения
        Button resetAdvancedButton = new Button {
            Content = new TextBlock { Text = "Сбросить изменения", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        resetAdvancedButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.ResetAdvancedQueue();
            }
        };
        StackPanel resultActions = new StackPanel {
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { resetAdvancedButton, saveAdvancedButton }
        };
        Grid cardContent = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        cardContent.Children.Add(toolsContent);
        Grid.SetRow(resultActions, 2);
        cardContent.Children.Add(resultActions);

        return new Border {
            MinHeight = 332,
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = cardContent
        };
    }
}
