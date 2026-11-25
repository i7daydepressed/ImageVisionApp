using System;
using System.ComponentModel;
using System.Collections.Generic;
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
    private FaceVideoWork? observedWork;
    private FaceVideoPreview? submitted;
    private readonly List<Task> liveJobs = [];
    private CancellationTokenSource? liveStop;
    private Task? capacityWait;
    private bool active;
    private bool closing;
    private bool refreshQueued;
    private bool previewHasNoResult = true;
    private bool presentingPrepared;
    private long issued;
    private long shown;

    private bool ShowsResult => tabs.SelectedIndex == 1;
    public bool HasWork => liveJobs.Exists(task => !task.IsCompleted) || model?.HasFaceVideoWork == true;

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
                    model?.SetFaceVideoVisible(false);
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
        observedWork = null;
        model?.SetFaceVideoVisible(false);
    }

    // запрещаем новый вход и дожидаемся захваченных кадров и освобождения моделей
    public async Task StopAsync() {
        closing = true;
        Deactivate();
        await Task.WhenAll(liveJobs);
        if (model is not null) {
            await model.DrainFaceVideoWorkAsync();
        }
    }

    // подготовленную пару передаем синхронно, живой кадр забираем после общей публикации
    private void OnMediaChanged(object? sender, PropertyChangedEventArgs args) {
        switch (args.PropertyName) {
            case nameof(MainViewModel.PreparedFaceVideo):
                if (!PresentPrepared()) {
                    ClearPreparedPreview();
                }
                break;
            case nameof(MainViewModel.CurrentDocument):
            case nameof(MainViewModel.OriginalImage):
            case nameof(MainViewModel.VideoPreviewFrame):
            case nameof(MainViewModel.VideoPreviewRevision):
                UpdateInput();
                if (model?.PreparedFaceVideo is null) {
                    ClearPreparedPreview();
                }
                break;
            case nameof(MainViewModel.PlaybackStatus):
            case nameof(MainViewModel.IsVideoEditorSourceReady):
            case nameof(MainViewModel.VideoEditorSourceNotice):
            case nameof(MainViewModel.CurrentFaceVideoWork):
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

    // при нехватке рабочего места живой режим оставляет следующий шанс последнему кадру
    private void RefreshRecognition() {
        liveJobs.RemoveAll(task => task.IsCompleted);
        if (!active || !ShowsResult || model is not { } media) {
            return;
        }
        media.SetFaceVideoVisible(true);
        if (!ReferenceEquals(observedWork, media.CurrentFaceVideoWork)) {
            ClearResult();
            observedWork = media.CurrentFaceVideoWork;
        }
        if (PresentPrepared()) {
            return;
        }
        if (media.CurrentDocument is not { Kind: MediaKind.Video }) {
            SetStatus(media.CurrentDocument is null ? "Откройте видео" : "Распознавание фото пока недоступно");
            return;
        }
        if (media.IsFaceVideoPrepared || media.ReadFaceVideoPreview() is not { } current) {
            ClearResult();
            media.ResolveFacePreviewAfterLivePause();
            SetStatus(media.VideoEditorSourceNotice ?? "Ожидаем видеокадр…");
            return;
        }
        if (submitted is { } previous && (!previous.SameRun(current) ||
            !current.IsPlaying && !previous.SameFrame(current))) {
            ClearResult();
        }
        if (observedWork is not { } worker || submitted is { } same && same.SameFrame(current)) {
            return;
        }
        if (worker.WorkerCapacity(current.Bitmap.PixelSize, live: true) == 0) {
            ClearResult();
            SetStatus(FaceVideoWork.BudgetError);
            return;
        }
        FaceVideoLease? lease = worker.TryReserve(current.Bitmap.PixelSize, live: true, out Task available);
        if (lease is null) {
            if (capacityWait is null || capacityWait.IsCompleted) {
                liveStop ??= new CancellationTokenSource();
                capacityWait = NotifyCapacityAsync(available, liveStop.Token);
            }
            return;
        }
        submitted = current;
        if (previewHasNoResult) {
            SetStatus("Распознаем кадр…");
        }
        try {
            long acceptedAt = Environment.TickCount64;
            VideoEditorFrame pixels = VideoEditorFrame.Capture(current.Bitmap);
            liveStop ??= new CancellationTokenSource();
            liveJobs.Add(RecognizeAsync(current, pixels, worker, lease, ++issued, acceptedAt, liveStop.Token));
        }
        catch (Exception error) {
            lease.Dispose();
            SetStatus($"Не удалось получить кадр. {error.Message}");
        }
    }

    // общий подготовитель выдает immutable bitmap и метаданные одного физического кадра
    private bool PresentPrepared() {
        if (!active || !ShowsResult) {
            return false;
        }
        if (model?.PreparedFaceVideo is not { } prepared) {
            return false;
        }
        if (submitted is not null) {
            ClearResult();
        }
        preview.ShowPrepared(prepared.Bitmap, prepared.Result);
        presentingPrepared = true;
        previewHasNoResult = false;
        ShowResultStatus(prepared.Result);
        return true;
    }

    // выдаем только более новый завершенный кадр в том же запуске
    private async Task RecognizeAsync(FaceVideoPreview frame, VideoEditorFrame pixels, FaceVideoWork worker,
        FaceVideoLease lease, long sequence, long acceptedAt, CancellationToken token) {
        using (lease)
        using (pixels) {
            try {
                var recognized = await Task.Run(() => {
                    FaceVideoResult result = worker.Process(pixels, token, out FaceImage? image);
                    return (Result: result, Image: image);
                }, token);
                if (sequence <= shown || !IsCurrent(frame, worker, token)) {
                    return;
                }
                shown = sequence;
                if (recognized.Image is { } image) {
                    preview.ShowLive(image, recognized.Result);
                    previewHasNoResult = false;
                    worker.Budget.Presented(acceptedAt);
                }
                ShowResultStatus(recognized.Result);
                lease.Finish(recognized.Result.Delay);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }
            catch (Exception error) {
                if (IsCurrent(frame, worker, token)) {
                    SetStatus($"Не удалось обработать кадр. {error.Message}");
                }
            }
            finally {
                QueueRefresh();
            }
        }
    }

    // живой результат отстает только в пределах текущей команды и текущего каталога
    private bool IsCurrent(FaceVideoPreview frame, FaceVideoWork worker, CancellationToken token) =>
        active && ShowsResult && !token.IsCancellationRequested &&
        ReferenceEquals(worker, model?.CurrentFaceVideoWork) && model?.PreparedFaceVideo is null &&
        model?.ReadFaceVideoPreview() is { } current && frame.SameRun(current) &&
        (current.IsPlaying || frame.SameFrame(current));

    // снимаем картинку и отменяем уже взятые работы при смене контекста
    private void ClearResult() {
        Task wake = capacityWait ?? Task.CompletedTask;
        capacityWait = null;
        if (liveStop is { } ending) {
            liveStop = null;
            ending.Cancel();
            _ = DisposeStopAsync(Task.WhenAll(Task.WhenAll(liveJobs), wake), ending);
        }
        submitted = null;
        preview.ShowPrepared(null);
        previewHasNoResult = true;
        presentingPrepared = false;
    }

    // отвязываем общий bitmap до освобождения или замены его владельцем
    private void ClearPreparedPreview() {
        if (presentingPrepared) {
            preview.ShowPrepared(null);
            previewHasNoResult = true;
            presentingPrepared = false;
        }
    }

    // просыпаемся по освобождению буфера, без очереди кадров и ожидания таймера
    private async Task NotifyCapacityAsync(Task available, CancellationToken token) {
        try {
            await available.WaitAsync(token);
            QueueRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
        }
    }

    // регистрация рабочего токена еще возможна до фактического возврата всех задач
    private static async Task DisposeStopAsync(Task workers, CancellationTokenSource stop) {
        try {
            await workers.ConfigureAwait(false);
        }
        finally {
            stop.Dispose();
        }
    }

    // оставляем явными ошибки, отсутствие лица и пустую базу
    private void ShowResultStatus(FaceVideoResult result) =>
        SetStatus(result.Error ?? (result.EmptyCatalogue ? "База пуста" : result.Faces.Count == 0 ? "Лицо не найдено" : ""));

    // текст пустого состояния объявляется только в выбранном представлении
    private void SetStatus(string message) {
        status.Text = message;
        status.IsVisible = ShowsResult && message.Length > 0;
    }
}
