ImageVisionApp
├── Views/
│   ├── MainWindow.axaml       — расположение кнопок, вкладок и изображения
│   └── MainWindow.axaml.cs    — только открытие системного диалога файлов
├── ViewModels/
│   └── MainViewModel.cs       — состояние экрана и команды
├── Models/
│   ├── ImageInfo.cs           — информация и EXIF
│   └── HistogramData.cs       — значения гистограммы
├── Services/
│   ├── ImageFileService.cs    — загрузка, сведения и сохранение
│   ├── ImageProcessingService.cs — яркость, серый цвет, поворот, коррекция
│   └── HistogramService.cs    — расчёт R/G/B
└── Program.cs