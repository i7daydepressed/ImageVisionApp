using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

internal sealed partial class FaceRecognitionView {
    private FaceImage? photoPixels;
    private Bitmap? orientedPhoto;
    private string? photoDecodeError;
    private bool photoDecodeRequested;
    private bool photoRecognitionRequested;
    private Task photoDecode = Task.CompletedTask;
    private Task photoRecognition = Task.CompletedTask;
    private CancellationTokenSource? stopPhotoDecode;
    private CancellationTokenSource? stopPhotoRecognition;

    // исходное фото декодируем один раз для обеих панелей, включая выключенный метод
    private void RefreshPhotoInput(MainViewModel media) {
        if (photoDecodeRequested || !photoDecode.IsCompleted || media.ReadFacePhotoSource() is not { } source) {
            return;
        }
        photoDecodeRequested = true;
        var stop = new CancellationTokenSource();
        stopPhotoDecode = stop;
        photoDecode = DecodePhotoAsync(source.Document, source.Bytes, stop);
    }

    // держим одну ориентированную подложку для исходных пикселей и координат рамок
    private async Task DecodePhotoAsync(MediaDocument document, byte[] bytes, CancellationTokenSource stop) {
        try {
            FaceImage decoded = await Task.Run(() => {
                using MemoryStream input = new(bytes, writable: false);
                return FacePhotoDecoder.Decode(input, stop.Token);
            }, stop.Token);
            if (IsCurrentPhoto(document, stop.Token)) {
                orientedPhoto = FaceRecognitionPreview.CreateBitmap(decoded);
                photoPixels = decoded;
                UpdateInput();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception error) {
            if (IsCurrentPhoto(document, stop.Token)) {
                photoDecodeError = $"Не удалось подготовить фото. {error.Message}";
            }
        }
        finally {
            stopPhotoDecode = null;
            stop.Dispose();
            QueueRefresh();
        }
    }

    // при смене вывода ждем возврата прежней работы и запускаем только текущий снимок
    private void RefreshPhotoRecognition(MediaDocument document) {
        if (photoPixels is not { } pixels) {
            SetStatus(photoDecodeError ?? "Готовим фото…");
            return;
        }
        if (photoRecognitionRequested) {
            return;
        }
        if (previewHasNoResult) {
            preview.ShowBitmap(orientedPhoto);
            previewHasNoResult = false;
            SetStatus(recognitionMode == FaceRecognitionMode.DetectOnly ? "Ищем лица…" : "Распознаем фото…");
        }
        if (!photoRecognition.IsCompleted) {
            return;
        }
        photoRecognitionRequested = true;
        var stop = new CancellationTokenSource();
        stopPhotoRecognition = stop;
        photoRecognition = RecognizePhotoAsync(document, pixels, recognitionMode, stop);
    }

    // расчеты не меняют исходную подложку и могут опубликовать слой только в своем режиме
    private async Task RecognizePhotoAsync(MediaDocument document, FaceImage pixels, FaceRecognitionMode mode,
        CancellationTokenSource stop) {
        try {
            FaceRecognitionFrame frame = await Task.Run(() => {
                var (paths, catalogue) = FaceRecognitionService.LoadSource(mode);
                stop.Token.ThrowIfCancellationRequested();
                using FaceRecognitionService service = new(paths.CreatePipeline(mode), catalogue);
                return service.Recognize(pixels, stop.Token);
            }, stop.Token);
            if (CanDisplayPhoto(document, mode, stop.Token)) {
                FaceVideoResult result = new(frame.Faces, frame.EmptyCatalogue, null, null, frame.ShowLabels);
                preview.ShowBitmap(orientedPhoto, result);
                ShowResultStatus(result);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception error) {
            if (CanDisplayPhoto(document, mode, stop.Token)) {
                SetStatus($"Не удалось обработать фото. {error.Message}");
            }
        }
        finally {
            stopPhotoRecognition = null;
            stop.Dispose();
            QueueRefresh();
        }
    }

    // даже повторно открытый файл имеет свою ссылку на документ и новый снимок
    private bool IsCurrentPhoto(MediaDocument document, CancellationToken token) =>
        active && !token.IsCancellationRequested && ReferenceEquals(document, model?.CurrentDocument);

    // переход на вход или смена метода отзывают право показать рамки
    private bool CanDisplayPhoto(MediaDocument document, FaceRecognitionMode mode, CancellationToken token) =>
        ShowsResult && methodEnabled && recognitionMode == mode && IsCurrentPhoto(document, token);

    // освобождаем заимствованный bitmap только после отключения обеих панелей
    private void ClearPhotoInput() {
        stopPhotoDecode?.Cancel();
        photoDecodeRequested = false;
        photoDecodeError = null;
        photoPixels = null;
        if (orientedPhoto is not { } previous) {
            return;
        }
        orientedPhoto = null;
        if (ReferenceEquals(input.Source, previous)) {
            input.Source = null;
        }
        if (ReferenceEquals(unprocessed.Source, previous)) {
            unprocessed.Source = null;
        }
        previous.Dispose();
    }
}
