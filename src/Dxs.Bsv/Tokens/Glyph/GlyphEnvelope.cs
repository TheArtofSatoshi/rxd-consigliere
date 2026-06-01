namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// A parsed Glyph envelope (the result of <see cref="GlyphParser.ParseEnvelope"/>).
/// Mirrors the dict returned by RXinDexer `parse_glyph_envelope`.
/// </summary>
public sealed class GlyphEnvelope
{
    /// <summary>Envelope version (1 or 2). For CBOR reveals this is the `v` field.</summary>
    public int Version { get; init; }

    /// <summary>Raw v2 flags byte (synthesised as IsReveal for CBOR-detected reveals).</summary>
    public GlyphEnvelopeFlags Flags { get; init; }

    /// <summary>True for a reveal (metadata present); false for a commit (hash only).</summary>
    public bool IsReveal { get; init; }

    /// <summary>The CBOR metadata bytes, when this is a reveal. Null for commits.</summary>
    public byte[] MetadataBytes { get; init; }

    /// <summary>SHA256 commit hash hex, when this is a commit. Null for reveals.</summary>
    public string CommitHash { get; init; }

    /// <summary>Optional v2 commit content-root hash hex (flag HasContentRoot).</summary>
    public string ContentRoot { get; init; }

    /// <summary>Optional v2 commit controller outpoint hex (flag HasController).</summary>
    public string Controller { get; init; }

    /// <summary>Reveal file-chunk pushes following the metadata push (v2 Style A).</summary>
    public System.Collections.Generic.IReadOnlyList<byte[]> FileChunks { get; init; }
}
