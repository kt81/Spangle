namespace Spangle.Transport.Rtsp;

/// <summary>
/// Resolves a configured dialect name to an <see cref="RtspDialect"/>. The built-ins are
/// always present; applications add their own by registering an <see cref="RtspDialect"/>
/// in DI (a same-named registration overrides a built-in). Unknown names fall back to the
/// default dialect. One instance per server.
/// </summary>
public sealed class RtspDialectRegistry
{
    private readonly Dictionary<string, RtspDialect> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seeds the built-ins, then the DI-registered dialects (which may override by name).</summary>
    public RtspDialectRegistry(IEnumerable<RtspDialect> dialects)
    {
        ArgumentNullException.ThrowIfNull(dialects);
        Add(RtspDialect.Default);
        Add(new LegacyOptionsKeepaliveRtspDialect());
        foreach (RtspDialect dialect in dialects)
        {
            Add(dialect);
        }
    }

    private void Add(RtspDialect dialect) => _byName[dialect.Name] = dialect;

    /// <summary>Resolves a name; null/empty and unknown names both fall back to the default.</summary>
    public RtspDialect Resolve(string? name, out bool known)
    {
        if (string.IsNullOrEmpty(name))
        {
            known = true;
            return RtspDialect.Default;
        }
        known = _byName.TryGetValue(name, out RtspDialect? dialect);
        return known ? dialect! : RtspDialect.Default;
    }
}
