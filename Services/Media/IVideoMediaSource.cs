using System.Collections.Generic;
using System.Threading;
using Avalonia;

namespace ImageVisionApp.Services;

internal readonly record struct VideoPresentationTime(long Value, int Timescale) {
    public bool IsKnown => Timescale > 0;
    public double Milliseconds => 1000d * Value / Timescale;

    // сравниваем метки из разных шкал без округления времени кадра
    public int CompareTo(VideoPresentationTime other) =>
        ((System.Int128)Value * other.Timescale).CompareTo((System.Int128)other.Value * Timescale);
}

internal readonly record struct VideoFrameCursor(VideoPresentationTime Time, long Ordinal, bool AtEnd);
internal readonly record struct VideoDecodedFrameHint(double Milliseconds, long Ordinal,
    bool FromStart = false, string? Fingerprint = null);

internal readonly record struct VideoFrameReadRequest(long PositionMilliseconds, bool AtEnd = false) {
    public VideoFrameCursor? Anchor { get; init; }
    public VideoDecodedFrameHint? DecodedAnchor { get; init; }
    public int FrameOffset { get; init; }
    public bool SingleFrame { get; init; }
}

internal readonly record struct TimedVideoInput(
    VideoPresentationTime Time, VideoPresentationTime? Until, long Ordinal, VideoEditorFrame Pixels) {
    public bool IsLast => Until is null;
}

internal interface IVideoMediaSource {
    PixelSize FrameSize { get; }
    long ReadScratchBytes { get; }

    // передаем владение кадром с проверенной меткой общей шкалы, при равных метках сохраняем порядок показа
    // справа передаем следующую метку или подтвержденный конец, от позиции возвращаем действующий и следующие кадры
    IAsyncEnumerable<TimedVideoInput> ReadFramesAsync(VideoFrameReadRequest start, CancellationToken cancellationToken);
}
