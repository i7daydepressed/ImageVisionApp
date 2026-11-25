using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.Views;

internal sealed class FaceRecognitionView : UserControl {
    private readonly Window owner;
    private readonly Button open = new() { Content = "Открыть видео…" };
    private readonly Button play = new() { Content = "Воспроизвести" };
    private readonly Slider timeline = new() { Minimum = 0, Maximum = 1, SmallChange = 1000, LargeChange = 5000 };
    private readonly TextBlock source = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly FaceRecognitionPreview preview = new() { Focusable = true, ClipToBounds = true };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private VideoClip? clip;
    private VideoPlaybackSession? playback;
    private FaceRecognitionService? recognizer;
    private string? recognitionProblem;
    private long position;
    private long videoRevision;
    private long startingPosition;
    private int visit;
    private bool active;
    private bool closing;
    private bool atEnd;
    private bool picking;
    private bool changingTimeline;
    private bool draggingTimeline;
    private bool transitioning;
    private bool spaceHeld;
    private Task work = Task.CompletedTask;
    private Task cleanup = Task.CompletedTask;
    private CancellationTokenSource? workStop;

    public bool HasWork => active || !work.IsCompleted || !cleanup.IsCompleted;

    // связываем собственную шкалу и команды, доступные только в распознавании
    public FaceRecognitionView(Window owner) {
        this.owner = owner;
        Content = CreateLayout();
        AutomationProperties.SetName(preview, "Кадр распознавания");
        AutomationProperties.SetName(timeline, "Позиция видео распознавания");
        open.Click += async (_, _) => await PickVideoAsync();
        play.Click += (_, _) => TogglePlay();
        preview.PointerPressed += (_, _) => preview.Focus(NavigationMethod.Pointer);
        KeyDown += OnRecognitionKeyDown;
        KeyUp += (_, args) => {
            if (args.Key == Key.Space) {
                spaceHeld = false;
            }
        };
        timeline.AddHandler(PointerPressedEvent, (_, args) => {
            draggingTimeline = args.GetCurrentPoint(timeline).Properties.IsLeftButtonPressed;
        }, RoutingStrategies.Tunnel);
        timeline.AddHandler(PointerReleasedEvent, (_, _) => EndTimelineDrag(), handledEventsToo: true);
        timeline.AddHandler(Thumb.DragCompletedEvent, (_, _) => EndTimelineDrag(), handledEventsToo: true);
        timeline.PointerCaptureLost += (_, _) => EndTimelineDrag();
        timeline.ValueChanged += (_, _) => {
            if (!changingTimeline && !draggingTimeline) {
                Seek((long)timeline.Value);
            }
            else if (draggingTimeline && clip is { } selected) {
                time.Text = FormatTime((long)timeline.Value, selected.Duration);
            }
        };
        timer.Tick += (_, _) => OnPlaybackTick();
        UpdateControls();
    }

