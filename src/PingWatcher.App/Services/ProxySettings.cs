using System.IO;
using System.Security;
using Microsoft.Win32;
using PingWatcher.Core.Net;

namespace PingWatcher.App.Services;

/// <summary>いまのプロキシ設定(現在のユーザー)。</summary>
internal sealed record ProxyState(ProxyMode Mode, string PacUrl, string Server, string Bypass)
{
    public string Summary => Mode switch
    {
        ProxyMode.Pac => $"PAC: {PacUrl}",
        ProxyMode.Fixed => Bypass.Length > 0 ? $"固定: {Server}(除外あり)" : $"固定: {Server}",
        _ => "プロキシなし(直接接続)",
    };
}

/// <summary>
/// Windows のプロキシ設定(WinINET)の読み書き。HKCU 配下なので<b>管理者権限は不要</b>。
/// 書き込み後は InternetSetOption で変更を通知する(しないとブラウザ等が
/// 再起動まで気づかない)。
/// </summary>
internal static class ProxySettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxyState Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null)
                return new ProxyState(ProxyMode.None, "", "", "");

            string pacUrl = key.GetValue("AutoConfigURL") as string ?? "";
            string server = key.GetValue("ProxyServer") as string ?? "";
            string bypass = key.GetValue("ProxyOverride") as string ?? "";
            bool enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;

            // PAC と固定が両方残っている場合、Windows の設定画面と同じく PAC を優先表示
            ProxyMode mode = pacUrl.Length > 0 ? ProxyMode.Pac
                : enabled && server.Length > 0 ? ProxyMode.Fixed
                : ProxyMode.None;

            return new ProxyState(mode, pacUrl, server, bypass);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            CrashLog.Write(ex, "ProxySettings.Read");
            return new ProxyState(ProxyMode.None, "", "", "");
        }
    }

    /// <summary>適用する。null = 成功。それ以外はそのまま画面に出せる日本語メッセージ。</summary>
    public static string? Apply(ProxyPlan plan)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath)
                ?? throw new IOException("レジストリキーを開けませんでした。");

            switch (plan.Mode)
            {
                case ProxyMode.Pac:
                    key.SetValue("AutoConfigURL", plan.PacUrl);
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    break;

                case ProxyMode.Fixed:
                    key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
                    key.SetValue("ProxyServer", plan.Server);
                    if (plan.Bypass.Length > 0)
                        key.SetValue("ProxyOverride", plan.Bypass);
                    else
                        key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    break;

                default:
                    key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    break;
            }

            // 変更したことを WinINET に伝える。伝えないと既存プロセスが古い設定のまま動く
            Interop.NativeMethods.RefreshProxySettings();
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"プロキシ設定を書き込めませんでした: {ex.Message}";
        }
    }
}
