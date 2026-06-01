using System;
using System.Collections.Generic;
using System.Linq;

namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// The Glyph view of a single output's locking script: the induction refs it
/// carries (token identity) plus, when the script also embeds a Glyph envelope,
/// the parsed envelope/token metadata. This is the one place output-level Glyph
/// extraction lives, so the streaming indexer (<c>Output</c>) and the persisted
/// document model (<c>MetaOutput</c>) stay in agreement.
///
/// Token identity on Radiant is the ref in the scriptPubKey — there is no
/// Back-to-Genesis trace. A fungible (FT) output carries a normal ref
/// (OP_PUSHINPUTREF); a non-fungible (NFT/singleton) output carries a singleton
/// ref (OP_PUSHINPUTREFSINGLETON). The <see cref="PrimaryRef"/> is the value a
/// per-token balance should be keyed by: the singleton if present, else the
/// first ref.
/// </summary>
public sealed class GlyphOutputInfo
{
    private static readonly GlyphOutputInfo Empty = new()
    {
        Refs = Array.Empty<Ref>(),
        PrimaryRef = null,
        Envelope = null,
        TokenInfo = null,
    };

    /// <summary>All carried refs in script order (OP_PUSHINPUTREF / singleton).</summary>
    public IReadOnlyList<Ref> Refs { get; private init; }

    /// <summary>
    /// The ref a per-token balance should be keyed by: the first singleton ref
    /// if the output carries one, otherwise the first ref. Null if no refs.
    /// </summary>
    public Ref? PrimaryRef { get; private init; }

    /// <summary>Parsed Glyph envelope if the script embeds one ('gly' magic), else null.</summary>
    public GlyphEnvelope Envelope { get; private init; }

    /// <summary>Normalised token info if the script embeds a Glyph reveal, else null.</summary>
    public GlyphTokenInfo TokenInfo { get; private init; }

    /// <summary>True if the output carries at least one induction ref.</summary>
    public bool HasRef => PrimaryRef.HasValue;

    /// <summary>True if the output carries a singleton (NFT-style) ref.</summary>
    public bool IsSingleton => PrimaryRef is { IsSingleton: true };

    /// <summary>True if this output is the reveal that carries token metadata.</summary>
    public bool IsReveal => TokenInfo != null;

    /// <summary>
    /// Extract the Glyph view of an output locking script. Returns an empty
    /// (no-ref, no-envelope) instance for plain scripts such as P2PKH — cheap to
    /// call on every output. Never throws on malformed scripts.
    /// </summary>
    public static GlyphOutputInfo FromScript(ReadOnlySpan<byte> scriptPubKey)
    {
        if (scriptPubKey.IsEmpty)
            return Empty;

        var refs = RadiantScriptScanner.ExtractRefs(scriptPubKey);

        GlyphEnvelope envelope = null;
        GlyphTokenInfo tokenInfo = null;
        if (GlyphParser.ContainsMagic(scriptPubKey))
        {
            envelope = GlyphParser.ParseEnvelope(scriptPubKey);
            if (envelope != null)
                tokenInfo = GlyphParser.ParseTokenInfo(envelope);
        }

        if (refs.Count == 0 && envelope == null)
            return Empty;

        Ref? primary = null;
        if (refs.Count > 0)
        {
            // Prefer a singleton (NFT) ref for the balance key; else the first ref.
            primary = refs[0];
            foreach (var r in refs)
            {
                if (r.IsSingleton)
                {
                    primary = r;
                    break;
                }
            }
        }

        return new GlyphOutputInfo
        {
            Refs = refs,
            PrimaryRef = primary,
            Envelope = envelope,
            TokenInfo = tokenInfo,
        };
    }

    /// <summary>The primary ref's compact outpoint hex (txid LE + vout LE), or null.</summary>
    public string PrimaryRefHex => PrimaryRef?.OutpointHex;

    /// <summary>Every carried ref as compact outpoint hex, in script order.</summary>
    public IReadOnlyList<string> RefHexes => Refs.Select(r => r.OutpointHex).ToArray();
}
