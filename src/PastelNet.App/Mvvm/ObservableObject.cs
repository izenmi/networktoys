using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PastelNet.App.Mvvm;

/// <summary>
/// 変更通知の最小実装。
///
/// MVVM ライブラリを入れてもよいが、必要なのはこれと <see cref="RelayCommand"/> だけで、
/// ソースジェネレータのバージョン依存を持ち込む方が（ローカルでビルドを確認できない以上）
/// リスクが大きい。依存は少ないほどよい。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 値が変わったときだけ通知する。測定結果は毎秒流れてくるので、
    /// 同じ値で通知を撒くと数百宛先で無駄な再描画が積み上がる。
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
