using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private readonly Button videoExportButton = new() { Content = "Экспорт видео…" };
    private CancellationTokenSource? videoExportStop;

    // фиксируем выбранный вид до диалогов и передаём фоновой задаче независимый снимок
    private async void ExportVideoButton_OnClick(object? sender, RoutedEventArgs e) {
        if (videoExportStop is not null ||
            DataContext is not MainViewModel { CurrentDocument: { Kind: MediaKind.Video, SourcePath: not null } document } viewModel) {
            return;
        }

        using CancellationTokenSource stop = new();
        videoExportStop = stop;
        videoExportButton.IsEnabled = false;
        try {
            VideoExportPlan plan = viewModel.CaptureVideoExportPlan(SelectedVideoExportView(viewModel));
            if (!await ConfirmVideoExportAsync(plan.Caption) || mediaWindowClosed || stop.IsCancellationRequested) {
                return;
            }

            FilePickerFileType mp4 = new("MP4") { Patterns = ["*.mp4"] };
            FilePickerFileType mkv = new("MKV") { Patterns = ["*.mkv"] };
            using IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Сохранить видео",
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(document.FileName)}_result",
                DefaultExtension = "mp4",
                FileTypeChoices = [mp4, mkv],
                SuggestedFileType = mp4
            });
            if (file is null || mediaWindowClosed || stop.IsCancellationRequested ||
                !ReferenceEquals(viewModel, DataContext) || !ReferenceEquals(document, viewModel.CurrentDocument)) {
                return;
            }

            string destination = file.TryGetLocalPath() ??
                throw new InvalidOperationException("Для записи видео выберите локальную папку.");
            viewModel.PauseVideoPlayback();
            await ShowVideoExportProgressAsync(plan, destination, viewModel, stop);
        }
        catch (Exception error) {
            if (!mediaWindowClosed && ReferenceEquals(viewModel, DataContext)) {
                string message = $"Не удалось экспортировать видео. {error.Message}";
                viewModel.ShowError(message);
                Window dialog = CreateVideoExportDialog("Экспорт видео");
                Button close = new() { Content = "Закрыть", IsDefault = true, IsCancel = true,
                    HorizontalAlignment = HorizontalAlignment.Right };
                close.Click += (_, _) => dialog.Close();
                dialog.Content = new StackPanel {
                    Margin = new Thickness(18), Spacing = 14,
                    Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close }
                };
                await dialog.ShowDialog(this);
            }
        }
        finally {
            videoExportStop = null;
            videoExportButton.IsEnabled = true;
        }
    }

    // учитываем выбранный «Вход» или «Результат», а также исходный вид редактора
    private VideoExportView SelectedVideoExportView(MainViewModel viewModel) {
        if (ReferenceEquals(pageHost.Content, videoForegroundPage)) {
            return videoForegroundTabs.SelectedIndex == 1 ? VideoExportView.SequenceOutput : VideoExportView.SequenceInput;
        }
        if (ReferenceEquals(pageHost.Content, analysisPage)) {
            return viewModel.VideoAnalysisResultTabSelected ? VideoExportView.AnalysisOutput : VideoExportView.AnalysisInput;
        }
        return ReferenceEquals(pageHost.Content, editingPage) && viewModel.VideoEditorPreviewTabIndex == 1
            ? VideoExportView.EditorOutput : VideoExportView.Original;
    }

    // пользователь подтверждает полный ролик с применёнными настройками выбранного вида
    private Task<bool> ConfirmVideoExportAsync(string caption) {
        Window dialog = CreateVideoExportDialog("Экспорт видео");
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        Button choose = new() { Content = "Выбрать файл…", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(false);
        choose.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel {
            Margin = new Thickness(18),
            Spacing = 14,
            Children = {
                new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold },
                new TextBlock {
                    Text = "Сохранится весь ролик с применёнными настройками и исходным звуком. Обработка может занять время.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, choose } }
            }
        };
        return dialog.ShowDialog<bool>(this);
    }

    // закрытие окна сначала отменяет рабочую задачу и даёт ей удалить временный файл
    private async Task ShowVideoExportProgressAsync(VideoExportPlan plan, string destination,
        MainViewModel viewModel, CancellationTokenSource stop) {
        Window dialog = CreateVideoExportDialog("Сохранение видео");
        ProgressBar bar = new() { IsIndeterminate = true };
        TextBlock status = new() { Text = "Подсчитываем кадры исходного видео…", TextWrapping = TextWrapping.Wrap };
        Button action = new() { Content = "Отменить", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        bool finished = false;
        void CancelExport() {
            stop.Cancel();
            status.Text = "Отмена экспорта…";
            action.IsEnabled = false;
        }
        dialog.Closing += (_, eventArgs) => {
            if (!finished) {
                eventArgs.Cancel = eventArgs.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;
                CancelExport();
            }
        };
        action.Click += (_, _) => {
            if (finished) {
                dialog.Close();
            }
            else {
                CancelExport();
            }
        };
        dialog.Content = new StackPanel {
            Margin = new Thickness(18), Spacing = 14,
            Children = { new TextBlock { Text = plan.Caption, TextWrapping = TextWrapping.Wrap }, status, bar, action }
        };

        try {
            Task visible = dialog.ShowDialog(this);
            IProgress<VideoExportProgress> progress = new Progress<VideoExportProgress>(report => {
                if (!finished && !stop.IsCancellationRequested) {
                    bar.Maximum = report.TotalFrames;
                    bar.Value = report.FramesWritten;
                    bar.IsIndeterminate = false;
                    status.Text = $"Обработано {report.FramesWritten} из {report.TotalFrames} кадров";
                    if (report.FramesWritten == report.TotalFrames) {
                        status.Text += "\nЗавершаем запись и проверяем файл…";
                    }
                }
            });
            try {
                await Task.Run(() => VideoExportService.ExportAsync(plan, destination, progress, stop.Token));
                status.Text = $"Видео сохранено: {Path.GetFileName(destination)}";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) {
                status.Text = "Экспорт отменён.";
            }
            catch (Exception error) {
                status.Text = $"Не удалось экспортировать видео. {error.Message}";
            }
            finished = true;
            bar.IsVisible = false;
            action.Content = "Закрыть";
            action.IsEnabled = true;
            if (!mediaWindowClosed && ReferenceEquals(viewModel, DataContext)) {
                viewModel.StatusMessage = status.Text;
            }
            await visible;
        }
        finally {
            finished = true;
            if (dialog.IsVisible) {
                dialog.Close();
            }
        }
    }

    // общая ширина оставляет место имени вида и сообщению о ходе записи
    private static Window CreateVideoExportDialog(string title) => new() {
        Title = title, Width = 470, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };
}
