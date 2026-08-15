using System.Media;

namespace PastelNet.App.Services;

/// <summary>知らせたい出来事。</summary>
internal enum AlertKind
{
    /// <summary>宛先が落ちた。</summary>
    Down,

    /// <summary>宛先が戻った。</summary>
    Recovered,

    /// <summary>落ちていたものが全部戻った。</summary>
    AllClear,
}

/// <summary>
/// 落ちた瞬間・戻った瞬間を音で知らせる。
///
/// 現場ではラックの裏に回っていたり、TeraTerm を触っていたりして画面を見ていられない。
/// <b>切替作業で知りたいのはまさにその瞬間</b>なので、音で気づけるようにする。
/// </summary>
internal static class AlertSound
{
    /// <summary>鳴りっぱなしにならないよう、この間隔はあける。</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(3);

    private static readonly Lock Gate = new();
    private static DateTime _lastPlayedAt = DateTime.MinValue;

    /// <summary>音を出すか。</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// 環境の音設定に左右されず、はっきり鳴らすか。
    /// Windows の音が「なし」に設定されていると <see cref="SystemSounds"/> は無音になる。
    /// </summary>
    public static bool UseBeep { get; set; }

    public static void Play(AlertKind kind)
    {
        if (!Enabled) return;

        lock (Gate)
        {
            DateTime now = DateTime.Now;

            // 何件同時に落ちても 1 回で足りる
            if (now - _lastPlayedAt < MinimumInterval)
                return;

            _lastPlayedAt = now;
        }

        // Console.Beep は鳴っている間ブロックするので、必ず別スレッドへ逃がす。
        // UI スレッドで呼ぶと画面が固まる
        _ = Task.Run(() => PlayCore(kind));
    }

    private static void PlayCore(AlertKind kind)
    {
        try
        {
            if (UseBeep)
            {
                PlayBeep(kind);
                return;
            }

            switch (kind)
            {
                case AlertKind.Down:
                    SystemSounds.Exclamation.Play();
                    break;
                case AlertKind.Recovered:
                    SystemSounds.Asterisk.Play();
                    break;
                default:
                    SystemSounds.Beep.Play();
                    break;
            }
        }
        catch (Exception ex)
        {
            // 音が出ないことでアプリを止めない
            CrashLog.Write(ex, "AlertSound");
        }
    }

    /// <summary>下降音なら異変、上昇音なら復旧。音の向きだけで意味が分かるようにする。</summary>
    private static void PlayBeep(AlertKind kind)
    {
        switch (kind)
        {
            case AlertKind.Down:
                Console.Beep(880, 120);
                Console.Beep(440, 220);
                break;

            case AlertKind.Recovered:
                Console.Beep(660, 110);
                Console.Beep(990, 160);
                break;

            default:
                Console.Beep(660, 100);
                Console.Beep(880, 100);
                Console.Beep(1320, 200);
                break;
        }
    }
}
