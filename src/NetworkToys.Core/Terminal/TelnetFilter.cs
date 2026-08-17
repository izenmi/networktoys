namespace NetworkToys.Core.Terminal;

/// <summary>
/// Telnet の制御シーケンス（IAC）を取り除き、必要な返事を組み立てる。
///
/// 方針は<b>全部断る</b>。こちらは端末エミュレータではなくログを取る機械なので、
/// エコーも行モードも要らない。Cisco は交渉に失敗しても素の NVT で会話を続ける。
///
/// <b>IAC の並びは TCP のセグメント境界をまたぐ。</b>「IAC で終わったチャンク」
/// 「IAC DO で終わったチャンク」「SB の途中で切れたチャンク」を持ち越せないと
/// 稀に文字が化ける。だから状態を持つフィルタにしてある。
/// </summary>
public sealed class TelnetFilter
{
    private const byte Iac = 255;   // Interpret As Command
    private const byte Se = 240;    // Subnegotiation End
    private const byte Sb = 250;    // Subnegotiation Begin
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    private enum State
    {
        Data,
        Iac,
        Negotiate,      // DO/DONT/WILL/WONT のオプション番号待ち
        Subnegotiate,   // IAC SE が来るまで読み飛ばす
        SubnegotiateIac,
    }

    private State _state = State.Data;
    private byte _verb;

    /// <summary>
    /// 受信バイトから制御を抜き、本文だけを <paramref name="payload"/> へ書く。
    /// </summary>
    /// <param name="reply">相手へ送り返すバイト列。空なら送らなくてよい。</param>
    /// <returns><paramref name="payload"/> に書いた本文のバイト数。</returns>
    public int Process(ReadOnlySpan<byte> input, Span<byte> payload, out byte[] reply)
    {
        List<byte>? answer = null;
        int written = 0;

        foreach (byte value in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (value == Iac)
                        _state = State.Iac;
                    else
                        payload[written++] = value;
                    break;

                case State.Iac:
                    if (value == Iac)
                    {
                        // IAC IAC は本文の 0xFF 1 バイト
                        payload[written++] = Iac;
                        _state = State.Data;
                    }
                    else if (value is Do or Dont or Will or Wont)
                    {
                        _verb = value;
                        _state = State.Negotiate;
                    }
                    else if (value == Sb)
                    {
                        _state = State.Subnegotiate;
                    }
                    else
                    {
                        // NOP / GA / DM など。捨てる
                        _state = State.Data;
                    }
                    break;

                case State.Negotiate:
                    // 相手の申し出も要求も断る。DONT/WONT には返事をしない(すでにその状態)
                    if (_verb == Do)
                        (answer ??= []).AddRange([Iac, Wont, value]);
                    else if (_verb == Will)
                        (answer ??= []).AddRange([Iac, Dont, value]);

                    _state = State.Data;
                    break;

                case State.Subnegotiate:
                    if (value == Iac) _state = State.SubnegotiateIac;
                    break;

                case State.SubnegotiateIac:
                    // IAC SE で終わり。IAC IAC ならデータなので読み飛ばしを続ける
                    _state = value == Se ? State.Data : State.Subnegotiate;
                    break;
            }
        }

        reply = answer?.ToArray() ?? [];
        return written;
    }
}
