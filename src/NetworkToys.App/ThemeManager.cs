using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace NetworkToys.App;

internal enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// 配色の切り替え。
///
/// 仕組みは単純で、<see cref="Application.Resources"/> にマージ済みの
/// パレット辞書を丸ごと差し替えるだけ。画面側は色をすべて DynamicResource で
/// 引いているので、差し替えた瞬間に全タブへ反映される。
///
/// <b>StaticResource で色を引いた箇所は追随しない。</b>新しく色を使うときは
/// 必ず DynamicResource にすること。
/// </summary>
internal static class ThemeManager
{
    private const string DarkSource = "Resources/Palette.Dark.xaml";
    private const string LightSource = "Resources/Palette.xaml";

    /// <summary>いま適用している配色。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>切り替わった後に発火する。描画をやり直したい要素が拾う。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// 保存された配色を読んで適用する。起動直後、ウィンドウを作る前に呼ぶこと。
    /// 保存が無ければライトにする（2026-08-16 から既定はライト）。
    /// </summary>
    public static void Initialize()
    {
        AppTheme theme = Load() ?? AppTheme.Light;

        // App.xaml が読み込んでいるのはライト。既定のままなら差し替えは要らない
        if (theme == AppTheme.Light)
        {
            Current = AppTheme.Light;
            return;
        }

        Apply(theme, save: false);
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public static void Apply(AppTheme theme) => Apply(theme, save: true);

    private static void Apply(AppTheme theme, bool save)
    {
        Collection<ResourceDictionary>? merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        var replacement = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Dark ? DarkSource : LightSource, UriKind.Relative),
        };

        // 位置を決め打ちにしない。App.xaml のマージ順を変えても壊れないようにする
        int index = IndexOfPalette(merged);
        if (index < 0)
            merged.Insert(0, replacement);
        else
            merged[index] = replacement;

        Current = theme;

        if (save)
            Save(theme);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static int IndexOfPalette(Collection<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null && source.Contains("Palette", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 保存先は settings.json(全設定の統合ファイル)。配色はウィンドウを作る前に
    /// 決まっていないと一瞬明るい画面が出るため、App 起動時に
    /// <see cref="Settings.Initialize"/> → <see cref="Initialize"/> の順で呼ぶこと。
    /// </summary>
    private static AppTheme? Load() => Settings.Current.Theme switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => null,
    };

    private static void Save(AppTheme theme)
    {
        try
        {
            Settings.Current.Theme = theme == AppTheme.Dark ? "dark" : "light";
            Settings.Save();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ThemeManager.Save");
        }
    }
}
