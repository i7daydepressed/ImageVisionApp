using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace ImageVisionApp.Services;

internal static class VideoExportProbe {
    internal readonly record struct TimeBase(int Unit, int Scale) {
        public VideoPresentationTime ToTime(long ticks) => new(checked(ticks * Unit), Scale);
    }

    internal readonly record struct VideoEnd(TimeBase Clock, VideoPresentationTime Last, VideoPresentationTime End,
        long NominalFrameNanoseconds, long TotalFrames);
    private readonly record struct AudioTrack(string Codec, TimeBase Clock, string? ExtradataHash);

    // считаем декодированные кадры, а длительность последнего сверяем по пакетам той же дорожки
    public static async Task<VideoEnd> ReadVideoEndAsync(string ffprobe, string path, CancellationToken token) {
        await using var probe = new FfprobeVideoMediaSource.FfprobeRun(ffprobe, path, "V:0",
            "frame=pts:packet=pts,duration:stream=time_base,start_pts,duration_ts,avg_frame_rate", token);
        long last = -1;
        long lastDuration = 0;
        long lastDecoded = -1;
        long totalFrames = 0;
        long? streamDuration = null;
        long nominal = 33_333_333;
        TimeBase clock = default;
        while (await probe.ReadLineAsync(token).ConfigureAwait(false) is { } line) {
            string[] fields = line.Split('|');
            if (fields[0] == "packet" && Number(fields, "pts") is { } pts && pts >= last) {
                long duration = Number(fields, "duration") ?? 0;
                lastDuration = pts == last ? Math.Max(lastDuration, duration) : duration;
                last = pts;
            }
            else if (fields[0] == "frame") {
                if (Number(fields, "pts") is not { } framePts || framePts < lastDecoded ||
                    totalFrames == 0 && framePts != 0) {
                    throw new InvalidDataException("Не удалось подтвердить исходные метки декодированных кадров");
                }
                lastDecoded = framePts;
                totalFrames = checked(totalFrames + 1);
            }
            else if (fields[0] == "stream") {
                clock = ReadClock(fields);
                nominal = GetNominalFrameNanoseconds(fields, nominal);
                if (Number(fields, "start_pts") == 0) {
                    streamDuration = Number(fields, "duration_ts");
                }
            }
        }
        await probe.CheckExitAsync(token).ConfigureAwait(false);
        if (totalFrames == 0) {
            throw new InvalidDataException("В исходной видеодорожке нет декодированных кадров");
        }
        long end = lastDuration > 0 ? checked(last + lastDuration) : streamDuration ?? 0;
        if (clock.Scale <= 0 || last != lastDecoded || end <= last) {
            throw new InvalidDataException("Не удалось подтвердить длительность последнего кадра источника");
        }
        return new(clock, clock.ToTime(last), clock.ToTime(end), nominal, totalFrames);
    }

    // сравниваем число кадров, порядок pts и подтвержденную правую границу, не сохраняя таблицу кадров
    public static async Task VerifyVideoAsync(string ffprobe, string path, PixelSize size, int accuracyScale, bool mp4,
        long frameCount, byte[] expectedTimeline, long expectedEnd, CancellationToken token) {
        await using var probe = new FfprobeVideoMediaSource.FfprobeRun(ffprobe, path, "V:0",
            "frame=pts,duration:stream=width,height,time_base", token);
        using IncrementalHash timeline = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long frames = 0;
        long last = -1;
        long duration = 0;
        bool correctTrack = false;
        while (await probe.ReadLineAsync(token).ConfigureAwait(false) is { } line) {
            string[] fields = line.Split('|');
            if (fields[0] == "frame") {
                long pts = Number(fields, "pts") ?? throw new InvalidDataException("В результате у кадра нет исходной метки");
                AppendTime(timeline, pts);
                frames++;
                last = pts;
                duration = Number(fields, "duration") ?? 0;
            }
            else if (fields[0] == "stream") {
                TimeBase clock = ReadClock(fields);
                correctTrack = clock is { Unit: 1 } && clock.Scale == accuracyScale &&
                    Number(fields, "width") == size.Width && Number(fields, "height") == size.Height;
            }
        }
        await probe.CheckExitAsync(token).ConfigureAwait(false);
        long tailTolerance = mp4 ? 0 : 1;
        if (!correctTrack || frames != frameCount || duration <= 0 ||
            Math.Abs(checked(last + duration - expectedEnd)) > tailTolerance ||
            !timeline.GetHashAndReset().AsSpan().SequenceEqual(expectedTimeline)) {
            throw new InvalidDataException("Записанный файл не сохранил порядок, размер или временные метки кадров");
        }
    }

    // сравниваем сжатые аудиопакеты и их начало, учитывая точность шкалы контейнера
    public static async Task VerifyAudioAsync(string ffprobe, string source, string result, bool mp4, CancellationToken token) {
        List<AudioTrack> sourceTracks = await ReadAudioTracksAsync(ffprobe, source, token).ConfigureAwait(false);
        List<AudioTrack> resultTracks = await ReadAudioTracksAsync(ffprobe, result, token).ConfigureAwait(false);
        if (sourceTracks.Count != resultTracks.Count) {
            throw AudioError(mp4);
        }

        const string entries = "packet=pts,dts,duration,data_hash:packet_side_data=skip_samples,discard_padding";
        for (int index = 0; index < sourceTracks.Count; index++) {
            AudioTrack sourceTrack = sourceTracks[index];
            AudioTrack resultTrack = resultTracks[index];
            if (sourceTrack.Codec != resultTrack.Codec || sourceTrack.ExtradataHash != resultTrack.ExtradataHash) {
                throw AudioError(mp4);
            }

            string stream = $"a:{index.ToString(CultureInfo.InvariantCulture)}";
            await using var before = new FfprobeVideoMediaSource.FfprobeRun(ffprobe, source, stream, entries, token,
                includeDataHash: true);
            await using var after = new FfprobeVideoMediaSource.FfprobeRun(ffprobe, result, stream, entries, token,
                includeDataHash: true);
            while (true) {
                string? one = await before.ReadLineAsync(token).ConfigureAwait(false);
                string? two = await after.ReadLineAsync(token).ConfigureAwait(false);
                if (one is null && two is null) {
                    break;
                }
                if (one is null || two is null || !SameAudioPacket(one, sourceTrack.Clock, two, resultTrack.Clock)) {
                    throw AudioError(mp4);
                }
            }
            await before.CheckExitAsync(token).ConfigureAwait(false);
            await after.CheckExitAsync(token).ConfigureAwait(false);
        }
    }

