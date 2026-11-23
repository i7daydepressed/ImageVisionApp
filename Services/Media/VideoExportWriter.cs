using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace ImageVisionApp.Services;

internal readonly record struct VideoExportProgress(long FramesWritten, long TotalFrames, VideoPresentationTime Time);

internal static class VideoExportWriter {
    private const int Nanoseconds = 1_000_000_000;

    // пишем в соседний временный файл и заменяем результат только после проверки обеих дорожек
    public static async Task WriteAsync(string sourcePath, VideoFileIdentity identity, string destination,
        PixelSize outputSize, IAsyncEnumerable<TimedVideoInput> frames, IProgress<VideoExportProgress>? progress,
        CancellationToken token) {
        token.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        destination = Path.GetFullPath(destination);
        bool mp4 = Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!mp4 && !Path.GetExtension(destination).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Для экспорта видео выберите файл MP4 или MKV", nameof(destination));
        }
        long frameBytes = VideoEditorFrame.GetBufferBytes(outputSize);
        if (outputSize.Width <= 0 || outputSize.Height <= 0 || frameBytes > Array.MaxLength) {
            throw new ArgumentException("Некорректные размеры кадра для экспорта", nameof(outputSize));
        }
        EnsureSeparateFile(sourcePath, identity, destination);
        VideoFileIdentity? destinationIdentity = VideoFileIdentity.TryCapture(destination);
        if (destinationIdentity is null && File.Exists(destination)) {
            throw new IOException("Не удалось подтвердить версию существующего файла назначения");
        }
        identity.EnsureCurrent(sourcePath);

        string ffprobe = FfprobeVideoMediaSource.FindFfprobe();
        string ffmpeg = FindFfmpeg(ffprobe);
        VideoExportProbe.VideoEnd tail = await VideoExportProbe.ReadVideoEndAsync(ffprobe, sourcePath, token)
            .ConfigureAwait(false);
        int outputScale = mp4 ? tail.Clock.Scale : 1000;
        if (outputScale > Nanoseconds) {
            throw new InvalidDataException("Временная шкала MP4 точнее шкалы записи предпросмотра");
        }
        identity.EnsureCurrent(sourcePath);
        token.ThrowIfCancellationRequested();
        progress?.Report(new(0, tail.TotalFrames, new(0, 1)));
        token.ThrowIfCancellationRequested();

