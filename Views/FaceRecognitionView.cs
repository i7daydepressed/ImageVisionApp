using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

internal sealed class FaceRecognitionView : UserControl {
    private readonly TabStrip tabs = new() { ItemsSource = new[] { "Вход", "Результат" }, SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Left };
    private readonly VideoPreviewImage input = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock inputEmpty = new() {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap
    };
    private readonly FaceRecognitionPreview preview = new() { ClipToBounds = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border inputPane;
    private readonly Border resultPane;

    private MainViewModel? model;
    private MediaDocument? requestedDocument;
    private FaceRecognitionService? recognizer;
    private string? initializationFailure;
    private FaceVideoPreview? submitted;
    private bool active;
    private bool closing;
    private bool refreshQueued;
    private bool previewHasNoResult = true;
    private Task work = Task.CompletedTask;
    private Task cleanup = Task.CompletedTask;
    private CancellationTokenSource? workStop;

    private bool ShowsResult => tabs.SelectedIndex == 1;
    public bool HasWork => !work.IsCompleted || !cleanup.IsCompleted || recognizer is not null;

    // располагаем исходный кадр, распознавание и вход в базу как соседние части одной вкладки
    public FaceRecognitionView(Button openDatabase) {
        AutomationProperties.SetName(input, "Исходный кадр «Лиц»");
        AutomationProperties.SetName(preview, "Кадр распознавания");
        inputPane = CreatePreviewPane(new Grid { Children = { input, inputEmpty } });
        resultPane = CreatePreviewPane(preview);
        inputPane.PointerPressed += (_, _) => inputPane.Focus();
        resultPane.PointerPressed += (_, _) => resultPane.Focus();
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { tabs } };
        Grid.SetColumn(openDatabase, 1);
        header.Children.Add(openDatabase);
        Grid previews = new() { Children = { inputPane, resultPane } };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
            Margin = new Thickness(12), Children = { header } };
        Grid.SetRow(previews, 1);
        layout.Children.Add(previews);
        Grid.SetRow(status, 2);
        layout.Children.Add(status);
        Content = layout;
        tabs.SelectionChanged += (_, args) => {
            if (ReferenceEquals(args.Source, tabs)) {
                UpdateVisiblePane();
                if (!ShowsResult) {
                    ClearResult();
                }
                QueueRefresh();
            }
        };
        UpdateVisiblePane();
    }

    // фон и поля одинаковы для двух вариантов общего предпросмотра
    private static Border CreatePreviewPane(Control content) => new() {
        Background = new SolidColorBrush(Color.Parse("#181818")), Padding = new Thickness(10), Child = content,
        Focusable = true
    };

    // отписываемся от прежнего окна и аннулируем его результаты
    public void UseModel(MainViewModel? next) {
        if (ReferenceEquals(model, next)) {
            return;
        }
        bool wasActive = active;
        Deactivate();
        model = next;
        if (wasActive) {
            Activate();
        }
    }

    // слушаем только общий документ и общий предпросмотр текущего окна
    public void Activate() {
        if (active || closing) {
            return;
        }
        active = true;
        if (model is not null) {
            model.PropertyChanged += OnMediaChanged;
        }
        UpdateInput();
        QueueRefresh();
    }

    // забираем старые пиксели и модели при уходе, оставляя позицию общему плееру
    public void Deactivate() {
        if (!active) {
            return;
        }
        active = false;
        if (model is not null) {
            model.PropertyChanged -= OnMediaChanged;
        }
        input.Source = null;
        ClearResult();
        requestedDocument = null;
        initializationFailure = null;
        if (recognizer is { } old) {
            recognizer = null;
            cleanup = ReleaseAfterAsync(Task.WhenAll(work, cleanup), old);
        }
    }

    // запрещаем повторный вход и дожидаемся единственного фонового вызова модели
    public Task StopAsync() {
        closing = true;
        Deactivate();
        return Task.WhenAll(work, cleanup);
    }

    // обновляем ссылку сразу, а съемку откладываем до завершения общей публикации кадра
    private void OnMediaChanged(object? sender, PropertyChangedEventArgs args) {
        switch (args.PropertyName) {
            case nameof(MainViewModel.CurrentDocument):
            case nameof(MainViewModel.OriginalImage):
            case nameof(MainViewModel.VideoPreviewFrame):
            case nameof(MainViewModel.VideoPreviewRevision):
                UpdateInput();
                break;
            case nameof(MainViewModel.PlaybackStatus):
            case nameof(MainViewModel.IsVideoEditorSourceReady):
            case nameof(MainViewModel.VideoEditorSourceNotice):
                break;
            default:
                return;
        }
        QueueRefresh();
    }

    // общий bitmap остается у медиаконтейнера, его ревизия обновляет отображение
    private void UpdateInput() {
        input.Source = model?.CurrentDocument?.Kind switch {
            MediaKind.Image => model.OriginalImage,
            MediaKind.Video => model.VideoPreviewFrame,
            _ => null
        };
        input.SetValue(VideoPreviewImage.RevisionProperty, model?.VideoPreviewRevision ?? 0);
        inputEmpty.IsVisible = input.Source is null;
        inputEmpty.Text = model?.CurrentDocument?.Kind == MediaKind.Video
            ? model.VideoEditorSourceNotice ?? "Ожидаем видеокадр" : "Откройте фото или видео";
    }

    // переключаем просмотр, скрывая статус распознавания при показе входа
    private void UpdateVisiblePane() {
        inputPane.IsVisible = !ShowsResult;
        resultPane.IsVisible = ShowsResult;
        status.IsVisible = ShowsResult;
    }

    // объединяем события пикселей, штампа и шкалы в один проход после общего тика
    private void QueueRefresh() {
        if (!active || refreshQueued) {
            return;
        }
        refreshQueued = true;
        Dispatcher.UIThread.Post(() => {
            refreshQueued = false;
            RefreshRecognition();
        });
    }

    // захватываем только последний готовый вход, когда закончился предыдущий расчет
    private void RefreshRecognition() {
        if (!active || !ShowsResult) {
            return;
        }

        MediaDocument? document = model?.CurrentDocument;
        if (!ReferenceEquals(requestedDocument, document)) {
            requestedDocument = document;
            ClearResult();
            initializationFailure = null;
        }
        if (document is not { Kind: MediaKind.Video }) {
            SetStatus(document is null ? "Откройте видео" : "Распознавание фото пока недоступно");
            return;
        }
        if (model?.ReadFaceVideoPreview() is not { } current) {
            ClearResult();
            model?.ResolveFacePreviewAfterLivePause();
            SetStatus(model?.VideoEditorSourceNotice ?? "Ожидаем видеокадр…");
            return;
        }
        if (submitted is { } previous && (!previous.SameRun(current) || !current.IsPlaying && !previous.SameFrame(current))) {
            ClearResult();
        }
        if (initializationFailure is { } problem) {
            SetStatus(problem);
            return;
        }
        if (!work.IsCompleted || submitted is { } same && same.SameFrame(current)) {
            return;
        }

        submitted = current;
        if (previewHasNoResult) {
            SetStatus("Распознаем кадр…");
        }
        try {
            VideoEditorFrame pixels = VideoEditorFrame.Capture(current.Bitmap);
            workStop = new CancellationTokenSource();
            work = RecognizeAsync(current, pixels, workStop);
        }
        catch (Exception error) {
            SetStatus($"Не удалось получить кадр. {error.Message}");
        }
    }

    // кадр и рамки публикуются вместе, на паузе требуется точный текущий штамп
    private async Task RecognizeAsync(FaceVideoPreview frame, VideoEditorFrame pixels, CancellationTokenSource stop) {
        using (pixels)
        using (stop) {
            CancellationToken token = stop.Token;
            try {
                await cleanup;
                token.ThrowIfCancellationRequested();
                if (recognizer is null) {
                    try {
                        await LoadRecognizerAsync(token);
                    }
                    catch (Exception error) when (IsCurrent(frame, token)) {
                        initializationFailure = $"Не удалось распознать лица. {error.Message}";
                        SetStatus(initializationFailure);
                        return;
                    }
                }
                FaceRecognitionService engine = recognizer!;
                FaceRecognitionFrame result = await Task.Run(() =>
                    engine.Recognize(FaceRecognitionService.CopyVideoImage(pixels, token), token), token);
                if (IsCurrent(frame, token)) {
                    preview.Show(result.Image, result);
                    previewHasNoResult = false;
                    SetStatus(result.EmptyCatalogue ? "База пуста" : result.Faces.Count == 0 ? "Лицо не найдено" : "");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }
            catch (Exception error) {
                if (IsCurrent(frame, token)) {
                    SetStatus($"Не удалось обработать кадр. {error.Message}");
                }
            }
            finally {
                workStop = null;
                QueueRefresh();
            }
        }
    }

    // живой результат может догонять общий просмотр, но не пересекать команду перехода
    private bool IsCurrent(FaceVideoPreview frame, CancellationToken token) =>
        active && ShowsResult && !token.IsCancellationRequested && model?.ReadFaceVideoPreview() is { } current &&
        frame.SameRun(current) && (current.IsPlaying || frame.SameFrame(current));

    // перечитываем сохраненный каталог после возвращения из базы
    private async Task LoadRecognizerAsync(CancellationToken token) {
        FaceRecognitionService candidate = await Task.Run(() => {
            FaceWorkspacePaths paths = FaceWorkspacePaths.Resolve(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable(FaceWorkspacePaths.RootVariable));
            var profiles = new FaceProfileStore(paths.CheckoutRoot).Load();
            token.ThrowIfCancellationRequested();
            return new FaceRecognitionService(paths.CreatePipeline(), profiles);
        }, token);
        if (token.IsCancellationRequested) {
            candidate.Dispose();
            token.ThrowIfCancellationRequested();
        }
        recognizer = candidate;
    }

    // сбрасываем кадр и доступную подпись при смене контекста
    private void ClearResult() {
        workStop?.Cancel();
        submitted = null;
        if (!previewHasNoResult) {
            preview.Show(null);
            previewHasNoResult = true;
        }
    }

    // текст пустого состояния объявляется только в выбранном представлении
    private void SetStatus(string message) {
        status.Text = message;
        status.IsVisible = ShowsResult && message.Length > 0;
    }

    // освобождаем нативные модели только после возврата их последнего вызова
    private static async Task ReleaseAfterAsync(Task previous, FaceRecognitionService engine) {
        await previous.ConfigureAwait(false);
        await Task.Run(() => {
            try {
                engine.Dispose();
            }
            catch (Exception error) {
                Trace.TraceWarning($"Ошибка освобождения распознавания: {error.Message}");
            }
        }).ConfigureAwait(false);
    }
}
