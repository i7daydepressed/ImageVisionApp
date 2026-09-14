using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageVisionApp.ViewModels;

public abstract class ViewModelBase : ObservableObject {}
// реализует механизм уведомления интерфейса об изменении свойств (интерфейс отрисовывает обновления)
// после: StatusMessage = fileName;
// <TextBlock Text="{Binding StatusMessage}" /> автоматически обновляется
// [ObservableProperty] генерит свойство, которое отправляет такое уведомление при изменении значения

// по сути этот класс явл суперклассом для всех вьюмоделов тк наследуясь от него атрибут уведомления интерфейся об изм переменных будет работать тк видемо обработчик аннотации смотрит ток на наслдеников наследника ObservableObject