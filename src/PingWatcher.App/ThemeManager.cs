using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace PingWatcher.App;

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
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>切り替わった後に発火する。描画をやり直したい要素が拾う。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// 保存された配色を読んで適用する。起動直後、ウィンドウを作る前に呼ぶこと。
    /// 保存が無ければダークにする（既定の見た目）。
    /// </summary>
    public static void Initialize()
    {
        AppTheme theme = Load() ?? AppTheme.Dark;

        // App.xaml が読み込んでいるのはダーク。既定のままなら差し替えは要らない
        if (theme == AppTheme.Dark)
        {
            Current = AppTheme.Dark;
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
    /// 保存先。設定 JSON に混ぜていないのは、あちらを読むのが
    /// ウィンドウを作った後だから。配色はその前に決まっていないと、
    /// 一瞬明るい画面が出てから暗くなる。
    /// </summary>
    private static string PathOfSetting() => AppData.PathOf("theme.txt");

    private static AppTheme? Load()
    {
        try
        {
            string path = PathOfSetting();
            if (!File.Exists(path)) return null;

            return File.ReadAllText(path).Trim().ToLowerInvariant() switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // 読めなくても既定の配色で動かせる。起動を止める理由にはしない
            CrashLog.Write(ex, "ThemeManager.Load");
            return null;
        }
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            string path = PathOfSetting();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, theme == AppTheme.Dark ? "dark" : "light");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ThemeManager.Save");
        }
    }
}
