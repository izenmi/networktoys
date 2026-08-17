using System.Globalization;

namespace NetworkToys.Core.Snmp;

/// <summary>
/// SNMP の OID。サブ識別子列とドット表記を相互変換し、既知 OID には名前を添える。
/// </summary>
public sealed class Oid : IEquatable<Oid>
{
    private static readonly IReadOnlyDictionary<string, string> KnownNames = new Dictionary<string, string>
    {
        ["1.3.6.1.2.1.1.1.0"] = "sysDescr",
        ["1.3.6.1.2.1.1.2.0"] = "sysObjectID",
        ["1.3.6.1.2.1.1.3.0"] = "sysUpTime",
        ["1.3.6.1.2.1.1.4.0"] = "sysContact",
        ["1.3.6.1.2.1.1.5.0"] = "sysName",
        ["1.3.6.1.2.1.1.6.0"] = "sysLocation",
        ["1.3.6.1.2.1.1.7.0"] = "sysServices",
        ["1.3.6.1.2.1.2.1.0"] = "ifNumber",
        ["1.3.6.1.6.3.1.1.4.1.0"] = "snmpTrapOID",
        ["1.3.6.1.6.3.1.1.5.1"] = "coldStart",
        ["1.3.6.1.6.3.1.1.5.2"] = "warmStart",
        ["1.3.6.1.6.3.1.1.5.3"] = "linkDown",
        ["1.3.6.1.6.3.1.1.5.4"] = "linkUp",
        ["1.3.6.1.6.3.1.1.5.5"] = "authenticationFailure",
    };

    public Oid(IReadOnlyList<uint> subIds)
    {
        ArgumentNullException.ThrowIfNull(subIds);
        SubIds = [.. subIds];
    }

    public IReadOnlyList<uint> SubIds { get; }

    /// <summary>ドット表記。</summary>
    public string Text => string.Join('.', SubIds.Select(s => s.ToString(CultureInfo.InvariantCulture)));

    /// <summary>既知の名前。無ければドット表記のまま。</summary>
    public string DisplayName => KnownNames.TryGetValue(Text, out string? name) ? name : Text;

    /// <summary>ドット表記から作る。読めなければ null。</summary>
    public static Oid? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim().TrimStart('.');
        var subs = new List<uint>();

        foreach (string part in trimmed.Split('.'))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
                return null;
            subs.Add(value);
        }

        return subs.Count >= 2 ? new Oid(subs) : null;
    }

    /// <summary>この OID が prefix の配下（同じか、続き）か。ウォークの範囲判定に使う。</summary>
    public bool IsDescendantOf(Oid prefix)
    {
        if (SubIds.Count < prefix.SubIds.Count) return false;

        for (int i = 0; i < prefix.SubIds.Count; i++)
        {
            if (SubIds[i] != prefix.SubIds[i]) return false;
        }

        return true;
    }

    public bool Equals(Oid? other) => other is not null && SubIds.SequenceEqual(other.SubIds);

    public override bool Equals(object? obj) => Equals(obj as Oid);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (uint s in SubIds) hash.Add(s);
        return hash.ToHashCode();
    }

    public override string ToString() => Text;
}
