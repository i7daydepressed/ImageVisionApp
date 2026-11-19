namespace ImageVisionApp.Models;

public sealed record VideoInfo {
    internal const string UnknownValue = "Не определено";

    public long? FileSizeBytes { get; init; }
    public long? DurationMilliseconds { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    public double? FramesPerSecond { get; init; }
    public bool? HasAudio { get; init; }
    public string? Codec { get; init; }
    public uint? VideoBitrate { get; init; }
    public string? Orientation { get; init; }

    public string FormattedFileSize => FileSizeBytes is { } size ? $"{size:N0} байт" : UnknownValue;
    public string FormattedDuration => DurationMilliseconds is { } duration ? FormatDuration(duration) : UnknownValue;
    public string Resolution => Width is { } width && Height is { } height ? $"{width} × {height} пикселей" : UnknownValue;
    public string FrameRateDescription => FramesPerSecond is { } rate ? $"{rate:0.###} кадр/с" : UnknownValue;
    public string AudioDescription => HasAudio switch { true => "Есть", false => "Нет", null => UnknownValue };
    public string CodecDescription => Codec ?? UnknownValue;
    public string BitrateDescription => VideoBitrate is { } bitrate ? $"{bitrate:N0} бит/с" : UnknownValue;
    public string OrientationDescription => Orientation ?? UnknownValue;

    // разбираем длительность в миллисекундах на часы, минуты, секунды и остаток
    private static string FormatDuration(long milliseconds) {
        long seconds = milliseconds / 1000;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}.{milliseconds % 1000:000}";
    }
}
