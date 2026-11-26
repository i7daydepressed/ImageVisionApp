using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal sealed partial class FaceDatabaseView {
    // размещаем список профилей и рабочую область в двух колонках с независимыми списками
    private Grid CreateDatabaseLayout() {
        TextBlock title = new() { Text = "Профили", FontSize = 18, FontWeight = FontWeight.SemiBold };
        Grid sidebar = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        sidebar.Children.Add(title);
        Grid.SetRow(profiles, 1);
        sidebar.Children.Add(profiles);
        Grid.SetRow(catalogueStatus, 2);
        sidebar.Children.Add(catalogueStatus);
        Grid.SetRow(create, 3);
        sidebar.Children.Add(create);

        Grid content = new() { Margin = new Thickness(12), ColumnSpacing = 12,
            ColumnDefinitions = new ColumnDefinitions("235,*") };
        content.Children.Add(CreateCard(sidebar));
        Border editor = CreateCard(CreateProfileEditor());
        Grid.SetColumn(editor, 1);
        content.Children.Add(editor);
        return content;
    }

    // держим строку имени и команды рядом, свободную высоту отдаем фото и рамкам
    private Grid CreateProfileEditor() {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { selectedProfileAvatar, profileTitle } });
        Grid.SetColumn(savedCount, 1);
        header.Children.Add(savedCount);
        Grid nameRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        nameRow.Children.Add(name);
        Grid.SetColumn(rename, 1);
        nameRow.Children.Add(rename);
        Grid.SetColumn(delete, 2);
        nameRow.Children.Add(delete);

        importControls = CreateImportArea();
        savedSampleControls = new TabControl {
            Items = {
                new TabItem { Header = "Образцы", FontSize = 16, Content = CreateSavedSampleArea() },
                new TabItem { Header = "Сравнение образцов", FontSize = 16, Content = CreateComparisonArea() }
            },
            SelectedIndex = 0
        };
        savedSampleControls.SelectionChanged += (_, args) => {
            if (ReferenceEquals(args.Source, savedSampleControls)) {
                ShowComparison();
            }
        };
        StackPanel actions = new() { Spacing = 4, Children = {
            new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Children = { add, save, cancel } }, toSave
        }};
        Border feedback = new() { MaxHeight = 108, Child = new ScrollViewer {
            Content = status,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        }};
        Grid editor = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 8 };
        editor.Children.Add(header);
        Grid.SetRow(nameRow, 1);
        editor.Children.Add(nameRow);
        Grid.SetRow(importControls, 2);
        editor.Children.Add(importControls);
        Grid.SetRow(savedSampleControls, 2);
        editor.Children.Add(savedSampleControls);
        Grid.SetRow(actions, 3);
        editor.Children.Add(actions);
        Grid.SetRow(feedback, 4);
        editor.Children.Add(feedback);
        return editor;
    }

    // отдаем двум областям собственную прокрутку и оставляем четыре колонки чисел
    private Grid CreateSavedSampleArea() {
        TextBlock sampleTitle = new() { Text = "Образцы", FontSize = 16, FontWeight = FontWeight.SemiBold };
        ScrollViewer.SetHorizontalScrollBarVisibility(savedSamples, ScrollBarVisibility.Disabled);
        vectorScroll.Content = vectorValues;

        Grid area = new() { ColumnDefinitions = new ColumnDefinitions("145,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 6, ColumnSpacing = 12 };
        area.Children.Add(sampleTitle);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { renameSavedSample, removeSavedSample } };
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 2);
        area.Children.Add(actions);
        Grid.SetRow(savedSamples, 2);
        area.Children.Add(savedSamples);
        Grid.SetColumn(vectorTitle, 1);
        area.Children.Add(vectorTitle);
        Grid.SetRow(vectorScroll, 2);
        Grid.SetColumn(vectorScroll, 1);
        area.Children.Add(vectorScroll);
        Grid.SetRow(lastSampleHint, 3);
        Grid.SetColumnSpan(lastSampleHint, 2);
        area.Children.Add(lastSampleHint);
        return area;
    }

    // разделяем прокручиваемые файлы и поле фото, номер лица доступен с клавиатуры
    private Grid CreateImportArea() {
        Grid files = new() { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
        files.Children.Add(photos);
        Grid.SetRow(removePhoto, 1);
        files.Children.Add(removePhoto);

        Grid image = new() { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 6 };
        image.Children.Add(new Border {
            Background = new SolidColorBrush(Color.Parse("#242424")), CornerRadius = new CornerRadius(4),
            ClipToBounds = true, Child = new Grid { Children = { preview, photoHint } }
        });
        photoHint.Foreground = Brushes.White;
        Grid.SetRow(faces, 1);
        image.Children.Add(faces);
        Grid.SetRow(importSampleName, 2);
        image.Children.Add(importSampleName);

        Grid area = new() { ColumnDefinitions = new ColumnDefinitions("205,*"), ColumnSpacing = 10 };
        area.Children.Add(files);
        Grid.SetColumn(image, 1);
        area.Children.Add(image);
        return area;
    }

    // используем общую форму карточек окна
    private static Border CreateCard(Control content) => new() {
        BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12), Child = content
    };

    // собираем содержимое до привязки к строке, чтобы у текста был один логический родитель
    private static ListBoxItem CreateRow(string title, string description, object tag, Control? leading = null) {
        StackPanel labels = new() { Spacing = 3, Children = {
            new TextBlock { Text = title, FontSize = 16, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }
        }};
        Control content = labels;
        if (leading is not null) {
            Grid withLeading = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            withLeading.Children.Add(leading);
            Grid.SetColumn(labels, 1);
            withLeading.Children.Add(labels);
            content = withLeading;
        }
        ListBoxItem row = new() { Tag = tag, Content = content };
        AutomationProperties.SetName(row, $"{title}, {description}");
        return row;
    }

    // добавляем значок к строке профиля, доступное имя остается текстовым
    private static ListBoxItem CreateProfileRow(FaceProfile profile, FaceProfileAvatarPattern? avatar) =>
        CreateRow(profile.Name, $"Образцов: {profile.Samples.Count}", new ProfileRow(profile, avatar), new FaceProfileAvatar(avatar));
}
