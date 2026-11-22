using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private const double MinimumComparisonRowHeight = 150;
    private const double MaximumComparisonRowHeight = 190;

    private readonly ImageComparisonSet comparisonSet = new();
    private readonly CancellationTokenSource comparisonLifetime = new();
    private readonly List<Image> comparisonImages = [];
    private readonly Grid comparisonGrid = new() {
        ColumnDefinitions = new ColumnDefinitions("*,*,*"),
        RowDefinitions = new RowDefinitions("*,*,*"),
        ColumnSpacing = 8,
        RowSpacing = 8
    };
    private readonly TextBlock comparisonCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button findComparisonPair = new() { Content = "Найти похожую пару" };
    private readonly TextBlock comparisonPairStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private (Guid First, Guid Second, bool UsesFallback)? comparisonPair;
    private CancellationTokenSource? comparisonSearch;
    private string? comparisonSearchError;
    private readonly TextBlock comparisonNotice = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool comparisonSelected;
    private bool comparisonPicking;
    private readonly Button openComparisonButton = new() {
        Content = "Сопоставление",
        VerticalAlignment = VerticalAlignment.Center
    };
    private Window? comparisonViewer;

    private bool IsComparisonPageVisible => comparisonSelected && ReferenceEquals(pageHost.Content, analysisPage);
    private bool IsAnalysisMethodsVisible => !comparisonSelected && ReferenceEquals(pageHost.Content, analysisPage);

    // сохраняем анализ и сетку при переключении кнопками внутри страницы
    private Grid CreateAnalysisSections(Grid methods) {
        Button back = new() { Content = "К анализу", VerticalAlignment = VerticalAlignment.Center };
        Control comparison = CreateComparisonView(back);
        comparison.IsVisible = false;
        openComparisonButton.Click += (_, _) => ShowComparison(true, back);
        back.Click += (_, _) => ShowComparison(false, openComparisonButton);
        return new Grid { Children = { methods, comparison } };

        void ShowComparison(bool show, Button focus) {
            if (!ReferenceEquals(pageHost.Content, analysisPage) || comparisonSelected == show) {
                return;
            }
            comparisonSelected = show;
            methods.IsVisible = !show;
            comparison.IsVisible = show;
            UpdateAnalysisSection();
            if (show) {
                videoSpaceHeld = videoPreviewUpHeld = false;
                (DataContext as MainViewModel)?.PauseVideoPlayback();
            }
            focus.Focus();
        }
    }

    // пробел на кнопке открывает набор вместо команды воспроизведения
    private bool IsComparisonButtonInput(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().Contains(openComparisonButton);

    // видеоанализ и полоса воспроизведения следуют за фактически открытым разделом
    private void UpdateAnalysisSection() {
        (DataContext as MainViewModel)?.SetVideoAnalysisVisible(IsAnalysisMethodsVisible);
        if (videoPlaybackHost is not null) {
            videoPlaybackHost.IsVisible = !IsComparisonPageVisible;
        }
    }

    // держим сетку отдельной от документа и оставляем место статусу выбора файлов
    private Control CreateComparisonView(Button back) {
        Button add = new() { Content = "Добавить изображение" };
        AutomationProperties.SetName(add, "Добавить изображение");
        add.Click += async (_, _) => await PickComparisonImagesAsync();
        AutomationProperties.SetName(findComparisonPair, "Найти похожую пару");
        findComparisonPair.Click += async (_, _) => await FindComparisonPairAsync();
        StackPanel toolbar = new() {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { add, findComparisonPair, comparisonCount }
        };
        Grid actions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { toolbar, back } };
        Grid.SetColumn(back, 1);
        StackPanel header = new() { Spacing = 4, Children = { actions, comparisonPairStatus, comparisonNotice } };
        ScrollViewer scroll = new() {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = comparisonGrid
        };
        // отдаем свободную высоту миниатюрам и сохраняем прокрутку в низком окне
        int rows = comparisonGrid.RowDefinitions.Count;
        double gaps = (rows - 1) * comparisonGrid.RowSpacing;
        double minHeight = rows * MinimumComparisonRowHeight + gaps;
        double maxHeight = rows * MaximumComparisonRowHeight + gaps;
        comparisonGrid.Height = minHeight;
        scroll.SizeChanged += (_, e) =>
            comparisonGrid.Height = Math.Clamp(Math.Floor(e.NewSize.Height), minHeight, maxHeight);
        Grid.SetRow(scroll, 1);
        UpdateComparisonGrid();
        return CreateAnalysisCard(new Grid {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Children = { header, scroll }
        });
    }

    // сначала снимаем ссылки контролов, чтобы старый bitmap можно было освободить
    private void ClearComparisonGrid() {
        foreach (Image image in comparisonImages) {
            image.Source = null;
        }
        comparisonImages.Clear();
        comparisonGrid.Children.Clear();
    }

    // обновляем максимум девять карточек и оставляем свободные места на своих позициях
    private void UpdateComparisonGrid() {
        ClearComparisonGrid();
        for (int index = 0; index < ImageComparisonSet.Capacity; index++) {
            Border card = new() {
                Padding = new Thickness(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(5)
            };
            if (index < comparisonSet.Items.Count) {
                card.Child = CreateComparisonCell(comparisonSet.Items[index]);
            }
            Grid.SetRow(card, index / 3);
            Grid.SetColumn(card, index % 3);
            comparisonGrid.Children.Add(card);
        }
        comparisonCount.Text = $"{comparisonSet.Items.Count} из {ImageComparisonSet.Capacity}";
        RefreshComparisonPairView();
    }

    // отмечаем обе записи рамкой и доступным именем, сохраняя размер содержимого карточек
    private void RefreshComparisonPairView() {
        bool enoughImages = comparisonSet.Items.Count >= ImageComparisonService.MinimumImages;
        findComparisonPair.IsEnabled = enoughImages && comparisonSearch is null;
        string? status = !enoughImages ? $"Для поиска добавьте минимум {ImageComparisonService.MinimumImages} изображения."
            : comparisonSearch is not null ? "Ищем похожую пару…" : comparisonSearchError;
        if (status is null && comparisonPair is { UsesFallback: true }) {
            status = ImageComparisonService.CanUseGeometry
                ? "Мало общих деталей. Пара выбрана по цветам и контурам."
                : "Поиск по деталям недоступен. Пара выбрана по цветам и контурам.";
        }
        comparisonPairStatus.Text = status;
        comparisonPairStatus.IsVisible = status is not null;
        for (int i = 0; i < comparisonSet.Items.Count; i++) {
            ImageComparisonItem item = comparisonSet.Items[i];
            bool selected = comparisonPair is { } pair && (item.Id == pair.First || item.Id == pair.Second);
            Border card = (Border)comparisonGrid.Children[i];
            card.BorderBrush = selected ? Brushes.DodgerBlue : Brushes.Gray;
            card.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Padding = new Thickness(selected ? 5 : 6);
            string label = selected ? $"{item.FileName} — найденная пара" : item.FileName;
            AutomationProperties.SetName(card, label);
            AutomationProperties.SetName(comparisonImages[i], label);
        }
    }

    // передаем фоновому расчету только неизменяемые признаки и проверяем ревизию перед показом
    private async Task FindComparisonPairAsync() {
        if (mediaWindowClosed || comparisonSearch is not null ||
            comparisonSet.Items.Count < ImageComparisonService.MinimumImages) {
            return;
        }

        long revision = comparisonSet.Revision;
        var images = comparisonSet.Items.Select(item => (item.Id, item.Features)).ToArray();
        using CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(comparisonLifetime.Token);
        comparisonSearch = search;
        comparisonSearchError = null;
        RefreshComparisonPairView();
        try {
            var pair = await Task.Run(() => ImageComparisonService.FindMostSimilarPair(images, search.Token), search.Token);
            if (IsCurrentSearch()) {
                comparisonPair = pair;
            }
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested) {
            // новое состояние набора уже получило собственный запрос
        }
        catch (Exception error) {
            if (IsCurrentSearch()) {
                comparisonSearchError = $"Не удалось найти похожую пару. {error.Message}";
            }
        }
        finally {
            if (ReferenceEquals(comparisonSearch, search)) {
                comparisonSearch = null;
                if (!mediaWindowClosed) {
                    RefreshComparisonPairView();
                }
            }
        }

        // поздняя пара не применяется ни к другому составу, ни к закрытому окну
        bool IsCurrentSearch() => !mediaWindowClosed && !search.IsCancellationRequested &&
            ReferenceEquals(comparisonSearch, search) && revision == comparisonSet.Revision;
    }

    // действия привязаны к записи, поэтому сдвиг соседней карточки не меняет их цель
    private Grid CreateComparisonCell(ImageComparisonItem item) {
        Image image = new() { Source = item.Bitmap, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(image, item.FileName);
        comparisonImages.Add(image);
        TextBlock name = new() { Text = item.FileName, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, item.FileName);
        Grid.SetRow(name, 1);

        Button enlarge = CreateComparisonAction("Увеличить", item);
        enlarge.Click += async (_, _) => await ShowComparisonImageAsync(item);
        Button replace = CreateComparisonAction("Заменить", item);
        replace.Click += async (_, _) => await PickComparisonImagesAsync(item);
        Button remove = CreateComparisonAction("Удалить", item);
        remove.Click += (_, _) => RemoveComparisonImage(item);

        Grid actions = new() {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 4,
            Children = { enlarge, replace, remove }
        };
        Grid.SetColumn(replace, 1);
        Grid.SetColumn(remove, 2);
        Grid.SetRow(actions, 2);

        return new Grid {
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            RowSpacing = 4,
            Children = { image, name, actions }
        };
    }

    // называем действие и его изображение для навигации по повторяющимся кнопкам
    private static Button CreateComparisonAction(string text, ImageComparisonItem item) {
        Button button = new() {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(button, $"{text}: {item.FileName}");
        return button;
    }

    // открываем один диалог набора и применяем только подготовленные изображения
    private async Task PickComparisonImagesAsync(ImageComparisonItem? target = null) {
        if (mediaWindowClosed || comparisonPicking || comparisonViewer is not null ||
            target is not null && !comparisonSet.Contains(target)) {
            return;
        }
        if (target is null && comparisonSet.Items.Count == ImageComparisonSet.Capacity) {
            ShowComparisonNotice("В наборе уже 9 изображений. Замените или удалите одно из них.");
            return;
        }

        comparisonPicking = true;
        CancellationToken token = comparisonLifetime.Token;
        IReadOnlyList<IStorageFile>? files = null;
        try {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = target is null ? "Добавить изображения" : $"Заменить «{target.FileName}»",
                AllowMultiple = target is null,
                FileTypeFilter = [new FilePickerFileType("Изображения") {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"]
                }]
            });
            if (mediaWindowClosed || files.Count == 0) {
                return;
            }

            List<string> issues = [];
            int count = target is null ? files.Count : 1;
            for (int index = 0; index < count && !mediaWindowClosed; index++) {
                if (target is null && comparisonSet.Items.Count == ImageComparisonSet.Capacity) {
                    issues.Add($"Достигнут предел 9. Не добавлено файлов: {count - index}.");
                    break;
                }

                IStorageFile file = files[index];
                string fileName = file.Name;
                Bitmap? bitmap = null;
                try {
                    (bitmap, ImageComparisonFeatures features) = await LoadComparisonImageAsync(file, token);
                    if (mediaWindowClosed) {
                        return;
                    }
                    ImageComparisonItem? retired = null;
                    bool accepted = target is null
                        ? comparisonSet.TryAdd(fileName, bitmap, features)
                        : comparisonSet.TryReplace(target, fileName, bitmap, features, out retired);
                    if (accepted) {
                        bitmap = null;
                        RefreshComparisonAfterChange(retired);
                    }
                    else {
                        issues.Add(target is null ? $"Не удалось добавить «{fileName}»."
                            : "Изображение для замены уже изменено или удалено.");
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested) {
                    issues.Add($"Не удалось открыть «{fileName}». {error.Message}");
                }
                finally {
                    bitmap?.Dispose();
                }
            }
            if (!mediaWindowClosed) {
                string message = issues.Count switch {
                    0 => string.Empty,
                    1 => issues[0],
                    _ => $"{issues[0]} Других сообщений: {issues.Count - 1}. {issues[^1]}"
                };
                ShowComparisonNotice(message);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            // закрытое окно не принимает отложенную загрузку
        }
        catch (Exception error) {
            if (!mediaWindowClosed) {
                ShowComparisonNotice($"Не удалось выбрать изображение. {error.Message}");
            }
        }
        finally {
            comparisonPicking = false;
            if (files is not null) {
                foreach (IStorageFile file in files) {
                    file.Dispose();
                }
            }
        }
    }

    // готовим показ и признаки одним декодированием до передачи bitmap набору
    private static async Task<(Bitmap Bitmap, ImageComparisonFeatures Features)> LoadComparisonImageAsync(
        IStorageFile file, CancellationToken token) {
        using MemoryStream bytes = new();
        await using (Stream input = await file.OpenReadAsync()) {
            await input.CopyToAsync(bytes, token);
        }
        bytes.Position = 0;
        return await Task.Run(() => {
            Bitmap bitmap = new(bytes);
            try {
                return (bitmap, ImageComparisonService.CreateFeatures(bitmap, token));
            }
            catch {
                bitmap.Dispose();
                throw;
            }
        }, token);
    }

    // сохраняем предыдущий набор, если кнопка относится к уже снятой записи
    private void RemoveComparisonImage(ImageComparisonItem item) {
        if (!mediaWindowClosed && comparisonViewer is null && comparisonSet.TryRemove(item, out ImageComparisonItem? removed)) {
            RefreshComparisonAfterChange(removed);
            ShowComparisonNotice(string.Empty);
        }
    }

    // освобождаем снятый bitmap после отвязки прежних карточек
    private void RefreshComparisonAfterChange(ImageComparisonItem? retired) {
        comparisonSearch?.Cancel();
        comparisonSearch = null;
        comparisonPair = null;
        comparisonSearchError = null;
        try {
            UpdateComparisonGrid();
        }
        finally {
            retired?.Bitmap.Dispose();
        }
    }

    // выводим ошибку операции только на независимом экране набора
    private void ShowComparisonNotice(string message) {
        comparisonNotice.Text = message;
        comparisonNotice.IsVisible = message.Length > 0;
    }

    // показываем сохраненное изображение крупно, без повторного чтения файла
    private async Task ShowComparisonImageAsync(ImageComparisonItem item) {
        if (mediaWindowClosed || comparisonPicking || comparisonViewer is not null || !comparisonSet.Contains(item)) {
            return;
        }
        Image image = new() { Source = item.Bitmap, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(image, item.FileName);
        Window dialog = new() {
            Title = item.FileName,
            Width = Math.Clamp(ClientSize.Width - 100, 440, 1120),
            Height = Math.Clamp(ClientSize.Height - 70, 340, 760),
            MinWidth = 340,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Button close = new() { Content = "Закрыть", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "Закрыть");
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 1);
        dialog.Content = new Grid {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 8,
            Children = { image, close }
        };
        comparisonViewer = dialog;
        dialog.Closed += (_, _) => ReleasePreview();
        try {
            await dialog.ShowDialog(this);
        }
        catch (Exception error) {
            if (!mediaWindowClosed) {
                ShowComparisonNotice($"Не удалось увеличить изображение. {error.Message}");
            }
        }
        finally {
            ReleasePreview();
        }

        // отвязываем картинку до освобождения набора и не затрагиваем уже новое окно
        void ReleasePreview() {
            image.Source = null;
            if (ReferenceEquals(comparisonViewer, dialog)) {
                comparisonViewer = null;
            }
        }
    }

    // закрываем окно просмотра и возвращаем все собственные картинки набора
    private void DisposeComparisonView() {
        comparisonLifetime.Cancel();
        comparisonLifetime.Dispose();
        comparisonViewer?.Close();
        ClearComparisonGrid();
        comparisonSet.Dispose();
    }
}
