using System.Collections.Generic;

using Dxs.Bsv.Protocol;
using Dxs.Bsv.Script;
using Dxs.Bsv.Script.Build;
using Dxs.Bsv.Script.Read;
using Dxs.Bsv.Tokens.Glyph;

namespace Dxs.Bsv.Models;

public class Output
{
    private IReadOnlyList<ScriptBuildToken> _scriptTokens;
    private GlyphOutputInfo _glyph;

    public ulong Satoshis { get; set; }

    public Slice ScriptPubKey { get; init; }

    public Address Address { get; init; }

    public ScriptType Type { get; init; }

    public string TokenId { get; init; }

    public ulong Idx { get; set; }

    public byte[] GetScriptBytes(Transaction transaction) => ScriptPubKey.Materialize(transaction.Raw);

    public IReadOnlyList<ScriptBuildToken> GetScriptTokens(Transaction transaction)
    {
        if (_scriptTokens != null)
            return _scriptTokens;

        var bytes = GetScriptBytes(transaction);
        _scriptTokens = SimpleScriptReader.Read(bytes, transaction.Network);

        return _scriptTokens;
    }

    /// <summary>
    /// The Glyph view of this output's locking script (induction refs + any
    /// embedded envelope/token metadata). Computed lazily from the script bytes
    /// and cached. Returns an empty instance for non-Glyph outputs (e.g. P2PKH).
    /// </summary>
    public GlyphOutputInfo GetGlyph(Transaction transaction)
        => _glyph ??= GlyphOutputInfo.FromScript(GetScriptBytes(transaction));

    public static Output Parse(BitcoinStreamReader bitcoinStreamReader, int txStartPosition, int length, ulong value, ulong idx, Network network)
    {
        var startPosition = bitcoinStreamReader.Position;
        var reader = LockingScriptReader.Read(bitcoinStreamReader, length, network);

        //bitcoinStreamReader.ReadNBytes((uint)length);

        return new Output
        {
            Address = reader.Address,
            Type = reader.ScriptType,
            Satoshis = value,
            Idx = idx,
            ScriptPubKey = new()
            {
                Length = length,
                Start = startPosition - txStartPosition
            },
            TokenId = reader.GetTokenId()
        };
    }
}
