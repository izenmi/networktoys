using System.Collections.Generic;
using System.IO;
using NetworkToys.App.ViewModels;
using NetworkToys.Core.Models;
using NetworkToys.Core.Storage;
using NetworkToys.Core.Work;

namespace NetworkToys.App.Services;

/// <summary>
/// 管理者権限で起動し直すときに、いまの状態を次のプロセスへ渡す。
///
/// 宛先リストや配色は settings.json にあるので触らない。ここが受け持つのは
/// <b>ファイルに残っていないもの</b> — 測定の途中経過と、各画面に打ち込んだ内容。
///
/// 引き継ぎファイルは %TEMP% に置き、<b>受け取った側が真っ先に消す</b>。
/// 差分比較に貼り付けた機器の出力には enable secret のような認証情報が
/// 入りうるので、置きっぱなしにしない。
/// </summary>
internal static class HandoverService
{
    /// <summary>起動引数。この後ろに引き継ぎファイルのパスが続く。</summary>
    public const string Switch = "--resume";

    public static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"NetworkToys-handover-{Guid.NewGuid():N}.json");

    /// <summary>起動引数から引き継ぎファイルのパスを取り出す。</summary>
    public static string? PathFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static HandoverDocument Capture(ShellViewModel shell)
    {
        var document = new HandoverDocument
        {
            WasRunning = shell.Monitor.IsRunning,
            TcpWasRunning = shell.Tcp.IsRunning,
        };

        CaptureTargets(shell.Monitor, document.Targets);
        CaptureTargets(shell.Tcp, document.TcpTargets);

        HandoverPanels panels = document.Panels;
        shell.DeviceCompare.CaptureInto(panels);

        panels.ConvertInput = shell.Converter.InputText;
        panels.DnsHost = shell.Dns.Name;
        panels.ScanRange = shell.Scan.RangeText;
        panels.SnmpHost = shell.SnmpGet.Host;
        panels.SnmpCommunity = shell.SnmpGet.Community;
        panels.SnmpOid = shell.SnmpGet.OidText;
        panels.TraceHost = shell.Trace.Host;
        panels.SubnetInput = shell.Subnet.Input;
        panels.ConnectionFilter = shell.Connections.Filter;
        panels.WfpFilter = shell.Wfp.Filter;

        // Meraki の API キーはここに入れない(保存しない決まりのもの)

        return document;
    }

    public static void Apply(HandoverDocument document, ShellViewModel shell)
    {
        ApplyTargets(shell.Monitor, document.Targets);
        ApplyTargets(shell.Tcp, document.TcpTargets);

        HandoverPanels panels = document.Panels;
        shell.DeviceCompare.ApplyHandover(panels);

        shell.Converter.InputText = panels.ConvertInput;
        if (panels.DnsHost.Length > 0) shell.Dns.Name = panels.DnsHost;
        if (panels.ScanRange.Length > 0) shell.Scan.RangeText = panels.ScanRange;
        if (panels.SnmpHost.Length > 0) shell.SnmpGet.Host = panels.SnmpHost;
        if (panels.SnmpCommunity.Length > 0) shell.SnmpGet.Community = panels.SnmpCommunity;
        if (panels.SnmpOid.Length > 0) shell.SnmpGet.OidText = panels.SnmpOid;
        if (panels.TraceHost.Length > 0) shell.Trace.Host = panels.TraceHost;
        if (panels.SubnetInput.Length > 0) shell.Subnet.Input = panels.SubnetInput;
        shell.Connections.Filter = panels.ConnectionFilter;
        shell.Wfp.Filter = panels.WfpFilter;
    }

    private static void CaptureTargets(MonitorViewModel monitor, List<HandoverTarget> into)
    {
        foreach (TargetRowViewModel row in monitor.Rows)
        {
            var samples = new ProbeSample[row.HistoryCapacity];
            int count = row.CopyHistory(samples);
            if (count == 0 && !row.Window.HasData) continue;

            var saved = new HandoverTarget
            {
                Key = MonitorViewModel.KeyOf(row.Target),
                Address = row.Address,
                Window = ToDocument(row.Window),
            };

            // 履歴は 3 本の配列で持つ。1 サンプル 1 オブジェクトにすると桁が変わる
            for (int i = 0; i < count; i++)
            {
                saved.Ticks.Add(samples[i].TimestampTicks);
                saved.Rtt.Add(samples[i].RttMs);
                saved.Status.Add((int)samples[i].Status);
            }

            into.Add(saved);
        }
    }

    private static void ApplyTargets(MonitorViewModel monitor, List<HandoverTarget> saved)
    {
        if (saved.Count == 0) return;

        Dictionary<string, HandoverTarget> byKey = [];
        foreach (HandoverTarget target in saved)
            byKey[target.Key] = target;

        foreach (TargetRowViewModel row in monitor.Rows)
        {
            if (!byKey.TryGetValue(MonitorViewModel.KeyOf(row.Target), out HandoverTarget? target))
                continue;

            row.Restore(ToSamples(target), ToCounters(target.Window), target.Address);
        }
    }

    private static List<ProbeSample> ToSamples(HandoverTarget target)
    {
        // 3 本の配列の長さが揃っていないファイルは信用しない
        int count = Math.Min(target.Ticks.Count, Math.Min(target.Rtt.Count, target.Status.Count));
        var samples = new List<ProbeSample>(count);

        for (int i = 0; i < count; i++)
        {
            ProbeStatus status = Enum.IsDefined((ProbeStatus)target.Status[i])
                ? (ProbeStatus)target.Status[i]
                : ProbeStatus.Pending;

            samples.Add(new ProbeSample(target.Ticks[i], (float)target.Rtt[i], status));
        }

        return samples;
    }

    private static HandoverWindow ToDocument(WindowCounters window) => new()
    {
        StartedAtTicks = window.StartedAtTicks,
        Attempts = window.Attempts,
        Successes = window.Successes,
        Responses = window.Responses,
        SumMs = window.SumMs,
        MinMs = window.MinMs,
        MaxMs = window.MaxMs,
        MaxConsecutiveFailures = window.MaxConsecutiveFailures,
        ConsecutiveFailures = window.ConsecutiveFailures,
    };

    private static WindowCounters ToCounters(HandoverWindow window) => WindowCounters.Restore(
        window.StartedAtTicks, window.Attempts, window.Successes, window.Responses,
        window.SumMs, window.MinMs, window.MaxMs,
        window.MaxConsecutiveFailures, window.ConsecutiveFailures);
}
