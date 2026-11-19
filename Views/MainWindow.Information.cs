using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private static StackPanel CreateInformationPanel() {
        // осн блок прокрутки данных
        StackPanel information = new StackPanel { Spacing = 8 };
        information.Children.Add(new TextBlock {
            Text = "Информация",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        });

        StackPanel photo = new() { Spacing = 8 };
        AddFileInformation(photo);
        photo.Children.Add(CreateSectionSeparator());
        AddDimensionsInformation(photo);
        photo.Children.Add(CreateSectionSeparator());
        AddColorInformation(photo);
        photo.Children.Add(CreateSectionSeparator());
        AddTechnicalInformation(photo);
        photo.Children.Add(CreateSectionSeparator());
        AddExifInformation(photo);
        photo.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });

        StackPanel video = CreateVideoInformationPanel();
        video.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        information.Children.Add(photo);
        information.Children.Add(video);
        return information;
    }

    // показываем сведения открытого видео по мере появления данных о дорожках
    private static StackPanel CreateVideoInformationPanel() {
        StackPanel information = new() { Spacing = 8 };
        TextBlock heading = CreateSectionHeading("Сведения о видео");
        heading.Margin = new Thickness(0, 10, 0, 2);
        information.Children.Add(heading);

        TextBlock fileName = CreateBoundText(viewModel => viewModel.CurrentDocument!.FileName);
        fileName.TextWrapping = TextWrapping.Wrap;
        AddInformationField(information, "Имя файла:", fileName);
        AddInformationField(information, "Размер файла:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.FormattedFileSize));
        AddInformationField(information, "Длительность:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.FormattedDuration));
        AddInformationField(information, "Разрешение:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.Resolution));
        AddInformationField(information, "Частота кадров:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.FrameRateDescription));
        AddInformationField(information, "Звук:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.AudioDescription));

        information.Children.Add(CreateSectionSeparator());
        information.Children.Add(CreateSectionHeading("Подробности"));
        AddInformationField(information, "Контейнер:", new TextBlock { Text = VideoInfo.UnknownValue });

        TextBlock codec = CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.CodecDescription);
        codec.TextWrapping = TextWrapping.Wrap;
        AddInformationField(information, "Видеокодек:", codec);
        AddInformationField(information, "Битрейт видеодорожки:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.BitrateDescription));
        AddInformationField(information, "Ориентация дорожки:",
            CreateBoundText(viewModel => viewModel.CurrentVideoInfo!.OrientationDescription));
        AddInformationField(information, "Число кадров:", new TextBlock { Text = VideoInfo.UnknownValue });
        return information;
    }

    private static void AddFileInformation(StackPanel information) {
        // раздел инфы
        TextBlock heading = CreateSectionHeading("Файл");
        heading.Margin = new Thickness(0, 10, 0, 2);
        information.Children.Add(heading);

        // ! нужен компилятору; пока картинки нет, null в цепочке обрабатывает привязка
        TextBlock fileName = CreateBoundText(viewModel => viewModel.CurrentImageInfo!.FileName);
        fileName.TextWrapping = TextWrapping.Wrap;
        AddInformationField(information, "Имя файла:", fileName);
        AddInformationField(information, "Размер файла:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.FormattedFileSize));
        AddInformationField(information, "Формат:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Format));
    }

    private static void AddDimensionsInformation(StackPanel information) {
        // Раздел инфы о размере
        information.Children.Add(CreateSectionHeading("Размеры изображения"));
        AddInformationField(information, "Разрешение:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Resolution));
        AddInformationField(information, "Количество пикселей:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.PixelCount));
        AddInformationField(information, "Мегапиксели:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Megapixels));
        AddInformationField(information, "Соотношение сторон:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.AspectRatio));
        AddInformationField(information, "Ориентация:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Orientation));
    }

    private static void AddColorInformation(StackPanel information) {
        // раздел инфы о цвете
        information.Children.Add(CreateSectionHeading("Цвет"));
        AddInformationField(information, "Цветовая модель:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.ColorModel));
        AddInformationField(information, "Тип цвета:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.ColorType));
        AddInformationField(information, "Глубина одного канала:", new StackPanel {
            Orientation = Orientation.Horizontal,
            Children = {
                CreateBoundText(viewModel => viewModel.CurrentImageInfo!.BitsPerChannel),
                new TextBlock { Text = " бит" }
            }
        });
        AddInformationField(information, "Общая глубина цвета:", new StackPanel {
            Orientation = Orientation.Horizontal,
            Children = {
                CreateBoundText(viewModel => viewModel.CurrentImageInfo!.BitsPerPixel),
                new TextBlock { Text = " бит на пиксель" }
            }
        });
        AddInformationField(information, "Количество каналов:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.ChannelCount));
        AddInformationField(information, "Наличие альфа канала:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.AlphaDescription));
        AddInformationField(information, "Количество уникальных цветов:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.UniqueColorCount));
        AddInformationField(information, "Гамма:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.GammaDescription));
    }

    private static void AddTechnicalInformation(StackPanel information) {
        // раздел о тех данных
        information.Children.Add(CreateSectionHeading("Технические данные"));
        AddInformationField(information, "Сжатие:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Compression));
        AddInformationField(information, "Параметр качества:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Quality));
        AddInformationField(information, "Чересстрочность:",
            CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Interlace));

        TextBlock profiles = CreateBoundText(viewModel => viewModel.CurrentImageInfo!.Profiles);
        profiles.TextWrapping = TextWrapping.Wrap;
        AddInformationField(information, "Встроенные профили:", profiles);
    }

    private static void AddExifInformation(StackPanel information) {
        // раздел о ехифе
        information.Children.Add(CreateSectionHeading("EXIF"));
        TextBlock exifStatus = CreateBoundText(viewModel => viewModel.CurrentImageInfo!.ExifStatus);
        exifStatus.Margin = new Thickness(0, 0, 0, 6);
        information.Children.Add(exifStatus);

        ItemsControl exifItems = new ItemsControl {
            ItemTemplate = new FuncDataTemplate<MetadataItem>((item, nameScope) => CreateExifItem())
        };
        exifItems.Bind(ItemsControl.ItemsSourceProperty,
            CompiledBinding.Create<MainViewModel, IReadOnlyList<MetadataItem>>(
                viewModel => viewModel.CurrentImageInfo!.ExifData));
        information.Children.Add(exifItems);
    }

    private static StackPanel CreateExifItem() {
        // тут DataContext уже одна запись EXIF, поэтому читаем Name/Value из MetadataItem
        TextBlock name = new TextBlock {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        name.Bind(TextBlock.TextProperty,
            CompiledBinding.Create<MetadataItem, string>(item => item.Name));

        TextBlock value = new TextBlock { TextWrapping = TextWrapping.Wrap };
        value.Bind(TextBlock.TextProperty,
            CompiledBinding.Create<MetadataItem, string>(item => item.Value));

        return new StackPanel {
            Margin = new Thickness(0, 0, 0, 10),
            Children = { name, value }
        };
    }

    private static void AddInformationField(StackPanel information, string label, Control value) {
        // кладём подпись и значение прямо в общий список, чтобы Spacing работал между ними
        information.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        information.Children.Add(value);
    }
}
