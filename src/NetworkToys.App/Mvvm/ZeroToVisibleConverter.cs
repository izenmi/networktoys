using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetworkToys.App.Mvvm;

/// <summary>
/// 件数 0 のときだけ Visible。空の一覧に「押すとここに出ます」の一言
/// （<c>EmptyHint</c> スタイル）を重ねるための変換（2026-08-20 の UI 改善）。
/// </summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
