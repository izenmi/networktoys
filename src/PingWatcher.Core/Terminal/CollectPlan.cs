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
public sealed record DeviceEntry(string Host, int Port, string UserName, string Memo);

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
/// 機器一覧テキストの解釈。書式は <c>ホスト[:ポート],ユーザー名[,メモ]</c>。
/// 区切りはカンマとタブ（表計算からの貼り付けを受ける）。
/// </summary>
public static class DeviceListParser
{
    public const int DefaultLimit = 200;

    public static DeviceListParseResult Parse(string? text, int defaultPort, int limit = DefaultLimit)
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
            string hostField = fields[0];

            if (!TrySplitHost(hostField, defaultPort, out string host, out int port))
            {
                errors.Add($"{i + 1} 行目: ポート番号が正しくありません（{hostField}）。");
                continue;
            }

            if (host.Length == 0)
            {
                errors.Add($"{i + 1} 行目: 宛先がありません。");
                continue;
            }

            devices.Add(new DeviceEntry(
                Host: host,
                Port: port,
                UserName: fields.Length > 1 ? fields[1] : "",
                Memo: fields.Length > 2 ? string.Join(", ", fields[2..]) : ""));
        }

        return new DeviceListParseResult(devices, comments, errors);
    }

    /// <summary>settings.json へ往復させるための書き戻し。</summary>
    public static string Format(IEnumerable<DeviceEntry> devices, int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var builder = new System.Text.StringBuilder();

        foreach (DeviceEntry device in devices)
        {
            builder.Append(device.Host);

            if (device.Port != defaultPort)
                builder.Append(':').Append(device.Port.ToString(CultureInfo.InvariantCulture));

            builder.Append(',').Append(device.UserName);

            if (device.Memo.Length > 0)
                builder.Append(',').Append(device.Memo);

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>ホスト:ポート</c> を割る。
    /// <b>コロンが 1 つのときだけ</b>割るのは、IPv6 リテラルを誤って割らないため
    /// （既存の宛先リストの解釈と同じ規則）。
    /// </summary>
    private static bool TrySplitHost(string field, int defaultPort, out string host, out int port)
    {
        host = field;
        port = defaultPort;

        int colons = field.Count(c => c == ':');
        if (colons != 1) return true;

        int index = field.IndexOf(':', StringComparison.Ordinal);
        string portText = field[(index + 1)..];

        if (!int.TryParse(portText, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 1 or > 65535)
            return false;

        host = field[..index];
        port = parsed;
        return true;
    }
}