    // сравниваем данные исходного пакета и возможный пропуск приминга или хвостового заполнения
    private static bool SameAudioPacket(string source, TimeBase sourceClock, string result, TimeBase resultClock) {
        string[] before = source.Split('|');
        string[] after = result.Split('|');
        if (before[0] != "packet" || after[0] != "packet" || Number(before, "pts") is null ||
            FfprobeVideoMediaSource.GetValue(before, "data_hash") is not { } sourceHash ||
            !string.Equals(sourceHash, FfprobeVideoMediaSource.GetValue(after, "data_hash"), StringComparison.OrdinalIgnoreCase) ||
            AudioSideData(before, "skip_samples") != AudioSideData(after, "skip_samples") ||
            AudioSideData(before, "discard_padding") != AudioSideData(after, "discard_padding")) {
            return false;
        }

        return SameAudioTime(Number(before, "pts"), sourceClock, Number(after, "pts"), resultClock) &&
            SameAudioTime(Number(before, "dts"), sourceClock, Number(after, "dts"), resultClock) &&
            SameAudioTime(Number(before, "duration"), sourceClock, Number(after, "duration"), resultClock);
    }

    // допускаем только округление до ближайшего тика шкалы выходной дорожки
    private static bool SameAudioTime(long? before, TimeBase sourceClock, long? after, TimeBase resultClock) {
        if (before is not { } source || after is not { } result) {
            return before is null && after is null;
        }
        VideoPresentationTime sourceTime = sourceClock.ToTime(source);
        VideoPresentationTime resultTime = resultClock.ToTime(result);
        Int128 difference = Int128.Abs((Int128)sourceTime.Value * resultTime.Timescale -
            (Int128)resultTime.Value * sourceTime.Timescale);
        return difference * 2 <= (Int128)sourceTime.Timescale * resultClock.Unit;
    }

    private static long AudioSideData(string[] fields, string name) =>
        Number(fields, $"side_datum/skip_samples:{name}") ?? Number(fields, name) ?? 0;

    // читаем только заголовки аудиодорожек, чтобы затем сверить их по порядку
    private static async Task<List<AudioTrack>> ReadAudioTracksAsync(string ffprobe, string path, CancellationToken token) {
        await using var probe = new FfprobeVideoMediaSource.FfprobeRun(ffprobe, path, "a",
            "stream=codec_name,time_base,extradata_hash", token, includeDataHash: true);
        List<AudioTrack> tracks = [];
        while (await probe.ReadLineAsync(token).ConfigureAwait(false) is { } line) {
            string[] fields = line.Split('|');
            string codec = FfprobeVideoMediaSource.GetValue(fields, "codec_name") ??
                throw new InvalidDataException("Не удалось определить формат исходной аудиодорожки");
            tracks.Add(new(codec, ReadClock(fields), FfprobeVideoMediaSource.GetValue(fields, "extradata_hash")));
        }
        await probe.CheckExitAsync(token).ConfigureAwait(false);
        return tracks;
    }

    private static TimeBase ReadClock(string[] fields) {
        string[]? rational = FfprobeVideoMediaSource.GetValue(fields, "time_base")?.Split('/');
        if (rational is not { Length: 2 } ||
            !int.TryParse(rational[0], NumberStyles.None, CultureInfo.InvariantCulture, out int unit) || unit <= 0 ||
            !int.TryParse(rational[1], NumberStyles.None, CultureInfo.InvariantCulture, out int scale) || scale <= 0) {
            throw new InvalidDataException("В файле не подтверждена временная шкала дорожки");
        }
        return new(unit, scale);
    }

    // средняя частота задает только номинал кодировщика, каждый кадр сохраняет свою метку и длительность
    private static long GetNominalFrameNanoseconds(string[] fields, long fallback) {
        string[]? rate = FfprobeVideoMediaSource.GetValue(fields, "avg_frame_rate")?.Split('/');
        if (rate is not { Length: 2 } ||
            !int.TryParse(rate[0], NumberStyles.None, CultureInfo.InvariantCulture, out int frames) || frames <= 0 ||
            !int.TryParse(rate[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0) {
            return fallback;
        }
        return Math.Max(1, (1_000_000_000L * seconds + frames / 2) / frames);
    }

    private static long? Number(string[] fields, string key) =>
        long.TryParse(FfprobeVideoMediaSource.GetValue(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out long number) ? number : null;

    internal static void AppendTime(IncrementalHash hash, long pts) {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, pts);
        hash.AppendData(bytes);
    }

    private static InvalidDataException AudioError(bool mp4) => new(mp4
        ? "MP4 не сохранил исходные аудиопакеты или их временную разметку. Попробуйте MKV."
        : "MKV не сохранил исходные аудиопакеты или их временную разметку. Попробуйте MP4.");
}
