using System.Collections.Generic;

namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// Normalised token info extracted from decoded Glyph reveal metadata. Mirrors
/// RXinDexer `extract_token_info` (handles both v1 `type` and v2 `p` formats).
/// </summary>
public sealed class GlyphTokenInfo
{
    public int Version { get; init; }
    public IReadOnlyList<GlyphProtocol> Protocols { get; init; } = System.Array.Empty<GlyphProtocol>();
    public GlyphTokenType TokenType { get; init; }
    public string TokenTypeName { get; init; }

    public string Name { get; init; }
    public string Ticker { get; init; }
    public long? Decimals { get; init; }
    public string Description { get; init; }

    public GlyphDmintInfo Dmint { get; init; }
}

/// <summary>dMint config subset, mirroring `extract_token_info`'s `dmint` dict.</summary>
public sealed class GlyphDmintInfo
{
    public long? Algorithm { get; init; }
    public long? MaxHeight { get; init; }
    public long? Reward { get; init; }
    public byte[] Target { get; init; }
}
