using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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
    private readonly Button videoFrameSaveButton = new() {
        Content = "Сохранить кадр…", HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false
    };
    private readonly MenuItem videoFrameSaveMenuItem = new() { Header = "Сохранить кадр…", IsEnabled = false };
    private readonly TextBlock videoFrameSaveNotice = new() {
        IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right,
        TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private bool videoFrameSaveBusy;

    // при перетаскивании время под ползунком относится к запрашиваемому, а не видимому кадру
    private void UpdateVideoFrameSaveButton() {
        videoFrameSaveButton.IsEnabled = CanRequestVideoFrameSave &&
            DataContext is MainViewModel { CanSaveCurrentVideoFrame: true };
        if (videoExportButton.Flyout?.IsOpen == true) {
            UpdateVideoFrameSaveMenuItem();
        }
    }

    private bool CanRequestVideoFrameSave => !videoFrameSaveBusy && videoExportStop is null &&
        videoDragDocument is null && !IsComparisonPageVisible;

    // доступность команды меню следует за готовностью растра выбранного вида
    private void UpdateVideoFrameSaveMenuItem() =>
        videoFrameSaveMenuItem.IsEnabled = CanRequestVideoFrameSave && DataContext is MainViewModel viewModel &&
            viewModel.CanSaveVideoFrame(SelectedVideoExportView(viewModel));

    // сохраняем снимок нажатого вида, даже если номер кадра позже сменится в том же документе
    private async void SaveVideoFrameButton_OnClick(object? sender, RoutedEventArgs e) {
        if (!CanRequestVideoFrameSave ||
            DataContext is not MainViewModel { CurrentDocument: { Kind: MediaKind.Video, SourcePath: { } source } document } viewModel) {
            return;
        }

        videoFrameSaveBusy = true;
        videoFrameSaveNotice.IsVisible = false;
        UpdateVideoFrameSaveButton();
        try {
            VideoExportView view = SelectedVideoExportView(viewModel);
            if (viewModel.CapturePausedVideoFramePng(view) is not { } png) {
                return;
            }

            string viewName = view switch {
                VideoExportView.EditorOutput => "edited",
                VideoExportView.AnalysisInput => "analysis_input",
                VideoExportView.AnalysisOutput => "analysis_result",
                VideoExportView.SequenceInput => "video_sequence_input",
                VideoExportView.SequenceOutput => "video_sequence_result",
                _ => "original"
            };
            string stamp = TimeSpan.FromMilliseconds(viewModel.PlaybackStatus.Position).ToString(@"hh\-mm\-ss\.fff", CultureInfo.InvariantCulture);
            FilePickerFileType pngType = new("PNG") { Patterns = ["*.png"] };
            using IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Сохранить видеокадр",
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(document.FileName)}_{stamp}_{viewName}",
                DefaultExtension = "png", FileTypeChoices = [pngType], SuggestedFileType = pngType
            });
            if (file is null || !IsCurrentDocument()) {
                return;
            }

            string destination = file.TryGetLocalPath() ??
                throw new IOException("Выберите локальную папку для сохранения PNG.");
            await WriteVideoFramePngAsync(png, source, Path.GetFullPath(destination));
            ShowNotice($"Кадр сохранён: {file.Name}");
        }
        catch (Exception error) {
            ShowNotice($"Не удалось сохранить кадр. {error.Message}");
        }
        finally {
            videoFrameSaveBusy = false;
            UpdateVideoFrameSaveButton();
        }

        bool IsCurrentDocument() =>
            !mediaWindowClosed && ReferenceEquals(DataContext, viewModel) && ReferenceEquals(viewModel.CurrentDocument, document);

        void ShowNotice(string text) {
            if (IsCurrentDocument()) {
                videoFrameSaveNotice.Text = text;
                videoFrameSaveNotice.IsVisible = true;
            }
        }
    }

    // публикуем полностью записанный png в той же папке и защищаем исходный ролик
    private static async Task WriteVideoFramePngAsync(byte[] png, string source, string destination) {
        if (!Path.GetExtension(destination).Equals(".png", StringComparison.OrdinalIgnoreCase)) {
            throw new IOException("Укажите имя файла с расширением .png.");
        }
        VideoFileIdentity? destinationIdentity = VideoFileIdentity.TryCapture(destination);
        if (string.Equals(Path.GetFullPath(source), destination, StringComparison.Ordinal) ||
            destinationIdentity is { } existing && VideoFileIdentity.TryCapture(source) is { } original &&
            existing.Device == original.Device && existing.FileId == original.FileId) {
            throw new IOException("Выберите другое имя. Перезапись исходного ролика недоступна.");
        }

        bool replace = File.Exists(destination);
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, $".ImageVisionApp-{Guid.NewGuid():N}.partial");
        FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        try {
            await using (output.ConfigureAwait(false)) {
                await output.WriteAsync(png).ConfigureAwait(false);
            }

            if (replace && (destinationIdentity != VideoFileIdentity.TryCapture(destination) || !File.Exists(destination))) {
                throw new IOException("Файл назначения изменился во время сохранения.");
            }
            File.Move(temporary, destination, overwrite: replace);
        }
        finally {
            File.Delete(temporary);
        }
    }
}
