using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

// повторно рисует изменённые пиксели общего WriteableBitmap
internal sealed class VideoPreviewImage : Image {
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<VideoPreviewImage, int>("Revision");

    static VideoPreviewImage() => AffectsRender<VideoPreviewImage>(RevisionProperty);
}

public partial class MainWindow {
    private const int VideoKeyboardSeekMilliseconds = 3000;
    private readonly Button videoPlayButton = new() { MinWidth = 130 };
    private readonly Slider videoTimeline = new() {
        SmallChange = VideoKeyboardSeekMilliseconds,
        LargeChange = 5000,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock videoTime = new() {
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new Avalonia.Media.FontFamily("Menlo, monospace")
    };
    private readonly TextBlock videoNotice = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool updatingVideoTimeline;
    private bool videoSpaceHeld;
    private bool videoPreviewUpHeld;
    private MediaDocument? videoDragDocument;

    // создаёт один таймлайн над страницами, который не пересоздаётся при навигации
    private Grid CreateVideoPlaybackControls() {
        Grid controls = new() {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(12, 0, 12, 0)
        };
        controls.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        AutomationProperties.SetName(videoTimeline, "Позиция видео");

        videoPlayButton.Click += (_, _) => (DataContext as MainViewModel)?.ToggleVideoPlayback();
        AddHandler(KeyDownEvent, VideoPlayback_OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, VideoPlayback_OnKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => {
            videoSpaceHeld = false;
            videoPreviewUpHeld = false;
        };
        videoTimeline.ValueChanged += VideoTimeline_OnValueChanged;
        videoTimeline.AddHandler(PointerPressedEvent, VideoTimeline_OnPointerPressed, RoutingStrategies.Tunnel);
        videoTimeline.AddHandler(PointerReleasedEvent, (_, _) => FinishVideoSeek(), RoutingStrategies.Bubble, true);
        videoTimeline.AddHandler(Thumb.DragCompletedEvent, (_, _) => FinishVideoSeek(), handledEventsToo: true);

        Grid.SetColumn(videoTimeline, 1);
        Grid.SetColumn(videoTime, 2);
        Grid.SetColumn(videoNotice, 3);
        controls.Children.Add(videoPlayButton);
        controls.Children.Add(videoTimeline);
        controls.Children.Add(videoTime);
        controls.Children.Add(videoNotice);
        Grid.SetColumn(videoExportButton, 4);
        ConfigureVideoExportButton();
        controls.Children.Add(videoExportButton);
        Grid.SetRow(videoFrameSaveNotice, 1);
        Grid.SetColumnSpan(videoFrameSaveNotice, 5);
        controls.Children.Add(videoFrameSaveNotice);
        UpdateVideoPlaybackControls();
        return controls;
    }

    // управляет плеером с клавиатуры, сохраняя обычный ввод в текстовых полях
    private void VideoPlayback_OnKeyDown(object? sender, KeyEventArgs e) {
        if (IsComparisonPageVisible || IsFacesDatabaseVisible || UsesFacesSectionKeys(e.Source) ||
            videoExportButton.Flyout?.IsOpen == true ||
            e.Key == Key.Space && (IsComparisonButtonInput(e.Source) || IsVideoExportButtonInput(e.Source) ||
                IsFacesNavigationInput(e.Source)) ||
            UsesVideoForegroundControlKey(e) ||
            e.KeyModifiers != KeyModifiers.None || e.Source is TextBox ||
            DataContext is not MainViewModel viewModel) {
            return;
        }

        if (IsVideoPreviewUpKey(e)) {
            if (viewModel.IsVideoOpen && editingPage is not null && ReferenceEquals(pageHost.Content, editingPage) &&
                !UsesUpForInput(e.Source)) {
                if (!videoPreviewUpHeld) {
                    videoPreviewUpHeld = true;
                    viewModel.VideoEditorPreviewTabIndex = viewModel.VideoEditorPreviewTabIndex == 0 ? 1 : 0;
                }
                e.Handled = true;
            }
            return;
        }

        switch (e.Key) {
            case Key.Left or Key.Right when viewModel.PlaybackStatus.CanSeek:
                viewModel.SeekVideoByKeyboard(e.Key == Key.Left ? -1 : 1, viewModel.PreviewSettings.ArrowStep);
                e.Handled = true;
                break;
            case Key.Space when videoSpaceHeld || viewModel.PlaybackStatus.CanPlay:
                if (!videoSpaceHeld) {
                    videoSpaceHeld = true;
                    viewModel.ToggleVideoPlayback();
                }
                e.Handled = true;
                break;
        }
    }

    // переключатели вида и кнопки раздела сохраняют собственный клавиатурный ввод
    private bool UsesFacesSectionKeys(object? source) =>
        IsFacesPageVisible && source is Visual visual && visual.GetSelfAndVisualAncestors().Any(control =>
            control is TabStrip || control is Button && !ReferenceEquals(control, videoPlayButton));

    // пробел на кнопках сохранения выполняет выбранную команду
    private bool IsVideoExportButtonInput(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().Any(control =>
            ReferenceEquals(control, videoExportButton) || ReferenceEquals(control, videoFrameSaveButton));

    // завершает удержание клавиш, чтобы каждое нажатие переключало состояние только один раз
    private void VideoPlayback_OnKeyUp(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Space && videoSpaceHeld) {
            videoSpaceHeld = false;
            e.Handled = true;
        }
        if (videoPreviewUpHeld && IsVideoPreviewUpKey(e)) {
            videoPreviewUpHeld = false;
            e.Handled = true;
        }
    }

    // используем физическую стрелку, если платформа сообщила положение клавиши
    private static bool IsVideoPreviewUpKey(KeyEventArgs e) =>
        e.PhysicalKey == PhysicalKey.ArrowUp || (e.PhysicalKey == PhysicalKey.None && e.Key == Key.Up);

    // оставляем стрелку полям ввода, ползункам и спискам, включая их вложенные элементы
    private static bool UsesUpForInput(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().Any(control =>
            control is TextBox or NumericUpDown or RangeBase or ComboBoxItem or ListBoxItem or TreeView or TreeViewItem or
                MenuItem or RadioButton or (SelectingItemsControl and not TabControl and not TabStrip));

    // запоминает начало жеста на ползунке или дорожке, оставляя подготовку кадра до отпускания
    private void VideoTimeline_OnPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(videoTimeline).Properties.IsLeftButtonPressed &&
            DataContext is MainViewModel viewModel && viewModel.PlaybackStatus.CanSeek) {
            videoDragDocument = viewModel.CurrentDocument;
            UpdateVideoFrameSaveButton();
            viewModel.PauseVideoPlayback();
        }
    }

    // завершает жест одним запросом, если за это время открыт всё тот же документ
    private void FinishVideoSeek() {
        if (videoDragDocument is not { } document) {
            return;
        }

        videoDragDocument = null;
        if (DataContext is MainViewModel viewModel && ReferenceEquals(viewModel.CurrentDocument, document)) {
            viewModel.RequestVideoSeek((long)videoTimeline.Value);
        }
        UpdateVideoPlaybackControls();
    }

    // после клика или клавиши запрашивает кадр, при перетаскивании оставляет один запрос на отпускание
    private void VideoTimeline_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e) {
        if (updatingVideoTimeline || DataContext is not MainViewModel viewModel) {
            return;
        }
        if (videoDragDocument is not null) {
            videoTime.Text = FormatVideoTime((long)e.NewValue, viewModel.PlaybackStatus.Duration);
        }
        else {
            viewModel.RequestVideoSeek((long)e.NewValue);
        }
    }

