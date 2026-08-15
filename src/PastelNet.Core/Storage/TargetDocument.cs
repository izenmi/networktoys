using System.Text.Json.Serialization;
using PastelNet.Core.Models;

namespace PastelNet.Core.Storage;

/// <summary>targets.json の中身。</summary>
public sealed class TargetDocument
{
    /// <summary>将来フォーマットを変えたときの移行判断用。</summary>
    public int Version { get; set; } = 1;

    public List<Target> Targets { get; set; } = [];

    public MonitorSettings Settings { get; set; } = new();
}

/// <summary>
/// リフレクションを使わないシリアライザ。起動時間に効くほか、
/// 将来トリミングや AOT を検討する余地も残る。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TargetDocument))]
[JsonSerializable(typeof(ProfileDocument))]
[JsonSerializable(typeof(Work.WorkSessionDocument))]
public partial class PastelNetJsonContext : JsonSerializerContext;