    // сохраняем место для кадра независимо от длины имени файла
    private Grid CreateLayout() {
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10,
            Children = { open } };
        Grid.SetColumn(source, 1);
        heading.Children.Add(source);
        Grid transport = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10,
            Children = { play } };
        Grid.SetColumn(timeline, 1);
        transport.Children.Add(timeline);
        Grid.SetColumn(time, 2);
        transport.Children.Add(time);
        Grid content = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8,
            Margin = new Thickness(12), Children = { heading } };
        Grid.SetRow(preview, 1);
        content.Children.Add(preview);
        Grid.SetRow(transport, 2);
        content.Children.Add(transport);
        Grid.SetRow(status, 3);
        content.Children.Add(status);
        return content;
    }

    // перечитываем профили и возвращаемся к сохраненной позиции своего файла в паузе
    public void Activate() {
        if (active || closing) {
            return;
        }
        active = true;
        visit++;
        timer.Start();
        recognitionProblem = null;
        if (clip is { } selected) {
            long restore = position;
            StartWork(token => ShowPausedAsync(selected, restore, token), "Не удалось восстановить кадр видео.");
        }
        else {
            status.Text = "Откройте видео MP4 или MKV.";
            UpdateControls();
        }
    }

    // отменяем запоздалый результат и оставляем только путь и позицию для возврата
    public void Deactivate() {
        if (!active) {
            return;
        }
        active = false;
        visit++;
        timer.Stop();
        spaceHeld = false;
        draggingTimeline = false;
        SavePlaybackPosition();
        RetirePlayer();
        workStop?.Cancel();
        FaceRecognitionService? previous = recognizer;
        recognizer = null;
        if (previous is not null) {
            Retire(previous, work);
        }
        preview.Show(null);
        UpdateControls();
    }

    // при закрытии окна дожидаемся последнего нативного кадра и завершения освобождения
    public async Task StopAsync() {
        closing = true;
        Deactivate();
        Task pendingCleanup;
        do {
            pendingCleanup = cleanup;
            await Task.WhenAll(work, pendingCleanup);
        } while (!ReferenceEquals(pendingCleanup, cleanup));
    }

    // системный диалог не меняет источник, пока не выбран поддерживаемый локальный файл
    private async Task PickVideoAsync() {
        if (!active || picking) {
            return;
        }
        picking = true;
        int openedIn = visit;
        UpdateControls();
        try {
            IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Выбрать видео для распознавания",
                FileTypeFilter = [new FilePickerFileType("Видео MP4 и MKV") { Patterns = ["*.mp4", "*.mkv"] }]
            });
            if (!active || openedIn != visit || files.Count == 0) {
                return;
            }
            string? path = files[0].TryGetLocalPath();
            if (path is null || Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".mkv")) {
                status.Text = "Выберите локальный видеофайл MP4 или MKV.";
                return;
            }
            OpenVideo(path);
        }
        catch (Exception error) {
            if (active && openedIn == visit) {
                status.Text = $"Не удалось выбрать видео. {error.Message}";
            }
        }
        finally {
            picking = false;
            UpdateControls();
        }
    }

    // удерживаем предыдущий снимок до проверки нового файла, а новый публикуем в паузе
    private void OpenVideo(string path) {
        SavePlaybackPosition();
        RetirePlayer();
        status.Text = "Открываем видео…";
        StartWork(async token => {
            (VideoClip opened, FaceImage image) = await Task.Run(async () => {
                VideoFileIdentity? identity = VideoFileIdentity.TryCapture(path);
                (FaceImage first, _) = ReadPaused(path, identity, 0, 0, token);
                PixelSize size = new(first.Width, first.Height);
                using VideoPlaybackSession candidate = await CreatePlayerAsync(path, size, 0, token).ConfigureAwait(false);
                identity?.EnsureCurrent(path);
                return (new VideoClip(path, identity, size, candidate.Duration), first);
            }, token);
            token.ThrowIfCancellationRequested();
            clip = opened;
            position = 0;
            atEnd = false;
            recognitionProblem = null;
            preview.Show(null);
            source.Text = Path.GetFileName(opened.Path);
            status.Text = "Проверяем кадр…";
            UpdateControls();
            await PublishAsync(image, token);
            if (active && !token.IsCancellationRequested) {
                preview.Focus();
            }
        }, "Не удалось открыть видео.");
    }

    // поднимаем новый плеер сразу с нужной стартовой позицией без буфера от прошлого seek
    private static async Task<VideoPlaybackSession> CreatePlayerAsync(string path, PixelSize size, long start,
        CancellationToken token) {
        VideoPlaybackSession candidate = new(path, size, start);
        try {
            if (!candidate.Prepare()) {
                throw new InvalidDataException("Плеер не открыл видео.");
            }
            long deadline = Environment.TickCount64 + 5000;
            while (candidate.State != VLCState.Paused || candidate.Duration <= 0) {
                token.ThrowIfCancellationRequested();
                if (candidate.State is VLCState.Error or VLCState.Ended || Environment.TickCount64 >= deadline) {
                    throw new InvalidDataException("Плеер не подготовил видеодорожку.");
                }
                await Task.Delay(25, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return candidate;
        }
        catch {
            candidate.Dispose();
            throw;
        }
    }

    // в паузе берем точный снимок существующего декодера и сохраняем его фактическую позицию
    private static (FaceImage Image, long Position) ReadPaused(string path, VideoFileIdentity? identity,
        long point, long duration, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        identity?.EnsureCurrent(path);
        (byte[] png, long actual) = VideoFirstFrameReader.ReadPreview(path, point, duration);
        identity?.EnsureCurrent(path);
        using MemoryStream stream = new(png);
        return (FacePhotoDecoder.Decode(stream, token), actual);
    }

    // обновляем неподвижный кадр после паузы, перемотки или возвращения на вкладку
    private async Task ShowPausedAsync(VideoClip selected, long point, CancellationToken token) {
        (FaceImage image, long actual) = await Task.Run(() =>
            ReadPaused(selected.Path, selected.Identity, point, selected.Duration, token), token);
        token.ThrowIfCancellationRequested();
        position = actual;
        await PublishAsync(image, token);
    }

    // прекращаем живой вывод сразу или начинаем новое воспроизведение с сохраненной точки
    private void TogglePlay() {
        if (!active || transitioning || clip is not { } selected) {
            return;
        }
        if (playback is not null) {
            SavePlaybackPosition();
            long pausedAt = position;
            RetirePlayer();
            StartWork(token => ShowPausedAsync(selected, pausedAt, token), "Не удалось обновить кадр в паузе.");
            return;
        }

        long start = atEnd ? 0 : position;
        status.Text = "Подготавливаем воспроизведение…";
        StartWork(async token => {
            VideoPlaybackSession? candidate = await Task.Run(async () => {
                selected.Identity?.EnsureCurrent(selected.Path);
                return await CreatePlayerAsync(selected.Path, selected.Size, start, token).ConfigureAwait(false);
            }, token);
            try {
                token.ThrowIfCancellationRequested();
                selected.Identity?.EnsureCurrent(selected.Path);
                videoRevision = 0;
                startingPosition = start;
                position = start;
                atEnd = false;
                candidate.SetPaused(false);
                playback = candidate;
                candidate = null;
                status.Text = "";
            }
            finally {
                if (candidate is not null) {
                    Retire(candidate, Task.CompletedTask);
                }
            }
        }, "Не удалось запустить видео.");
    }

    // при перемотке заменяем текущий ход работ одним запросом кадра и оставляем видео на паузе
    private void Seek(long point) {
        if (!active || clip is not { } selected || transitioning) {
            return;
        }
        RetirePlayer();
        long target = Math.Clamp(point, 0, selected.Duration);
        atEnd = target == selected.Duration;
        status.Text = "Ищем кадр…";
        StartWork(token => ShowPausedAsync(selected, target, token), "Не удалось перейти к кадру.");
    }

    // один запрос перемотки уходит при отпускании указателя
    private void EndTimelineDrag() {
        if (draggingTimeline) {
            draggingTimeline = false;
            Seek((long)timeline.Value);
        }
    }

    // окно редактора пропускает эти команды, а кнопки и шкала используют свой обычный ввод
    private void OnRecognitionKeyDown(object? sender, KeyEventArgs args) {
        if (args.KeyModifiers != KeyModifiers.None || clip is null || transitioning ||
            args.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(control => control is Button or Slider)) {
            return;
        }
        switch (args.Key) {
            case Key.Space:
                if (!spaceHeld) {
                    spaceHeld = true;
                    TogglePlay();
                }
                break;
            case Key.Left:
                Seek(position - 1000);
                break;
            case Key.Right:
                Seek(position + 1000);
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    // часы идут отдельно, а в расчет передаем один последний готовый кадр без очереди
    private void OnPlaybackTick() {
        if (playback is not { } player || clip is not { } selected) {
            return;
        }
        if (player.State == VLCState.Error) {
            workStop?.Cancel();
            RetirePlayer();
            status.Text = "Распознавание остановилось: ошибка воспроизведения видео.";
            UpdateControls();
            return;
        }
        if (player.State == VLCState.Ended) {
            RetirePlayer();
            atEnd = true;
            StartWork(token => ShowPausedAsync(selected, selected.Duration, token),
                "Не удалось прочитать последний кадр.");
            return;
        }

        SavePlaybackPosition();
        UpdateControls();
        if (workStop is not null || player.State != VLCState.Playing ||
            player.CaptureFrame(ref videoRevision) is not { } frame) {
            return;
        }

        StartWork(async token => {
            FaceImage image = await Task.Run(() => FaceRecognitionService.CopyVideoImage(frame, token), token);
            await PublishAsync(image, token);
        }, "Не удалось обработать кадр.", transition: false, input: frame);
    }

    // вычисляем все рамки и решения по одному снимку, публикуем его только после проверки отмены
    private async Task PublishAsync(FaceImage image, CancellationToken token) {
        FaceRecognitionFrame? result = null;
        if (recognizer is null && recognitionProblem is null) {
            try {
                await LoadRecognizerAsync(token);
            }
            catch (Exception error) when (!token.IsCancellationRequested) {
                recognitionProblem = $"Не удалось распознать лица. {error.Message}";
            }
        }
        string? failure = recognitionProblem;
        if (recognizer is { } engine) {
            try {
                result = await Task.Run(() => engine.Recognize(image, token), token);
            }
            catch (Exception error) when (!token.IsCancellationRequested) {
                failure = $"Не удалось обработать кадр. {error.Message}";
            }
        }
        token.ThrowIfCancellationRequested();
        preview.Show(image, result);
        status.Text = failure ?? (result!.EmptyCatalogue ? "База пуста" :
            result.Faces.Count == 0 ? "Лицо не найдено" : "");
    }

    // загружаем один проверенный каталог и одну пару моделей на посещение
    private async Task LoadRecognizerAsync(CancellationToken token) {
        FaceRecognitionService candidate = await Task.Run(() => {
            FaceWorkspacePaths paths = FaceWorkspacePaths.Resolve(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable(FaceWorkspacePaths.RootVariable));
            IReadOnlyList<FaceProfile> profiles = new FaceProfileStore(paths.CheckoutRoot).Load();
            token.ThrowIfCancellationRequested();
            return new FaceRecognitionService(paths.CreatePipeline(), profiles);
        }, token);
        if (token.IsCancellationRequested) {
            candidate.Dispose();
            token.ThrowIfCancellationRequested();
        }
        recognizer = candidate;
    }

    // последовательные запросы передают владение рабочими ресурсами после завершения предшественника
    private void StartWork(Func<CancellationToken, Task> operation, string failure, bool transition = true,
        IDisposable? input = null) {
        workStop?.Cancel();
        CancellationTokenSource replacement = new();
        workStop = replacement;
        transitioning = transition;
        work = RunAfterAsync(work, replacement, operation, failure, input);
        UpdateControls();
    }

    // отмененный запрос не меняет картинку, шкалу или сообщение следующего запроса
    private async Task RunAfterAsync(Task previous, CancellationTokenSource stop,
        Func<CancellationToken, Task> operation, string failure, IDisposable? input) {
        await Task.Yield();
        try {
            await previous;
            stop.Token.ThrowIfCancellationRequested();
            // отмененная подготовка могла поставить освобождение плеера в очередь в самом конце
            await cleanup;
            stop.Token.ThrowIfCancellationRequested();
            await operation(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception error) {
            if (!stop.IsCancellationRequested) {
                status.Text = $"{failure} {error.Message}";
            }
        }
        finally {
            input?.Dispose();
            if (ReferenceEquals(workStop, stop)) {
                workStop = null;
                transitioning = false;
                UpdateControls();
            }
            stop.Dispose();
        }
    }

    // переводим нативный плеер в отставку до запуска следующего декодера
    private void RetirePlayer() {
        VideoPlaybackSession? previous = playback;
        playback = null;
        if (previous is not null) {
            Retire(previous, Task.CompletedTask);
        }
    }

    // освобождаем ресурс после последнего обращения и перед созданием следующего плеера
    private void Retire(IDisposable resource, Task lastUse) {
        Task previous = cleanup;
        cleanup = Task.Run(async () => {
            await Task.WhenAll(previous, lastUse).ConfigureAwait(false);
            try {
                resource.Dispose();
            }
            catch (Exception error) {
                Trace.TraceWarning($"Ошибка освобождения распознавания: {error.Message}");
            }
        });
    }

    // время VLC относится к часам воспроизведения, точную метку отдельного кадра не выдаем
    private void SavePlaybackPosition() {
        if (playback is { } player && clip is { } selected) {
            long clock = player.Position;
            if (clock >= startingPosition) {
                position = Math.Min(clock, selected.Duration);
            }
        }
    }

    // обновляем шкалу без обратного вызова перемотки
    private void UpdateControls() {
        bool ready = active && clip is not null;
        open.IsEnabled = active && !picking;
        play.IsEnabled = ready && !transitioning;
        play.Content = playback is null ? "Воспроизвести" : "Пауза";
        timeline.IsEnabled = ready && !transitioning;
        time.IsVisible = ready;
        if (!draggingTimeline) {
            changingTimeline = true;
            timeline.Maximum = clip?.Duration ?? 1;
            timeline.Value = position;
            time.Text = FormatTime(position, clip?.Duration ?? 0);
            changingTimeline = false;
        }
    }

    // показываем собственную позицию и продолжительность выбранного ролика
    private static string FormatTime(long position, long duration) =>
        $"{TimeSpan.FromMilliseconds(position).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)} / " +
        TimeSpan.FromMilliseconds(duration).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private sealed record VideoClip(string Path, VideoFileIdentity? Identity, PixelSize Size, long Duration);
}