    // отображает состояние общего плеера и не отправляет обратную команду при обновлении ползунка
    private void UpdateVideoPlaybackControls() {
        MainViewModel? viewModel = DataContext as MainViewModel;
        VideoPlaybackStatus status = viewModel?.PlaybackStatus ?? default;
        if (!ReferenceEquals(videoDragDocument, viewModel?.CurrentDocument)) {
            videoDragDocument = null;
        }
        UpdateVideoFrameSaveButton();

        videoPlayButton.Content = status.IsPlaying ? "Пауза" : "Воспроизвести";
        videoPlayButton.IsEnabled = status.CanPlay;
        videoTimeline.IsEnabled = status.CanSeek;
        videoNotice.Text = status.Notice;
        videoNotice.IsVisible = status.Notice is not null;

        updatingVideoTimeline = true;
        try {
            videoTimeline.Maximum = Math.Max(status.Duration, 1);
            if (videoDragDocument is null) {
                videoTimeline.Value = status.Position;
            }
            videoTime.Text = FormatVideoTime((long)videoTimeline.Value, status.Duration);
        }
        finally {
            updatingVideoTimeline = false;
        }
    }

    // показывает время в миллисекундах, а для длинных роликов также выводит часы
    private static string FormatVideoTime(long position, long duration) {
        string format = duration >= 3_600_000 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff";
        string current = TimeSpan.FromMilliseconds(position).ToString(format);
        string total = TimeSpan.FromMilliseconds(duration).ToString(format);
        return $"{current} / {total}";
    }
}
