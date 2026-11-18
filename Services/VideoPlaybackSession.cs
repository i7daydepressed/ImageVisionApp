using System;
using Avalonia;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace ImageVisionApp.Services;

// владеет одним источником, общей шкалой времени и системным выводом звука
internal sealed class VideoPlaybackSession : IDisposable {
    private readonly LibVLC engine;
    private readonly Media source;
    private readonly MediaPlayer player;
    private readonly VideoPlaybackFrames frames;

    public VLCState State => player.State;
    public bool CanSeek => player.IsSeekable;
    public long Position => player.Time;
    public long Duration => player.Length;

    // загружает библиотеку из сборки и соединяет декодер с предпросмотром
    public VideoPlaybackSession(string sourcePath, PixelSize visibleSize) {
        frames = new VideoPlaybackFrames(visibleSize);
        Core.Initialize();
        engine = new LibVLC("--ignore-config", "--no-video-title-show", "--no-sub-autodetect-file", "--avcodec-hw=none");
        try {
            source = new Media(engine, sourcePath, FromType.FromPath);
            try {
                source.AddOption(":start-paused");
                player = new MediaPlayer(engine);
                try {
                    player.SetVideoFormatCallbacks(frames.Configure, frames.Release);
                    player.SetVideoCallbacks(frames.Lock, null, frames.Display);
                    player.Media = source;
                }
                catch {
                    player.Dispose();
                    throw;
                }
            }
            catch {
                source.Dispose();
                throw;
            }
        }
        catch {
            engine.Dispose();
            throw;
        }
    }

    // открывает источник с паузой, сохраняя явную кнопку запуска
    public bool Prepare() => player.Play();

    // передаёт плееру явное состояние, чтобы повторная команда не меняла его на противоположное
    public void SetPaused(bool paused) => player.SetPause(paused);

    // меняет позицию исходного ролика в миллисекундах
    public void Seek(long position) => player.Time = position;

    // повторно открывает тот же источник после окончания ролика
    public bool Restart() {
        player.Stop();
        return Prepare();
    }

    // переносит последний готовый кадр в переиспользуемое Avalonia-изображение
    public bool CopyFrame(ref WriteableBitmap? bitmap, ref long revision) => frames.CopyTo(ref bitmap, ref revision);

    // освобождает источник только после завершения нативных callback
    public void Dispose() {
        try {
            player.Stop();
        }
        finally {
            player.Dispose();
            source.Dispose();
            engine.Dispose();
            frames.Dispose();
        }
    }
}
