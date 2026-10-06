using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DominoBridge.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _async;
    private readonly Action? _sync;
    private readonly Func<bool>? _can;
    private bool _running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _sync = execute; _can = canExecute; }
    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) { _async = execute; _can = canExecute; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_async != null)
        {
            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _async(); }
            finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
        }
        else _sync!();
    }
}

public sealed class MappingRowVM : ObservableObject
{
    private string _edcIndexText = "";
    public MappingRowVM(int columnIndex, string header, string sample)
    {
        ColumnIndex = columnIndex; Header = header; Sample = sample;
    }
    /// <summary>0-based column in the table.</summary>
    public int ColumnIndex { get; }
    public string Header { get; }
    public string Sample { get; }
    /// <summary>Empty = this column is not sent.</summary>
    public string EdcIndexText { get => _edcIndexText; set => Set(ref _edcIndexText, value); }
}
