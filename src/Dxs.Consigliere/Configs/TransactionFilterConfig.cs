namespace Dxs.Consigliere.Configs;

public class TransactionFilterConfig
{
    public string[] Addresses { get; set; } = [];
    public string[] Tokens { get; set; } = [];

    /// <summary>Radiant Glyph token refs to watch (compact outpoint hex: txid LE + vout LE).</summary>
    public string[] GlyphRefs { get; set; } = [];
}
