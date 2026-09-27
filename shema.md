ImageVisionApp
├── App.cs                   — системная тема, FluentTheme, ViewLocator и MainViewModel
├── Views/
│   ├── MainWindow.cs         — окно, верхняя панель, вкладки и предпросмотр на C#
│   ├── MainWindow.Information.cs — сведения об изображении и шаблон EXIF
│   ├── MainWindow.Transformations.cs — элементы преобразований и MVVM-привязки
│   ├── MainWindow.Actions.cs — диалоги файлов, передача действий ViewModel и открытие гистограмм
│   └── HistogramWindow.cs    — сравнение двух RGB-гистограмм с общей шкалой
├── ViewModels/
│   └── MainViewModel.cs      — состояние экрана, действия и обновление предпросмотра
├── Models/
│   ├── ImageInfo.cs          — информация и EXIF
│   ├── MetadataItem.cs       — пара имени и значения EXIF
│   ├── ImageTransformationSettings.cs — наблюдаемые настройки преобразований
│   ├── GrayscaleCorrectionMode.cs — режим коррекции серого
│   ├── ImageHistogramData.cs — значения RGB-гистограммы
│   └── ImageHistogramComparison.cs — исходная и обработанная гистограммы
├── Services/
│   ├── ImageStatsFileService.cs — чтение сведений и EXIF через Magick.NET
│   ├── ImageProcessingService.cs — яркость, насыщенность, контраст, серый цвет, поворот, коррекция
│   └── ImageHistogramService.cs — расчёт R/G/B
├── Controls/HistogramChart.cs — отрисовка кривых и логарифмической оси Y
├── Assets/                  — ресурсы, включая сохранённую иконку
└── Program.cs               — запуск Avalonia для Windows и macOS, шрифт Inter
