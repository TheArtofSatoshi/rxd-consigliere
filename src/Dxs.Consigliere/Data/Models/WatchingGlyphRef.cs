namespace Dxs.Consigliere.Data.Models;

/// <summary>
/// A Radiant Glyph token ref the indexer watches, persisted so it survives
/// restarts (mirrors <see cref="WatchingToken"/> for BSV STAS). The ref is the
/// compact outpoint hex (32-byte txid LE + 4-byte vout LE).
/// </summary>
public class WatchingGlyphRef : AuditableEntity
{
    public string GlyphRef { get; init; }
    public string Name { get; init; }

    public override string GetId() => $"glyphRef/{GlyphRef}";

    public override IEnumerable<string> AllKeys()
    {
        foreach (var key in base.AllKeys())
            yield return key;

        yield return nameof(GlyphRef);
        yield return nameof(Name);
    }

    public override IList<string> UpdateableKeys() => EmptyKeys;

    public override IEnumerable<KeyValuePair<string, object>> ToEntries()
    {
        foreach (var entry in base.ToEntries())
            yield return entry;

        yield return new KeyValuePair<string, object>(nameof(GlyphRef), GlyphRef);
        yield return new KeyValuePair<string, object>(nameof(Name), Name);
    }
}
