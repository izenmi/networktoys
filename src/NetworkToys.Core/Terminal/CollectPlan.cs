using System.Globalization;

namespace NetworkToys.Core.Terminal;

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

    /// <summary>CSV から取り込んだ 1 台。<b>パスワードは <see cref="DeviceEntry"/> に持たせない</b>
    /// — あちらは settings.json へ往復するので、入れ物を分けて書き戻る道を絶つ。</summary>
    public sealed record ImportedDevice(DeviceEntry Entry, string Password, string EnablePassword);

    public sealed record CsvParseResult(
        IReadOnlyList<ImportedDevice> Devices,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// 取り込み用 CSV の解釈。列は
    /// <c>宛先,ssh|telnet,ユーザー名,パスワード,enable,メモ</c>（2026-08-20 ユーザー指示で
    /// パスワードの列を足した）。設定ファイルの書式（<see cref="Parse"/>）とは別で、
    /// <b>この書式を書き出すのはひな型だけ・実物のパスワードを書き出す口は作らない</b>。
    /// </summary>
    public static CsvParseResult ParseCsv(string? text, bool defaultUseSsh, int limit = DefaultLimit)
    {
        List<ImportedDevice> devices = [];
        List<string> errors = [];

        foreach ((string[] fields, int next, bool useSsh) in DeviceLines(text, defaultUseSsh, limit, errors, out _))
        {
            string At(int offset) => fields.Length > next + offset ? fields[next + offset] : "";

            devices.Add(new ImportedDevice(
                new DeviceEntry(
                    Host: fields[0],
                    UseSsh: useSsh,
                    UserName: At(0),
                    Memo: Rest(fields, next + 3)),
                Password: At(1),
                EnablePassword: At(2)));
        }

        return new CsvParseResult(devices, errors);
    }

    public static DeviceListParseResult Parse(string? text, bool defaultUseSsh, int limit = DefaultLimit)
    {
        List<DeviceEntry> devices = [];
        List<string> errors = [];

        IReadOnlyList<(string[] Fields, int Next, bool UseSsh)> rows =
            DeviceLines(text, defaultUseSsh, limit, errors, out int comments);

        foreach ((string[] fields, int next, bool useSsh) in rows)
        {
            devices.Add(new DeviceEntry(
                Host: fields[0],
                UseSsh: useSsh,
                UserName: fields.Length > next ? fields[next] : "",
                Memo: Rest(fields, next + 1)));
        }

        return new DeviceListParseResult(devices, comments, errors);
    }

    /// <summary>指定位置から後ろを全部メモとして繋ぎ直す（メモの中のカンマを守る）。</summary>
    private static string Rest(string[] fields, int from)
        => fields.Length > from ? string.Join(", ", fields[from..]) : "";

    /// <summary>
    /// 機器一覧の 1 行を読む共通の骨組み（<see cref="Parse"/> と <see cref="ParseCsv"/> で共用）。
    /// 注釈・空行・宛先なし・上限超えをここで捌き、
    /// <c>next</c> は「ユーザー名が始まる位置」（2 番目が ssh / telnet なら 2、
    /// 方式を省いた行なら 1 — 方式を持たなかった頃の行をそのまま受けるため）。
    /// </summary>
    private static IReadOnlyList<(string[] Fields, int Next, bool UseSsh)> DeviceLines(
        string? text, bool defaultUseSsh, int limit, List<string> errors, out int comments)
    {
        comments = 0;

        var rows = new List<(string[], int, bool)>();

        if (string.IsNullOrWhiteSpace(text)) return rows;

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

            if (rows.Count >= limit)
            {
                errors.Add($"{limit} 台を超えたため、{i + 1} 行目以降は読み飛ばしました。");
                break;
            }

            string[] fields = line.Split([',', '\t'], StringSplitOptions.TrimEntries);

            if (fields[0].Length == 0)
            {
                errors.Add($"{i + 1} 行目: 宛先がありません。");
                continue;
            }

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

            rows.Add((fields, next, useSsh));
        }

        return rows;
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
