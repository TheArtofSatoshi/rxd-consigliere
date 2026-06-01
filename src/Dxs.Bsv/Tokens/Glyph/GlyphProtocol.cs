namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>Glyph envelope versions (the CBOR `v` field / v2 inline version byte).</summary>
public static class GlyphVersion
{
    public const int V1 = 0x01;
    public const int V2 = 0x02;
}

/// <summary>
/// Glyph protocol IDs (the values in the CBOR `p` array). Mirrors
/// RXinDexer `electrumx/lib/glyph.py` GlyphProtocol.
/// </summary>
public enum GlyphProtocol
{
    Ft = 1,         // Fungible Token
    Nft = 2,        // Non-Fungible Token
    Dat = 3,        // Data Storage
    Dmint = 4,      // Decentralized Minting
    Mut = 5,        // Mutable State
    Burn = 6,       // Explicit Burn
    Container = 7,  // Container/Collection
    Encrypted = 8,  // Encrypted Content
    Timelock = 9,   // Timelocked Reveal
    Authority = 10, // Issuer Authority
    Wave = 11,      // WAVE Naming
}

/// <summary>Stable token-type classification derived from the protocol set.</summary>
public enum GlyphTokenType
{
    Unknown = 0,
    Ft = 1,
    Nft = 2,
    Dat = 3,
    Dmint = 4,
    Wave = 5,
    Container = 6,
    Authority = 7,
}

/// <summary>Glyph v2 envelope flag bits (the inline `flags` byte).</summary>
[System.Flags]
public enum GlyphEnvelopeFlags : byte
{
    None = 0,
    HasContentRoot = 1 << 0,
    HasController = 1 << 1,
    HasProfileHint = 1 << 2,
    IsReveal = 1 << 7,
}

public static class GlyphTokenTypeExtensions
{
    /// <summary>
    /// Maps a protocol set to a stable token-type ID. Mirrors
    /// RXinDexer `get_token_type_id`: FT (+dMint) wins, then NFT specializations
    /// (WAVE &gt; Container &gt; Authority &gt; NFT), then DAT.
    /// </summary>
    public static GlyphTokenType ToTokenType(this System.Collections.Generic.IReadOnlyCollection<GlyphProtocol> protocols)
    {
        if (protocols == null || protocols.Count == 0)
            return GlyphTokenType.Unknown;

        bool Has(GlyphProtocol p) => System.Linq.Enumerable.Contains(protocols, p);

        if (Has(GlyphProtocol.Ft))
            return Has(GlyphProtocol.Dmint) ? GlyphTokenType.Dmint : GlyphTokenType.Ft;

        if (Has(GlyphProtocol.Nft))
        {
            if (Has(GlyphProtocol.Wave)) return GlyphTokenType.Wave;
            if (Has(GlyphProtocol.Container)) return GlyphTokenType.Container;
            if (Has(GlyphProtocol.Authority)) return GlyphTokenType.Authority;
            return GlyphTokenType.Nft;
        }

        if (Has(GlyphProtocol.Dat))
            return GlyphTokenType.Dat;

        return GlyphTokenType.Unknown;
    }

    public static string ToDisplayName(this GlyphTokenType type) => type switch
    {
        GlyphTokenType.Ft => "ft",
        GlyphTokenType.Nft => "nft",
        GlyphTokenType.Dat => "dat",
        GlyphTokenType.Dmint => "dmint",
        GlyphTokenType.Wave => "wave",
        GlyphTokenType.Container => "container",
        GlyphTokenType.Authority => "authority",
        _ => "unknown",
    };
}
