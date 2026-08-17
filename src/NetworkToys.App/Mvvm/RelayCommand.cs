using System.Windows.Input;

namespace NetworkToys.App.Mvvm;

/// <summary>ボタン用の最小コマンド実装。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 引数を 1 つ受け取るコマンド。
///
/// 一覧の行から「この行を消す」のように、<b>どれに対する操作か</b>を
/// 渡す必要があるときだけ使う（引数の要らない操作は <see cref="RelayCommand"/>）。
/// </summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => canExecute is null || canExecute(parameter is T typed ? typed : default);

    public void Execute(object? parameter) => execute(parameter is T typed ? typed : default);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
