using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Puck.App.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises PropertyChanged; null or empty = every property.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>An async command that can't run twice at once, and says when it's running.</summary>
public sealed class AsyncCommand(Func<Task> execute) : ObservableObject, ICommand
{
    bool _running;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value)) CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        IsRunning = true;
        try
        {
            await execute();
        }
        catch (Exception e)
        {
            AppLog.Instance.Error("ui", "command failed", e);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
