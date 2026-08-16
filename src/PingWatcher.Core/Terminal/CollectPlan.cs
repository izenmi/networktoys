using System.Globalization;

namespace PingWatcher.Core.Terminal;

/// <summary>実行するかどうかを判定済みのコマンド 1 本。</summary>
public sealed record PlannedCommand(int LineNumber, string Command, CommandRisk Risk, string? Reason);

public sealed record CommandListParseResult(
    IReadOnlyList<PlannedCommand> Commands,
    int CommentLines,
    IReadOnlyList<string> Errors)
{
    /// <summary>実際に投げる本数。</summary>
    public int RunnableCount => Commands.Count(c => c.Risk != CommandRisk.Blocked);
}

/// <summary>収集する機器 1 台。</summary>
/// <param name="UseSsh">true なら SSH、false なら Telnet。ポートは方式から決まる。</param>
public sealed record DeviceEntry(string Host, bool UseSsh, string UserName, string Memo)
{
    public const int SshPort = 22;
    public const int TelnetPort = 23;

    public int Port => UseSsh ? SshPort : TelnetPort;
}

public sealed record DeviceListParseResult(
    IReadOnlyList<DeviceEntry> Devices,
    int CommentLines,
    IReadOnlyList<string> Errors);

/// <summary>
/// コマンド一覧テキストの解釈。
///
/// <b>行内注釈は解釈しない。</b><c>show run | include !</c> や
/// <c>show interfaces | exclude 0.00</c> のように <c>!</c> や <c>#</c> は
/// コマンドの一部として正当に現れる。注釈はコマンドの<b>上の行</b>に書く決まりにしてある
/// （Cisco の設定ファイルと同じ見た目なので、現場の人に説明が要らない）。
/// </summary>
public static class CommandListParser
{
    public const int DefaultLimit = 200;

    public static CommandListParseResult Parse(string? text, int limit = DefaultLimit)
    {
        List<PlannedCommand> commands = [];
        List<string> errors = [];
        int comments = 0;

        if (string.IsNullOrWhiteSpace(text))
            return new CommandListParseResult(commands, 0, errors);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            // 全角スペースも空白として扱う(表計算からの貼り付けで混ざる)
            string line = lines[i].Trim().Trim('　');
            if (line.Length == 0) continue;

            if (line[0] is '!' or '#' or ';')
            {
                comments++;
                continue;
            }

            if (commands.Count >= limit)
            {
                errors.Add($"{limit} 本を超えたため、{i + 1} 行目以降は読み飛ばしました。");
                break;
            }

            CommandVerdict verdict = CiscoCommandGuard.Classify(line);
            commands.Add(new PlannedCommand(i + 1, line, verdict.Risk, verdict.Reason));
        }

        return new CommandListParseResult(commands, comments, errors);
    }
}

/// <summary>
/// 機器一覧の読み書き。設定ファイルへ往復させるためだけのもので、
/// 画面では 1 台 1 行の表として編集する（書式を人に覚えてもらわない）。
///
/// 1 行は <c>ホスト,ssh|telnet,ユーザー名,メモ</c>。区切りはカンマとタブ。
/// </summary>
public static class DeviceListParser
{
    public const int DefaultLimit = 200;

    public static DeviceListParseResult Parse(string? text, bool defaultUseSsh, int limit = DefaultLimit)
    {
        List<DeviceEntry> devices = [];
        List<string> errors = [];
        int comments = 0;

        if (string.IsNullOrWhiteSpace(text))
            return new DeviceListParseResult(devices, 0, errors);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim().Trim('　');
            if (line.Length == 0) continue;

            if (line[0] is '!' or '#' or ';')
            {
                comments++;
                continue;
            }

            if (devices.Count >= limit)
            {
                errors.Add($"{limit} 台を超えたため、{i + 1} 行目以降は読み飛ばしました。");
                break;
            }

            string[] fields = line.Split([',', '\t'], StringSplitOptions.TrimEntries);
            string host = fields[0];

            if (host.Length == 0)
            {
                errors.Add($"{i + 1} 行目: 宛先がありません。");
                continue;
            }

            // 2 番目が ssh / telnet なら方式。そうでなければユーザー名として読む
            // (方式を持たなかった頃に書かれた行をそのまま受けられるように)
            bool useSsh = defaultUseSsh;
            int next = 1;

            if (fields.Length > 1)
            {
                if (string.Equals(fields[1], "ssh", StringComparison.OrdinalIgnoreCase))
                {
                    useSsh = true;
                    next = 2;
                }
                else if (string.Equals(fields[1], "telnet", StringComparison.OrdinalIgnoreCase))
                {
                    useSsh = false;
                    next = 2;
                }
            }

            devices.Add(new DeviceEntry(
                Host: host,
                UseSsh: useSsh,
                UserName: fields.Length > next ? fields[next] : "",
                Memo: fields.Length > next + 1 ? string.Join(", ", fields[(next + 1)..]) : ""));
        }

        return new DeviceListParseResult(devices, comments, errors);
    }

    /// <summary>settings.json へ往復させるための書き戻し。</summary>
    public static string Format(IEnumerable<DeviceEntry> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var builder = new System.Text.StringBuilder();

        foreach (DeviceEntry device in devices)
        {
            builder.Append(device.Host).Append(',')
                   .Append(device.UseSsh ? "ssh" : "telnet").Append(',')
                   .Append(device.UserName);

            if (device.Memo.Length > 0)
                builder.Append(',').Append(device.Memo);

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
