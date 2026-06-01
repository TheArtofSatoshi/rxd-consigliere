using System.Collections.Generic;
using System.Threading.Tasks;

using Dxs.Bsv.BitcoinMonitor.Models;
using Dxs.Bsv.Models;

namespace Dxs.Bsv.BitcoinMonitor;

public interface ITransactionStore
{
    Task<List<Address>> GetWatchingAddresses();

    Task<List<TokenId>> GetWatchingTokens();

    /// <summary>Radiant Glyph refs to watch (compact outpoint hex), from config + DB.</summary>
    Task<List<string>> GetWatchingGlyphRefs();

    Task<TransactionProcessStatus> SaveTransaction(
        Transaction transaction,
        long timestamp,
        string firstOutToRedeem,
        string blockHash = null,
        int? blockHeight = null,
        int? indexInBlock = null
    );

    Task<Transaction> TryRemoveTransaction(string id);
}
