using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using OpenCvSharp;

namespace ImageVisionApp.Services;

internal sealed class FfprobeVideoMediaSource(
    string path, string ffprobe, PixelSize sourceSize, int timescale, int timeUnit, VideoFileIdentity identity,
    int decodeThreads) : IVideoMediaSource {
    // свойство CAP_PROP_N_THREADS открытого в проекте OpenCV 4.13
    private const int DecodeThreadCountProperty = 70;
    private const double TimestampToleranceMs = 0.000001;

    public PixelSize FrameSize { get; } = VideoPreviewDimensions.Fit(sourceSize);
    public long ReadScratchBytes => checked((long)sourceSize.Width * sourceSize.Height * 3 +
        (sourceSize == FrameSize ? 0 : (long)FrameSize.Width * FrameSize.Height * 3) +
        VideoEditorFrame.GetBufferBytes(FrameSize) + (long)FrameSize.Width * 3);

    internal FfprobeVideoMediaSource WithDecoderThreads(int threads) =>
        threads == decodeThreads ? this : new(path, ffprobe, sourceSize, timescale, timeUnit, identity, threads);

    // подтверждаем общую нулевую шкалу и размер единственной дорожки той же версии файла
    public static Task<FfprobeVideoMediaSource> OpenAsync(string path, VideoFileIdentity? expected,
        CancellationToken token) => OpenWithDecoderThreadsAsync(path, expected, 1, token);

    // задаем предел потоков декодера при открытии, отдельный от порядка выдачи кадров
    internal static async Task<FfprobeVideoMediaSource> OpenWithDecoderThreadsAsync(string path, VideoFileIdentity? expected,
        int decodeThreads, CancellationToken token) {
        VideoFileIdentity identity = expected ??
            throw new InvalidDataException("Не удалось подтвердить версию открытого файла");
        identity.EnsureCurrent(path);
        string executable = FindFfprobe();
        await using FfprobeRun probe = new(executable, path, "v",
            "stream=index,time_base,start_pts,width,height:stream_disposition=attached_pic:stream_side_data=displaymatrix,rotation:stream_tags=rotate:format=start_time",
            token);
        PixelSize? size = null;
        bool knownOrigin = false;
        int scale = 0;
        int unit = 0;
        while (await probe.ReadLineAsync(token).ConfigureAwait(false) is { } line) {
            string[] fields = line.Split('|');
            if (fields[0] == "format") {
                knownOrigin = decimal.TryParse(GetValue(fields, "start_time"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal origin) && origin == 0;
            }
            else if (fields[0] == "stream") {
                if (size is not null || GetValue(fields, "disposition:attached_pic") == "1") {
                    throw new InvalidDataException("Для подготовки нужна одна видеодорожка без обложки");
                }
                string? startPts = GetValue(fields, "start_pts");
                if (!long.TryParse(startPts, NumberStyles.Integer, CultureInfo.InvariantCulture, out long originPts)) {
                    throw new InvalidDataException("У видеодорожки нет исходной метки начала");
                }
                if (originPts != 0) {
                    throw new InvalidDataException("Начало видеодорожки отличается от общей нулевой позиции");
                }
                string[]? timeBase = GetValue(fields, "time_base")?.Split('/');
                if (timeBase is not { Length: 2 } ||
                    !TryPositiveInt(timeBase[0], out unit) || !TryPositiveInt(timeBase[1], out scale) ||
                    !TryPositiveInt(GetValue(fields, "width"), out int width) ||
                    !TryPositiveInt(GetValue(fields, "height"), out int height)) {
                    throw new InvalidDataException("У видеодорожки нет подтвержденной шкалы или размеров");
                }
                string? rotation = GetValue(fields, "side_datum/display_matrix:rotation") ?? GetValue(fields, "rotation");
                string? matrix = GetValue(fields, "side_datum/display_matrix:displaymatrix") ?? GetValue(fields, "displaymatrix");
                if (matrix is not null || rotation is not null || GetValue(fields, "tag:rotate") is not null) {
                    if (!TryReadPureRotation(matrix, out bool swapsAxes)) {
                        throw new InvalidDataException("Неподдерживаемая матрица ориентации видеодорожки");
                    }
                    if (swapsAxes) {
                        (width, height) = (height, width);
                    }
                }
                size = new(width, height);
            }
        }
        await probe.CheckExitAsync(token).ConfigureAwait(false);
        if (!knownOrigin || size is null) {
            throw new InvalidDataException("Не удалось подтвердить общий ноль времени видео");
        }
        identity.EnsureCurrent(path);
        return new(path, executable, size.Value, scale, unit, identity, decodeThreads);
    }

    // допускаем только ортогональный поворот с единичным масштабом, без отражения и сдвига
    private static bool TryReadPureRotation(string? matrix, out bool swapsAxes) {
        swapsAxes = false;
        if (matrix is null) {
            return false;
        }
        string[] rows = matrix.Split("\\n", StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length != 3) {
            return false;
        }
        Span<int> coefficients = stackalloc int[9];
        for (int row = 0; row < rows.Length; row++) {
            int separator = rows[row].IndexOf(':');
            if (separator == -1) {
                return false;
            }
            string[] values = rows[row][(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 3) {
                return false;
            }
            for (int column = 0; column < values.Length; column++) {
                if (!int.TryParse(values[column], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out coefficients[row * 3 + column])) {
                    return false;
                }
            }
        }
        const int one = 1 << 16;
        int a = coefficients[0], b = coefficients[1], c = coefficients[3], d = coefficients[4];
        bool canonical = a != 0
            ? (a == one || a == -one) && a == d && b == 0 && c == 0
            : (b == one || b == -one) && c == -b && d == 0;
        swapsAxes = canonical && a == 0;
        return canonical && coefficients[2] == 0 && coefficients[5] == 0 &&
            coefficients[6] == 0 && coefficients[7] == 0 && coefficients[8] == 1 << 30;
    }

    // перед повторным использованием готового участка проверяем ту же версию источника
    internal void EnsureCurrentFile() => identity.EnsureCurrent(path);

    private readonly record struct StreamFrame(long Ordinal, VideoPresentationTime Time,
        VideoPresentationTime? Until, bool Repeated);

    // читаем первую метку и начинаем поиск только после проверки общего нуля
    private async ValueTask<StreamFrame> ReadFirstFrameAsync(FfprobeRun probe, CancellationToken token) {
        VideoPresentationTime time = await ReadNextTimeAsync(probe, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("В видеопотоке нет кадров");
        if (time.Value != 0) {
            throw new InvalidDataException("Первый кадр отсутствует на общей нулевой позиции");
        }
        VideoPresentationTime? until = await ReadBoundaryAsync(probe, time, token).ConfigureAwait(false);
        return new(0, time, until, until == time);
    }

    // сдвигаем курсор меток на одну запись даже при равных времени соседних кадров
    private async ValueTask<StreamFrame> NextFrameAsync(FfprobeRun probe, StreamFrame current, CancellationToken token) {
        VideoPresentationTime time = current.Until!.Value;
        VideoPresentationTime? until = await ReadBoundaryAsync(probe, time, token).ConfigureAwait(false);
        return new(current.Ordinal + 1, time, until, current.Repeated || until == time);
    }

    // от времени берем последний кадр интервала, оставляя дубли различимыми по номеру
    private async ValueTask<StreamFrame> FindTimeAsync(FfprobeRun probe, StreamFrame first,
        VideoFrameReadRequest start, CancellationToken token) {
        StreamFrame frame = first;
        VideoPresentationTime requested = new(start.PositionMilliseconds, 1000);
        while (frame.Until is { } next && (start.AtEnd || next.CompareTo(requested) <= 0)) {
            frame = await NextFrameAsync(probe, frame, token).ConfigureAwait(false);
        }
        return frame;
    }

    // определяем номер перед первым шагом назад без сохранения общей таблицы кадров
    private async Task<long> FindBackwardTargetAsync(VideoFrameReadRequest start, CancellationToken token) {
        await using FfprobeRun locate = new(ffprobe, path, "V:0", "frame=pts", token);
        StreamFrame first = await ReadFirstFrameAsync(locate, token).ConfigureAwait(false);
        StreamFrame anchor = await FindTimeAsync(locate, first, start, token).ConfigureAwait(false);
        return Math.Max(0, anchor.Ordinal + start.FrameOffset);
    }

    // подтверждаем опору обычной перемотки по метке, номеру и пикселям при неоднозначном индексе
    private async Task<long> CheckDecodedAnchorAsync(VideoDecodedFrameHint hint, CancellationToken token) {
        await using FfprobeRun locate = new(ffprobe, path, "V:0", "frame=pts", token);
        StreamFrame frame = await ReadFirstFrameAsync(locate, token).ConfigureAwait(false);
        while (frame.Time.Milliseconds < hint.Milliseconds - TimestampToleranceMs && frame.Until is not null) {
            frame = await NextFrameAsync(locate, frame, token).ConfigureAwait(false);
        }
        if (Math.Abs(frame.Time.Milliseconds - hint.Milliseconds) > TimestampToleranceMs) {
            throw new InvalidDataException("Не удалось подтвердить метку кадра после обычной перемотки");
        }

        bool unique = frame.Until != frame.Time;
        if (frame.Ordinal == hint.Ordinal && (unique || hint.FromStart && frame.Ordinal == 0)) {
            return frame.Ordinal;
        }
        if (hint.Fingerprint is null) {
            throw new InvalidDataException("Не удалось подтвердить номер кадра после обычной перемотки");
        }
        return await MatchDecodedFingerprintAsync(locate, frame, hint, token).ConfigureAwait(false);
    }

    // сравниваем кадры одной метки по порядку и храним только номер совпадения
    private async Task<long> MatchDecodedFingerprintAsync(FfprobeRun probe, StreamFrame frame,
        VideoDecodedFrameHint hint, CancellationToken token) {
        using Mat raw = new();
        using Mat reduced = new();
        using VideoCapture capture = StartCapture(raw, frame.Ordinal, frame.Time, allowIndex: false, token);
        long? onlyMatch = null;
        while (true) {
            token.ThrowIfCancellationRequested();
            Mat basis = PreviewBgr(raw, reduced);
            if (VideoFirstFrameReader.GetBgrFingerprint(basis) == hint.Fingerprint) {
                if (onlyMatch.HasValue) {
                    throw new InvalidDataException("У нескольких кадров совпадают время и пиксели. Номер показанного кадра определить нельзя");
                }
                onlyMatch = frame.Ordinal;
            }
            if (frame.Until != frame.Time) {
                return onlyMatch ?? throw new InvalidDataException("Не удалось подтвердить пиксели кадра после обычной перемотки");
            }
            frame = await NextFrameAsync(probe, frame, token).ConfigureAwait(false);
            ReadMatchedFrame(capture, raw, frame.Ordinal, frame.Time, token);
        }
    }

    // пропускаем метки до начала участка, сверяем кадр и его порядок перед выдачей в координатор
    public async IAsyncEnumerable<TimedVideoInput> ReadFramesAsync(VideoFrameReadRequest start,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        CancellationToken token = cancellationToken;
        token.ThrowIfCancellationRequested();
        identity.EnsureCurrent(path);
        long? anchor = start.Anchor?.Ordinal;
        if (anchor is null && start.DecodedAnchor is { } decoded) {
            anchor = await CheckDecodedAnchorAsync(decoded, token).ConfigureAwait(false);
        }
        long? target = anchor is { } number ? Math.Max(0, number + start.FrameOffset) :
            start.FrameOffset < 0 ? await FindBackwardTargetAsync(start, token).ConfigureAwait(false) : null;
        identity.EnsureCurrent(path);
        await using FfprobeRun probe = new(ffprobe, path, "V:0", "frame=pts", token);
        StreamFrame first = await ReadFirstFrameAsync(probe, token).ConfigureAwait(false);
        StreamFrame selected = target is null
            ? await FindTimeAsync(probe, first, start, token).ConfigureAwait(false)
            : first;
        long ordinalTarget = target ?? selected.Ordinal + start.FrameOffset;
        while (selected.Ordinal < ordinalTarget && selected.Until is not null) {
            selected = await NextFrameAsync(probe, selected, token).ConfigureAwait(false);
        }
        if (start is { FrameOffset: 0, Anchor: { } confirmed } &&
            (selected.Ordinal != confirmed.Ordinal || selected.Time.CompareTo(confirmed.Time) != 0)) {
            throw new InvalidDataException("Положение выбранного кадра изменилось");
        }

        using Mat raw = new();
        using Mat reduced = new();
        using Mat bgra = new();
        using VideoCapture capture = StartCapture(raw, selected.Ordinal, selected.Time, allowIndex: !selected.Repeated, token);
        while (true) {
            token.ThrowIfCancellationRequested();
            if (selected.Until is null) {
                // EOF должен быть согласован и по меткам, и по кадрам
                if (capture.Grab()) {
                    throw new InvalidDataException("После последней подтвержденной метки есть еще кадры");
                }
            }
            Cv2.CvtColor(PreviewBgr(raw, reduced), bgra, ColorConversionCodes.BGR2BGRA);
            token.ThrowIfCancellationRequested();
            identity.EnsureCurrent(path);
            yield return new(selected.Time, selected.Until, selected.Ordinal,
                VideoEditorFrame.CopyBgra(bgra.Data, checked((int)bgra.Step()), FrameSize));

            if (selected.Until is null || start.SingleFrame) {
                yield break;
            }
            selected = await NextFrameAsync(probe, selected, token).ConfigureAwait(false);
            ReadMatchedFrame(capture, raw, selected.Ordinal, selected.Time, token);
        }
    }

    // одинаково приводим цветовой кадр к размеру показа для вывода и сверки
    private Mat PreviewBgr(Mat raw, Mat reduced) {
        if (FrameSize == sourceSize) {
            return raw;
        }
        Cv2.Resize(raw, reduced, new OpenCvSharp.Size(FrameSize.Width, FrameSize.Height),
            interpolation: InterpolationFlags.Area);
        return reduced;
    }

    // пробуем найти уникальную метку по индексу, после расхождения читаем по порядку
    private VideoCapture StartCapture(Mat raw, long ordinal, VideoPresentationTime time, bool allowIndex,
        CancellationToken token) {
        VideoCapture capture = OpenCapture();
        try {
            if (allowIndex && ordinal > 0) {
                if (capture.Set(VideoCaptureProperties.PosFrames, ordinal) && Matches(capture, raw, ordinal, time, token)) {
                    return capture;
                }
                capture.Dispose();
                capture = OpenCapture();
            }

            for (long i = 0; i < ordinal; i++) {
                token.ThrowIfCancellationRequested();
                if (!capture.Grab()) {
                    throw new InvalidDataException("OpenCV не дошел до запрошенного кадра");
                }
            }
            ReadMatchedFrame(capture, raw, ordinal, time, token);
            return capture;
        }
        catch {
            capture.Dispose();
            throw;
        }
    }

    // открываем тот же файл с настроенным пределом потоков FFmpeg
    private VideoCapture OpenCapture() {
        identity.EnsureCurrent(path);
        VideoCapture capture = new(path, VideoCaptureAPIs.FFMPEG, [DecodeThreadCountProperty, decodeThreads]);
        try {
            if (!capture.IsOpened()) {
                throw new InvalidDataException("OpenCV не смог открыть видеодорожку");
            }
            identity.EnsureCurrent(path);
            return capture;
        }
        catch {
            capture.Dispose();
            throw;
        }
    }

    // прерываем выдачу, если кадр OpenCV не совпал с меткой в исходной шкале
    private void ReadMatchedFrame(VideoCapture capture, Mat raw, long ordinal, VideoPresentationTime time,
        CancellationToken token) {
        if (!Matches(capture, raw, ordinal, time, token)) {
            throw new InvalidDataException("Не удалось подтвердить порядок или время кадра OpenCV");
        }
    }

    // сверяем время и порядковый номер, не получая метку из оценки FPS
    private bool Matches(VideoCapture capture, Mat raw, long ordinal, VideoPresentationTime time, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        return capture.Read(raw) && !raw.Empty() && raw.Width == sourceSize.Width && raw.Height == sourceSize.Height &&
            capture.Get(VideoCaptureProperties.PosFrames) == ordinal + 1 &&
            Math.Abs(capture.Get(VideoCaptureProperties.PosMsec) - time.Milliseconds) <= TimestampToleranceMs;
    }

    // правая граница берется из следующей метки, включая равное время у соседних кадров
    private async ValueTask<VideoPresentationTime?> ReadBoundaryAsync(
        FfprobeRun probe, VideoPresentationTime current, CancellationToken token) {
        VideoPresentationTime? next = await ReadNextTimeAsync(probe, token).ConfigureAwait(false);
        if (next is { } boundary && boundary.CompareTo(current) < 0) {
            throw new InvalidDataException("Метки видеокадров идут не в порядке показа");
        }
        return next;
    }

    // читаем только точный контейнерный PTS, пустую строку или дополнительные данные пропускаем
    private async ValueTask<VideoPresentationTime?> ReadNextTimeAsync(FfprobeRun probe, CancellationToken token) {
        while (await probe.ReadLineAsync(token).ConfigureAwait(false) is { } line) {
            if (!line.StartsWith("frame|", StringComparison.Ordinal) && line != "frame") {
                continue;
            }
            if (!long.TryParse(GetValue(line.Split('|'), "pts"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long value) || value < 0) {
                throw new InvalidDataException("В видеопотоке есть кадр без подходящего исходного PTS");
            }
            return new(checked(value * timeUnit), timescale);
        }
        await probe.CheckExitAsync(token).ConfigureAwait(false);
        return null;
    }

    // используем установленную утилиту в согласованном порядке, без поиска по личным путям
    private static string FindFfprobe() {
        string? configured = Environment.GetEnvironmentVariable("IMAGEVISIONAPP_FFPROBE");
        if (!string.IsNullOrWhiteSpace(configured)) {
            return IsExecutable(configured) ? Path.GetFullPath(configured) :
                throw new InvalidDataException("Указанный в IMAGEVISIONAPP_FFPROBE ffprobe отсутствует или не исполняется");
        }

        string name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        string adjacent = Path.Combine(AppContext.BaseDirectory, name);
        if (IsExecutable(adjacent)) {
            return adjacent;
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (Path.IsPathFullyQualified(directory) && IsExecutable(Path.Combine(directory, name))) {
                return Path.Combine(directory, name);
            }
        }

        const string losslessCut = "/Applications/LosslessCut.app/Contents/Resources/ffprobe";
        return OperatingSystem.IsMacOS() && IsExecutable(losslessCut) ? losslessCut :
            throw new InvalidDataException("Не найден ffprobe для проверки меток кадров");
    }

    // пропускаем существующие файлы, которые ОС не может запустить как утилиту
    private static bool IsExecutable(string file) {
        if (!File.Exists(file)) {
            return false;
        }
        if (OperatingSystem.IsWindows()) {
            return true;
        }
        try {
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(file) & execute) != 0;
        }
        catch (IOException) {
            return false;
        }
        catch (UnauthorizedAccessException) {
            return false;
        }
    }

    // извлекаем значение поля из компактной строки ffprobe
    private static string? GetValue(string[] fields, string name) {
        string prefix = name + "=";
        foreach (string field in fields) {
            if (field.StartsWith(prefix, StringComparison.Ordinal)) {
                return field[prefix.Length..];
            }
        }
        return null;
    }

    // размеры и коэффициенты времени должны быть положительными
    private static bool TryPositiveInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private sealed class FfprobeRun : IAsyncDisposable {
        private readonly Process process;
        private readonly Task drainErrors;
        private readonly CancellationTokenRegistration stop;

        // передаем аргументы без оболочки и отводим stderr отдельно, чтобы не заполнить канал
        public FfprobeRun(string executable, string path, string stream, string entries, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            ProcessStartInfo arguments = new(executable) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-v", "error", "-threads", "1", "-protocol_whitelist", "file",
                "-select_streams", stream, "-show_entries", entries, "-of", "compact=p=1:nk=0", "-i", path }) {
                arguments.ArgumentList.Add(argument);
            }
            try {
                process = Process.Start(arguments) ?? throw new IOException("Не удалось запустить ffprobe");
            }
            catch (Win32Exception error) {
                throw new IOException("Не удалось запустить ffprobe", error);
            }
            drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            stop = token.UnsafeRegister(static state => ((FfprobeRun)state!).Stop(), this);
        }

        // получаем по одной строке, пока следующий вызов не нужен потребителю
        public ValueTask<string?> ReadLineAsync(CancellationToken token) => process.StandardOutput.ReadLineAsync(token);

        // считаем конец подтвержденным только после штатного выхода программы
        public async Task CheckExitAsync(CancellationToken token) {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await drainErrors.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) {
                throw new InvalidDataException($"ffprobe не прочитал видеопоток (код {process.ExitCode})");
            }
        }

        // останавливаем чтение меток сразу при отмене, даже если потребитель временно не запрашивает следующий кадр
        private void Stop() {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited) {
                // процесс успел закончить сам
            }
            catch (Win32Exception) when (process.HasExited) {
                // ОС завершила процесс между проверкой и командой
            }
        }

        // закрываем подпроцесс и при раннем выходе из перечисления
        public async ValueTask DisposeAsync() {
            try {
                await stop.DisposeAsync().ConfigureAwait(false);
                Stop();
                await process.WaitForExitAsync().ConfigureAwait(false);
                await drainErrors.ConfigureAwait(false);
            }
            finally {
                process.Dispose();
            }
        }
    }
}
