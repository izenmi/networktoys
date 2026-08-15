namespace PingWatcher.Core.Models;

/// <summary>
/// 測定ループから UI へ流す 1 件分の結果。
/// 宛先ごとに独立して飛んでくるので、どの宛先のものかを Id で持つ。
/// </summary>
/// <param name="TargetId">対象の <see cref="Target.Id"/>。</param>
/// <param name="Sample">測定結果。</param>
/// <param name="ResolvedAddress">名前解決の結果。解決できていなければ null。</param>
public readonly record struct ProbeResult(string TargetId, ProbeSample Sample, string? ResolvedAddress);
