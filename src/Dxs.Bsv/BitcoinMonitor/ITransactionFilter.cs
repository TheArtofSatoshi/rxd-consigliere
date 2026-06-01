using System;

namespace Dxs.Bsv.BitcoinMonitor;

public interface ITransactionFilter : IDisposable
{
    void ManageUtxoSetForAddress(Address address);
    void ManageUtxoSetForToken(TokenId tokenId);

    /// <summary>
    /// Watch a Radiant Glyph token by its induction ref (compact outpoint hex,
    /// 32-byte txid LE + 4-byte vout LE = 72 hex chars). Transactions with an
    /// output carrying this ref will be indexed.
    /// </summary>
    void ManageUtxoSetForGlyphRef(string glyphRef);

    int QueueLength();
}
