using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

internal sealed partial class FaceDatabaseView : UserControl {
    private const int VectorColumns = 4;
    private static readonly FontFamily VectorFont = new("Menlo, monospace");
    private readonly Window owner;
    private readonly ListBox profiles = new();
    private readonly ListBox photos = new();
    private readonly ListBox savedSamples = new();
    private readonly TextBlock vectorTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly Grid vectorValues = new() {
        ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8, RowSpacing = 8
    };
    private readonly ScrollViewer vectorScroll = new() {
        Focusable = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly TextBox name = new() { PlaceholderText = "Имя профиля", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button create = new() { Content = "Создать профиль" };
    private readonly Button add = new() { Content = "Добавить фото…" };
    private readonly Button rename = new() { Content = "Переименовать" };
    private readonly Button delete = new() { Content = "Удалить профиль" };
    private readonly Button save = new() { Content = "Сохранить" };
    private readonly Button cancel = new() { Content = "Отменить черновик" };
    private readonly Button removePhoto = new() { Content = "Убрать фото" };
    private readonly ComboBox faces = new() { MinWidth = 100 };
    private readonly TextBlock profileTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl selectedProfileAvatar = new();
    private readonly TextBlock savedCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock toSave = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock catalogueStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock photoHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly FaceImportPreview preview = new() { ClipToBounds = true };
    private Grid? importControls;
    private TabControl? savedSampleControls;

    private PageSession? page;
    private Task pendingWrite = Task.CompletedTask;
    private FaceProfileDraft? draft;
    private FaceProfile? sampleProfile;
    private bool changingSelection;
    private bool leaveDialogOpen;
    private ProfileRow? SelectedProfileRow => (profiles.SelectedItem as ListBoxItem)?.Tag as ProfileRow;
    private FaceProfile? SelectedProfile => SelectedProfileRow?.Profile;
    private FaceImportItem? SelectedPhoto => (photos.SelectedItem as ListBoxItem)?.Tag as FaceImportItem;
    private bool HasDraftChanges => page?.IsImporting == true || draft?.Photos.Count > 0 ||
        (draft is { ProfileId: null } && !string.IsNullOrWhiteSpace(name.Text)) ||
        (draft is null && SelectedProfile is { } profile && name.Text != profile.Name);
    private bool LeaveBlocked => page is { IsSaving: true } or { IsWriting: true };
    public bool NeedsLeaveDecision => LeaveBlocked || HasDraftChanges;

    // собираем независимые контролы лица, данные загружаются только при входе
    public FaceDatabaseView(Window owner) {
        this.owner = owner;
        AutomationProperties.SetName(name, "Имя профиля");
        AutomationProperties.SetName(profiles, "Профили");
        AutomationProperties.SetName(photos, "Файлы импорта");
        AutomationProperties.SetName(savedSamples, "Образцы");
        AutomationProperties.SetName(vectorScroll, "Компоненты вектора образца");
        AutomationProperties.SetName(faces, "Номер лица");
        AutomationProperties.SetName(preview, "Предпросмотр фото с рамками");
        ScrollViewer.SetHorizontalScrollBarVisibility(profiles, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(photos, ScrollBarVisibility.Disabled);

        Content = new TabControl {
            Items = {
                new TabItem { Header = "База лиц", FontSize = 16, Content = CreateDatabaseLayout() },
                new TabItem { Header = "Распознавание", FontSize = 16, IsEnabled = false }
            },
            SelectedIndex = 0
        };
        profiles.SelectionChanged += (_, _) => {
            if (!changingSelection && draft is null) {
                ShowSelectedProfile();
            }
        };
        savedSamples.SelectionChanged += (_, _) => SelectSample(savedSamples.SelectedIndex);
        comparisonAnchor.SelectionChanged += (_, _) => SelectSample(comparisonAnchor.SelectedIndex);
        photos.SelectionChanged += (_, _) => {
            if (!changingSelection) {
                ShowSelectedPhoto();
            }
        };
        faces.SelectionChanged += (_, _) => {
            if (!changingSelection && faces.SelectedIndex >= 0) {
                ChooseFace(faces.SelectedIndex);
            }
        };
        preview.FaceChosen += ChooseFace;
        name.TextChanged += (_, _) => UpdateControls();
        create.Click += (_, _) => StartDraft(null);
        add.Click += async (_, _) => await PickPhotosAsync();
        removePhoto.Click += (_, _) => RemoveSelectedPhoto();
        save.Click += async (_, _) => await SaveDraftAsync();
        cancel.Click += (_, _) => CancelDraft();
        rename.Click += async (_, _) => await RenameAsync();
        delete.Click += async (_, _) => await DeleteAsync();
        ShowSelectedProfile();
    }

    // перечитываем базу при входе, дождавшись начатой записи с прошлого посещения
    public void Activate() {
        if (page is not null) {
            return;
        }
        page = new PageSession();
        catalogueStatus.Text = "Читаем базу лиц…";
        status.Text = "";
        _ = RunWorkAsync("Не удалось открыть базу лиц.", async (active, token) => {
            try {
                await pendingWrite;
            }
            catch {
                // предыдущая запись уже обработала ошибку, перечитываем сохраненное состояние
            }
            token.ThrowIfCancellationRequested();
            active.Paths = FaceWorkspacePaths.Resolve(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable(FaceWorkspacePaths.RootVariable));
            active.Store = new FaceProfileStore(active.Paths.CheckoutRoot);
            await ReloadProfilesAsync(active, null, token);
        });
    }

    // отсекаем результаты прежней страницы, модель освобождается после ее последней работы
    public void Deactivate() {
        PageSession? leaving = page;
        if (leaving is null) {
            return;
        }
        page = null;
        if (leaving.Work is { } work) {
            work.Cancel();
        }
        else {
            leaving.Pipeline?.Dispose();
        }
        draft = null;
        RefreshPhotos();
        profiles.Items.Clear();
        ShowSelectedProfile();
    }

    // сохраняем черновик при отказе и блокируем уход до результата начатой записи
    public async Task<bool> ConfirmLeaveAsync() {
        if (LeaveBlocked) {
            status.Text = page?.IsWriting == true
                ? "Дождитесь завершения записи базы лиц."
                : "Дождитесь завершения сохранения или отмените черновик.";
            UpdateControls();
            return false;
        }
        if (leaveDialogOpen) {
            return false;
        }
        if (!HasDraftChanges) {
            return true;
        }

        leaveDialogOpen = true;
        try {
            bool discard = await ConfirmActionAsync("Сбросить черновик?",
                "Несохраненное имя и выбранные фото будут сброшены.", "Остаться", "Сбросить черновик");
            return discard && !LeaveBlocked;
        }
        finally {
            leaveDialogOpen = false;
        }
    }

    // удерживаем по одной фоновой операции на посещение, ошибки показываем в той же странице
    private async Task RunWorkAsync(string errorPrefix, Func<PageSession, CancellationToken, Task> operation) {
        if (page is not { Work: null } active) {
            return;
        }

        using CancellationTokenSource stop = new();
        active.Work = stop;
        UpdateControls();
        try {
            await operation(active, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
            // отмененный запрос не публикует результат
        }
        catch (Exception error) {
            if (IsCurrent(active, stop.Token)) {
                status.Text = $"{errorPrefix} {error.Message}";
                if (!active.CatalogueLoaded) {
                    catalogueStatus.Text = "База лиц недоступна.";
                }
            }
        }
        finally {
            active.IsImporting = false;
            active.IsSaving = false;
            active.Work = null;
            if (ReferenceEquals(page, active)) {
                UpdateControls();
            }
            else {
                active.Pipeline?.Dispose();
            }
        }
    }

    // проверяем посещение и отмену перед публикацией в контролы
    private bool IsCurrent(PageSession active, CancellationToken token) =>
        ReferenceEquals(page, active) && !token.IsCancellationRequested;

    // обновляем список только после полного чтения, выбираем сохраненную запись по ID
    private async Task ReloadProfilesAsync(PageSession active, Guid? selected, CancellationToken token) {
        (IReadOnlyList<FaceProfile> snapshot, FaceProfileAvatarPattern?[] avatars) = await Task.Run(() => {
            IReadOnlyList<FaceProfile> loaded = active.Store!.Load();
            return (loaded, FaceProfileAvatars.BuildCatalogue(loaded, token));
        }, token);
        if (!IsCurrent(active, token)) {
            return;
        }
        active.CatalogueLoaded = true;
        changingSelection = true;
        try {
            profiles.Items.Clear();
            for (int i = 0; i < snapshot.Count; i++) {
                FaceProfile profile = snapshot[i];
                ListBoxItem row = CreateProfileRow(profile, avatars[i]);
                profiles.Items.Add(row);
                if (profile.Id == selected) {
                    profiles.SelectedItem = row;
                }
            }
            if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) {
                profiles.SelectedIndex = 0;
            }
        }
        finally {
            changingSelection = false;
        }
        catalogueStatus.Text = snapshot.Count == 0 ? "База лиц пуста." : "";
        ShowSelectedProfile();
    }

    // начинаем создание или дополнение выбранного профиля без записи в базу
    private void StartDraft(FaceProfile? target) {
        if (page is not { Work: null, CatalogueLoaded: true } || draft is not null) {
            return;
        }
        draft = new FaceProfileDraft(target?.Id);
        status.Text = "";
        name.Text = target?.Name ?? "";
        RefreshPhotos();
        if (target is null) {
            name.Focus();
        }
    }

    // открываем отдельный диалог и привязываем результаты к точному черновику
    private Task PickPhotosAsync() {
        FaceProfileDraft? existingDraft = draft;
        FaceProfile? targetProfile = SelectedProfile;
        if (existingDraft is null && targetProfile is null) {
            return Task.CompletedTask;
        }
        return RunWorkAsync("Не удалось добавить фото.", async (active, token) => {
            active.IsImporting = true;
            IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Добавить фото",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Изображения") {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.tif", "*.tiff"]
                }]
            });
            try {
                if (!IsCurrent(active, token) || !ReferenceEquals(draft, existingDraft) || files.Count == 0) {
                    return;
                }
                FaceProfileDraft target = existingDraft ?? new FaceProfileDraft(targetProfile!.Id);
                if (existingDraft is null) {
                    draft = target;
                    name.Text = targetProfile!.Name;
                    RefreshPhotos();
                }
                status.Text = "Проверяем фото…";
                UpdateControls();
                FacePipelineService pipeline = await Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    return active.Pipeline ??= active.Paths!.CreatePipeline();
                }, token);
                token.ThrowIfCancellationRequested();

                FaceImportItem? first = null;
                foreach (IStorageFile file in files) {
                    FaceImportItem item = await ReadPhotoAsync(file, pipeline, token);
                    if (!IsCurrent(active, token) || !ReferenceEquals(draft, target)) {
                        return;
                    }
                    target.Photos.Add(item);
                    first ??= item;
                    RefreshPhotos(SelectedPhoto ?? first);
                }
                status.Text = "";
            }
            finally {
                foreach (IStorageFile file in files) {
                    file.Dispose();
                }
            }
        });
    }

    // один файл дает ориентированный предпросмотр и результат детекции либо причину отказа
    private static async Task<FaceImportItem> ReadPhotoAsync(
        IStorageFile file, FacePipelineService pipeline, CancellationToken token) {

        string fileName = file.Name;
        try {
            token.ThrowIfCancellationRequested();
            await using Stream stream = await file.OpenReadAsync();
            return await Task.Run(() => {
                FaceImage photo = FacePhotoDecoder.Decode(stream, token);
                FaceDetectionResult detection = pipeline.Detect(photo, token);
                byte[] png = FaceImportPreview.EncodePreview(photo, token);
                return new FaceImportItem(fileName, detection, png);
            }, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested) {
            return new FaceImportItem(fileName, null, null, $"Не читается: {error.Message}");
        }
    }

    // рамка мышью и номер в списке обновляют один выбор того же фото
    private void ChooseFace(int index) {
        if (page is not { Work: null } || SelectedPhoto is not { } photo) {
            return;
        }
        photo.SelectFace(index);
        RefreshPhotos(photo);
    }

    // убираем только выделенный файл и пересчитываем число принятых образцов
    private void RemoveSelectedPhoto() {
        if (page is { Work: null } && SelectedPhoto is { } photo && draft is not null) {
            draft.Photos.Remove(photo);
            RefreshPhotos();
        }
    }

    // отменяем ожидание и черновик до подтвержденной записи
    private void CancelDraft() {
        if (draft is null || page is not { IsWriting: false } active) {
            return;
        }
        active.Work?.Cancel();
        draft = null;
        status.Text = "Черновик отменен.";
        RefreshPhotos();
        ShowSelectedProfile();
    }

    // считаем признаки принятых рамок и сохраняем всю пачку одной операцией
    private Task SaveDraftAsync() {
        if (draft is not { AcceptedCount: > 0, HasUnselectedFace: false } target) {
            return Task.CompletedTask;
        }
        string profileName = (name.Text ?? "").Trim();
        if (target.ProfileId is null && profileName.Length == 0) {
            return Task.CompletedTask;
        }
        FaceImportSelection[] chosen = target.CollectSelections();
        return RunWorkAsync("Не удалось сохранить профиль.", async (active, token) => {
            active.IsSaving = true;
            status.Text = "Рассчитываем образцы…";
            UpdateControls();
            FaceEmbedding[] embeddings = await Task.Run(() => chosen.Select(choice =>
                active.Pipeline!.ExtractEmbedding(choice.Detection, choice.Index, token)).ToArray(), token);
            if (!IsCurrent(active, token) || !ReferenceEquals(draft, target)) {
                return;
            }

            FaceProfile written = await CommitAsync(active, token, () => target.ProfileId is Guid id
                ? active.Store!.AddSamples(id, embeddings) : active.Store!.Create(profileName, embeddings));
            if (!IsCurrent(active, token)) {
                return;
            }
            draft = null;
            RefreshPhotos();
            status.Text = $"Записано образцов: {embeddings.Length}.";
            await ReloadAfterWriteAsync(active, written.Id, token);
        });
    }

    // сохраняем имя выбранного ID отдельно от добавления фото
    private Task RenameAsync() {
        if (SelectedProfile is not { } profile || draft is not null) {
            return Task.CompletedTask;
        }
        string newName = (name.Text ?? "").Trim();
        return RunWorkAsync("Не удалось переименовать профиль.", async (active, token) => {
            FaceProfile saved = await CommitAsync(active, token, () => active.Store!.Rename(profile.Id, newName));
            if (IsCurrent(active, token)) {
                status.Text = "Имя профиля сохранено.";
                await ReloadAfterWriteAsync(active, saved.Id, token);
            }
        });
    }

    // подтверждаем удаление показанного профиля и используем его ID
    private Task DeleteAsync() {
        if (SelectedProfile is not { } profile || draft is not null) {
            return Task.CompletedTask;
        }
        return RunWorkAsync("Не удалось удалить профиль.", async (active, token) => {
            if (!await ConfirmDeleteAsync(profile) || !IsCurrent(active, token)) {
                return;
            }
            await CommitAsync(active, token, () => active.Store!.Delete(profile.Id));
            if (IsCurrent(active, token)) {
                status.Text = "Профиль удален.";
                await ReloadAfterWriteAsync(active, null, token);
            }
        });
    }

    // после начала файловой транзакции ждем ее исход, при новом входе перечитываем базу
    private async Task<FaceProfile> CommitAsync(PageSession active, CancellationToken token, Func<FaceProfile> write) {
        active.IsWriting = true;
        status.Text = "Записываем базу лиц…";
        UpdateControls();
        Task<FaceProfile> commit = Task.Run(() => {
            token.ThrowIfCancellationRequested();
            return write();
        });
        pendingWrite = commit;
        try {
            return await commit;
        }
        finally {
            active.IsWriting = false;
        }
    }

    // отличаем записанное изменение от возможной ошибки повторного чтения
    private async Task ReloadAfterWriteAsync(PageSession active, Guid? selected, CancellationToken token) {
        try {
            await ReloadProfilesAsync(active, selected, token);
        }
        catch (Exception error) when (IsCurrent(active, token)) {
            active.CatalogueLoaded = false;
            profiles.Items.Clear();
            ShowSelectedProfile();
            catalogueStatus.Text = "База лиц недоступна.";
            status.Text = $"Изменение сохранено. Не удалось перечитать базу лиц. {error.Message}";
        }
    }

    // подтверждение относится к одному имени и числу сохраненных образцов
    private Task<bool> ConfirmDeleteAsync(FaceProfile profile) =>
        ConfirmActionAsync("Удалить профиль?", $"Удалить профиль «{profile.Name}» и его образцы ({profile.Samples.Count})?",
            "Отмена", "Удалить профиль");

    // выполняем действие только после выбора конкретной кнопки владельцем окна
    private Task<bool> ConfirmActionAsync(string title, string prompt, string cancelText, string actionText) {
        Window dialog = new() { Title = title, Width = 430, CanResize = false,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button keep = new() { Content = cancelText, IsCancel = true, IsDefault = true };
        Button apply = new() { Content = actionText };
        keep.Click += (_, _) => dialog.Close(false);
        apply.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = {
            new TextBlock { Text = prompt,
                TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { keep, apply } }
        }};
        return dialog.ShowDialog<bool>(owner);
    }

    // обновляем имя и сохраненные образцы из одного выбранного профиля
    private void ShowSelectedProfile() {
        ProfileRow? selected = SelectedProfileRow;
        selectedProfileAvatar.Content = selected is null ? null : new FaceProfileAvatar(selected.Avatar);
        name.Text = selected?.Profile.Name ?? "";
        ShowSavedSamples(selected?.Profile);
        UpdateControls();
    }

    // сохраняем номер при перечитывании того же ID, для другого профиля выбираем первый образец
    private void ShowSavedSamples(FaceProfile? profile) {
        if (ReferenceEquals(sampleProfile, profile)) {
            return;
        }
        int index = sampleProfile?.Id == profile?.Id ? savedSamples.SelectedIndex : 0;
        sampleProfile = profile;
        int count = profile?.Samples.Count ?? 0;
        string[] labels = Enumerable.Range(1, count).Select(number => $"Образец {number}").ToArray();
        int selectedIndex = count == 0 ? -1 : Math.Clamp(index, 0, count - 1);
        changingSelection = true;
        try {
            savedSamples.ItemsSource = labels;
            comparisonAnchor.ItemsSource = labels;
            savedSamples.SelectedIndex = selectedIndex;
            comparisonAnchor.SelectedIndex = selectedIndex;
        }
        finally {
            changingSelection = false;
        }
        ShowSelectedVector();
        ShowComparison();
    }

    // синхронизируем исходный образец между вкладками, пропуская ответное событие выбора
    private void SelectSample(int index) {
        if (changingSelection || index < 0) {
            return;
        }
        changingSelection = true;
        try {
            savedSamples.SelectedIndex = index;
            comparisonAnchor.SelectedIndex = index;
        }
        finally {
            changingSelection = false;
        }
        ShowSelectedVector();
        ShowComparison();
    }

    // показываем исходный порядок и все значащие цифры сохраненных float
    private void ShowSelectedVector() {
        vectorValues.Children.Clear();
        vectorValues.RowDefinitions.Clear();
        vectorScroll.Offset = default;
        int index = savedSamples.SelectedIndex;
        if (sampleProfile is not { } profile || (uint)index >= (uint)profile.Samples.Count) {
            vectorTitle.Text = "Вектор образца";
            return;
        }

        IReadOnlyList<float> values = profile.Samples[index];
        vectorTitle.Text = $"Вектор образца · {values.Count} компонент";
        for (int i = 0; i < values.Count; i++) {
            if (i % VectorColumns == 0) {
                vectorValues.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            StackPanel cell = new() { Children = {
                new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 12 },
                new TextBlock { Text = values[i].ToString("G9", CultureInfo.InvariantCulture), FontFamily = VectorFont, FontSize = 13 }
            }};
            Grid.SetRow(cell, i / VectorColumns);
            Grid.SetColumn(cell, i % VectorColumns);
            vectorValues.Children.Add(cell);
        }
    }

    // сохраняем выбранный файл при обновлении статусов и пересчитываем предпросмотр
    private void RefreshPhotos(FaceImportItem? selected = null) {
        changingSelection = true;
        try {
            photos.Items.Clear();
            if (draft is not null) {
                foreach (FaceImportItem photo in draft.Photos) {
                    ListBoxItem row = CreateRow(photo.FileName, photo.Status, photo);
                    photos.Items.Add(row);
                    if (ReferenceEquals(photo, selected)) {
                        photos.SelectedItem = row;
                    }
                }
            }
            if (photos.SelectedIndex < 0 && photos.Items.Count > 0) {
                photos.SelectedIndex = 0;
            }
        }
        finally {
            changingSelection = false;
        }
        ShowSelectedPhoto();
    }

    // показываем ориентированную копию и номера обнаружений выбранного файла
    private void ShowSelectedPhoto() {
        FaceImportItem? photo = SelectedPhoto;
        preview.ShowPhoto(photo);
        photoHint.Text = photo is null ? "Выберите фото для предпросмотра." : "Предпросмотр недоступен.";
        photoHint.IsVisible = photo?.PreviewPng is null;
        changingSelection = true;
        try {
            int count = photo?.Detection?.Faces.Count ?? 0;
            faces.ItemsSource = Enumerable.Range(1, count).Select(index => $"Лицо {index}").ToArray();
            faces.SelectedIndex = photo?.SelectedIndex ?? -1;
        }
        finally {
            changingSelection = false;
        }
        UpdateControls();
    }

    // отображаем только доступные команды текущего профиля или черновика
    private void UpdateControls() {
        bool ready = page?.CatalogueLoaded == true;
        bool idle = page is { Work: null };
        bool editing = draft is not null;
        FaceProfile? profile = SelectedProfile;
        bool selected = profile is not null && !editing;
        bool showEditor = ready && (editing || selected);
        bool createMode = editing && draft?.ProfileId is null;
        bool hasName = !string.IsNullOrWhiteSpace(name.Text);

        profileTitle.Text = editing ? (createMode ? "Новый профиль" : "Добавить образцы") :
            (selected ? "Профиль" : "Выберите или создайте профиль");
        selectedProfileAvatar.IsVisible = ready && selected;
        profiles.IsEnabled = ready && idle && !editing;
        create.IsEnabled = ready && idle && !editing;
        name.IsVisible = showEditor;
        name.IsEnabled = idle && (createMode || selected);
        savedCount.IsVisible = ready && profile is not null && !createMode;
        savedCount.Text = profile is null ? "" : $"Сохранено образцов: {profile.Samples.Count}";
        add.IsVisible = showEditor;
        add.IsEnabled = ready && idle;
        rename.IsVisible = showEditor && selected;
        rename.IsEnabled = idle && hasName && name.Text!.Trim() != profile?.Name;
        delete.IsVisible = rename.IsVisible;
        delete.IsEnabled = idle;
        save.IsVisible = editing;
        save.IsEnabled = ready && idle && (hasName || !createMode) &&
            draft is { AcceptedCount: > 0, HasUnselectedFace: false };
        cancel.IsVisible = editing;
        cancel.IsEnabled = page is { IsWriting: false };
        if (importControls is not null) {
            importControls.IsVisible = editing;
        }
        if (savedSampleControls is not null) {
            savedSampleControls.IsVisible = ready && selected && idle;
        }
        toSave.IsVisible = editing;
        toSave.Text = $"К сохранению: {draft?.AcceptedCount ?? 0}." +
            (draft?.HasUnselectedFace == true ? " Выберите лицо на групповом фото или уберите это фото." : "");
        photos.IsEnabled = idle;
        faces.IsEnabled = idle && SelectedPhoto?.Detection is { Faces.Count: > 1 };
        faces.IsVisible = SelectedPhoto?.Detection is { Faces.Count: > 0 };
        removePhoto.IsEnabled = idle && SelectedPhoto is not null;
        preview.IsEnabled = idle;
        status.IsVisible = !string.IsNullOrEmpty(status.Text);
        catalogueStatus.IsVisible = !string.IsNullOrEmpty(catalogueStatus.Text);
    }

    private sealed record ProfileRow(FaceProfile Profile, FaceProfileAvatarPattern? Avatar);

    private sealed class PageSession {
        public FaceWorkspacePaths? Paths { get; set; }
        public FaceProfileStore? Store { get; set; }
        public FacePipelineService? Pipeline { get; set; }
        public CancellationTokenSource? Work { get; set; }
        public bool CatalogueLoaded { get; set; }
        public bool IsWriting { get; set; }
        public bool IsImporting { get; set; }
        public bool IsSaving { get; set; }
    }
}
