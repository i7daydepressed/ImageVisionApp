using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace ImageVisionApp.Models;

internal sealed record ImageComparisonItem(Guid Id, string FileName, Bitmap Bitmap, ImageComparisonFeatures Features);

// набор владеет принятыми изображениями в течение сеанса окна
internal sealed class ImageComparisonSet : IDisposable {
    public const int Capacity = 9;

    private readonly List<ImageComparisonItem> items = [];
    private bool disposed;

    public IReadOnlyList<ImageComparisonItem> Items { get; }
    public long Revision { get; private set; }

    // даем интерфейсу и будущему поиску пары только представление текущих записей
    public ImageComparisonSet() => Items = items.AsReadOnly();

    // передаем новому слоту отдельный идентификатор и владение загруженным изображением
    public bool TryAdd(string fileName, Bitmap bitmap, ImageComparisonFeatures features) {
        if (disposed || items.Count >= Capacity || Owns(bitmap)) {
            return false;
        }
        items.Add(new(Guid.NewGuid(), fileName, bitmap, features));
        Revision++;
        return true;
    }

    // заменяем только ту версию слота, которая была выбрана перед открытием файла
    public bool TryReplace(ImageComparisonItem expected, string fileName, Bitmap bitmap,
        ImageComparisonFeatures features, out ImageComparisonItem? removed) {
        removed = null;
        int index = Find(expected);
        if (index < 0 || Owns(bitmap)) {
            return false;
        }
        items[index] = new(expected.Id, fileName, bitmap, features);
        removed = expected;
        Revision++;
        return true;
    }

    // передаем снятую запись интерфейсу для освобождения после обновления ее карточки
    public bool TryRemove(ImageComparisonItem expected, out ImageComparisonItem? removed) {
        removed = null;
        int index = Find(expected);
        if (index < 0) {
            return false;
        }
        items.RemoveAt(index);
        removed = expected;
        Revision++;
        return true;
    }

    // проверяем принадлежность записи перед крупным показом
    public bool Contains(ImageComparisonItem item) => Find(item) >= 0;

    // освобождаем оставшиеся картинки при закрытии окна
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        foreach (ImageComparisonItem item in items) {
            item.Bitmap.Dispose();
        }
        items.Clear();
    }

    // прежний объект записи не заменит свежую версию с тем же идентификатором
    private int Find(ImageComparisonItem expected) => items.FindIndex(item => ReferenceEquals(item, expected));

    // один экземпляр bitmap должен принадлежать только одному слоту
    private bool Owns(Bitmap bitmap) => items.Exists(item => ReferenceEquals(item.Bitmap, bitmap));
}