        string directory = Path.GetDirectoryName(destination) ?? throw new IOException("Не найдена папка сохранения");
        string temporary = Path.Combine(directory, $".ImageVisionApp-{Guid.NewGuid():N}.partial");
        using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try {
            using IncrementalHash timeline = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = await EncodeAsync(ffmpeg, sourcePath, temporary, outputSize, tail, outputScale, mp4, frames,
                timeline, progress, token).ConfigureAwait(false);
            await VideoExportProbe.VerifyVideoAsync(ffprobe, temporary, outputSize, outputScale, mp4, written,
                timeline.GetHashAndReset(), ConvertTime(tail.End, outputScale), token).ConfigureAwait(false);
            await VideoExportProbe.VerifyAudioAsync(ffprobe, sourcePath, temporary, mp4, token).ConfigureAwait(false);
            identity.EnsureCurrent(sourcePath);
            EnsureSeparateFile(sourcePath, identity, destination);
            if (destinationIdentity is { } previous && VideoFileIdentity.TryCapture(destination) != previous) {
                throw new IOException("Файл назначения изменился во время экспорта. Результат не заменен.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: destinationIdentity.HasValue);
        }
        finally {
            File.Delete(temporary);
        }
    }

    // отправляем кадры по одному, проверяя физический номер и согласованную границу каждого кадра
    private static async Task<long> EncodeAsync(string ffmpeg, string source, string temporary, PixelSize size,
        VideoExportProbe.VideoEnd tail, int outputScale, bool mp4, IAsyncEnumerable<TimedVideoInput> frames,
        IncrementalHash timeline, IProgress<VideoExportProgress>? progress, CancellationToken token) {
        using Process process = StartEncoder(ffmpeg, source, temporary, size, outputScale, mp4);
        Task<string> errors = ReadErrorsAsync(process.StandardError);
        using CancellationTokenRegistration cancel = token.UnsafeRegister(static state => StopEncoder((Process)state!), process);
        bool completed = false;
        try {
            Stream pipe = process.StandardInput.BaseStream;
            await VideoExportRasterPipe.WriteHeaderAsync(pipe, size, tail.NominalFrameNanoseconds, token)
                .ConfigureAwait(false);
            long count = 0;
            long lastReported = Environment.TickCount64 - 100;
            long? previousTick = null;
            VideoPresentationTime? expectedTime = new VideoPresentationTime(0, 1);
            int frameBytes = checked((int)VideoEditorFrame.GetBufferBytes(size));

            await foreach (TimedVideoInput frame in frames.WithCancellation(token).ConfigureAwait(false)) {
                token.ThrowIfCancellationRequested();
                if (count >= tail.TotalFrames || frame.IsLast != (count == tail.TotalFrames - 1)) {
                    throw new InvalidDataException("Число полученных кадров отличается от числа кадров исходника");
                }
                if (frame.Ordinal != count || frame.Pixels.Size != size || frame.Pixels.Bgra.Length != frameBytes ||
                    !frame.Time.IsKnown || expectedTime is not { } expected || frame.Time.CompareTo(expected) != 0 ||
                    frame.Until is { } until && (!until.IsKnown || until.CompareTo(frame.Time) < 0) ||
                    frame.IsLast && frame.Time.CompareTo(tail.Last) != 0) {
                    throw new InvalidDataException("Нарушены порядок, размер или время кадров при экспорте");
                }

                long outputTick = ConvertTime(frame.Time, outputScale);
                if (mp4 && previousTick == outputTick) {
                    throw new InvalidDataException("MP4 не сохраняет одинаковые метки соседних кадров. Попробуйте MKV.");
                }
                VideoExportProbe.AppendTime(timeline, outputTick);
                long ns = ConvertTime(frame.Time, Nanoseconds);
                long end = ConvertTime(frame.Until ?? tail.End, Nanoseconds);
                await VideoExportRasterPipe.WriteFrameAsync(pipe, frame.Pixels.Bgra, ns, checked(end - ns), token)
                    .ConfigureAwait(false);
                expectedTime = frame.Until;
                previousTick = outputTick;
                count++;
                long now = Environment.TickCount64;
                if (frame.IsLast || now - lastReported >= 100) {
                    progress?.Report(new(count, tail.TotalFrames, frame.Time));
                    lastReported = now;
                }
            }
            if (count != tail.TotalFrames || expectedTime is not null) {
                throw new InvalidDataException("Не получен подтвержденный конец исходного видеоряда");
            }

            await pipe.FlushAsync(token).ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) {
                throw await EncoderFailureAsync(process, errors).ConfigureAwait(false);
            }
            completed = true;
            return count;
        }
        catch (IOException error) when (token.IsCancellationRequested) {
            throw new OperationCanceledException("Экспорт видео отменен", error, token);
        }
        catch (IOException) when (!token.IsCancellationRequested && process.HasExited && process.ExitCode != 0) {
            throw await EncoderFailureAsync(process, errors).ConfigureAwait(false);
        }
        finally {
            if (!completed) {
                StopEncoder(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await errors.ConfigureAwait(false);
        }
    }

    // перекодируем только видеоряд, а все аудиодорожки передаем в выбранный контейнер
    private static Process StartEncoder(string executable, string source, string temporary, PixelSize size, int outputScale,
        bool mp4) {
        ProcessStartInfo info = new(executable) {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        string pixelFormat = (size.Width & 1) == 0 && (size.Height & 1) == 0 ? "yuv420p" : "yuv444p";
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-copyts",
            "-protocol_whitelist", "pipe", "-f", "matroska", "-probesize", "32768", "-analyzeduration", "1", "-i", "pipe:0",
            "-protocol_whitelist", "file", "-i", source, "-map", "0:v:0", "-map", "1:a?", "-map_metadata", "-1",
            "-map_chapters", "-1", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-bf", "0", "-threads:v", "2",
            "-pix_fmt", pixelFormat, "-fps_mode:v", "passthrough", "-enc_time_base:v", "demux", "-c:a", "copy",
            "-avoid_negative_ts", "disabled", "-max_interleave_delta", "1000000" }) {
            info.ArgumentList.Add(argument);
        }
        if (mp4) {
            info.ArgumentList.Add("-video_track_timescale");
            info.ArgumentList.Add(outputScale.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("-movflags");
            info.ArgumentList.Add("+faststart");
        }
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(mp4 ? "mp4" : "matroska");
        info.ArgumentList.Add(temporary);
        try {
            return Process.Start(info) ?? throw new IOException("Не удалось запустить FFmpeg для записи видео");
        }
        catch (Win32Exception error) {
            throw new IOException("Не удалось запустить FFmpeg для записи видео", error);
        }
    }

    // сохраняем точную шкалу mp4 или округляем до разрешения выбранного контейнера
    private static long ConvertTime(VideoPresentationTime time, int scale) =>
        checked((long)(((Int128)time.Value * scale + time.Timescale / 2) / time.Timescale));

    // не разрешаем заменить открытый исходник через другой путь или жесткую ссылку
    private static void EnsureSeparateFile(string source, VideoFileIdentity identity, string destination) {
        if (string.Equals(source, destination, StringComparison.Ordinal) ||
            VideoFileIdentity.TryCapture(destination) is { } current &&
            current.Device == identity.Device && current.FileId == identity.FileId) {
            throw new IOException("Выберите другой файл. Сохранение поверх открытого исходника недоступно.");
        }
    }

    // совместимая установка обычно содержит FFmpeg рядом с уже выбранным ffprobe
    private static string FindFfmpeg(string ffprobe) {
        string? configured = Environment.GetEnvironmentVariable("IMAGEVISIONAPP_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) {
            return FfprobeVideoMediaSource.IsExecutable(configured) ? Path.GetFullPath(configured) :
                throw new InvalidDataException("Указанный в IMAGEVISIONAPP_FFMPEG FFmpeg отсутствует или не исполняется");
        }
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string? sibling = Path.GetDirectoryName(ffprobe);
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string directory in new[] { sibling ?? "", AppContext.BaseDirectory }.Concat(path)) {
            if (Path.IsPathFullyQualified(directory) && FfprobeVideoMediaSource.IsExecutable(Path.Combine(directory, name))) {
                return Path.Combine(directory, name);
            }
        }
        throw new InvalidDataException("Для экспорта нужен установленный FFmpeg с кодировщиком libx264");
    }

    // освобождаем канал ошибок, сохраняя только короткое описание сбоя
    private static async Task<string> ReadErrorsAsync(StreamReader reader) {
        const int limit = 700;
        char[] buffer = new char[512];
        StringBuilder message = new(limit);
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) {
            message.Append(buffer, 0, Math.Min(count, limit - message.Length));
        }
        return message.ToString().Trim();
    }

    private static async Task<IOException> EncoderFailureAsync(Process process, Task<string> errors) {
        string message = await errors.ConfigureAwait(false);
        return new IOException($"FFmpeg не завершил экспорт (код {process.ExitCode}). {message}");
    }

    // отмена прерывает запись даже при ожидании места в системном канале
    private static void StopEncoder(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited) {
        }
        catch (Win32Exception) when (process.HasExited) {
        }
    }
}
