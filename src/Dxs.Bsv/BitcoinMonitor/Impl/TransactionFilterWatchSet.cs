using System.Collections.Concurrent;
using System.Collections.Generic;

using Dxs.Bsv.Models;
using Dxs.Bsv.Script;
using Dxs.Common.Extensions;

namespace Dxs.Bsv.BitcoinMonitor.Impl;

internal sealed class TransactionFilterWatchSet
{
    private readonly ConcurrentDictionary<string, Address> _watchingAddresses = new();
    private readonly ConcurrentDictionary<string, TokenId> _watchingTokens = new();
    private readonly ConcurrentDictionary<string, Address> _watchingTokensRedeemAddresses = new();
    // Radiant Glyph: watch tokens by induction ref (compact outpoint hex).
    private readonly ConcurrentDictionary<string, byte> _watchingGlyphRefs = new();

    public int WatchingAddressesCount => _watchingAddresses.Count;
    public int WatchingTokensCount => _watchingTokens.Count;
    public int WatchingGlyphRefsCount => _watchingGlyphRefs.Count;

    public void AddAddress(Address address)
        => _watchingAddresses.TryAdd(address.Value, address);

    public void RemoveAddress(Address address)
        => _watchingAddresses.TryRemove(address.Value, out _);

    public void AddGlyphRef(string glyphRef)
    {
        if (!string.IsNullOrEmpty(glyphRef))
            _watchingGlyphRefs.TryAdd(glyphRef.ToLowerInvariant(), 0);
    }

    public void RemoveGlyphRef(string glyphRef)
    {
        if (!string.IsNullOrEmpty(glyphRef))
            _watchingGlyphRefs.TryRemove(glyphRef.ToLowerInvariant(), out _);
    }

    public void SeedGlyphRefs(IEnumerable<string> glyphRefs)
    {
        foreach (var glyphRef in glyphRefs)
            AddGlyphRef(glyphRef);
    }

    public void AddToken(TokenId tokenId)
    {
        _watchingTokens.TryAdd(tokenId.Value, tokenId);
        _watchingTokensRedeemAddresses.TryAdd(tokenId.RedeemAddress.Value, tokenId.RedeemAddress);
    }

    public void RemoveToken(TokenId tokenId)
    {
        _watchingTokens.TryRemove(tokenId.Value, out _);
        _watchingTokensRedeemAddresses.TryRemove(tokenId.RedeemAddress.Value, out _);
    }

    public void SeedAddresses(IEnumerable<Address> addresses)
    {
        foreach (var address in addresses)
            AddAddress(address);
    }

    public void SeedTokens(IEnumerable<TokenId> tokenIds)
    {
        foreach (var tokenId in tokenIds)
            AddToken(tokenId);
    }

    public TransactionFilterMatchResult Match(Transaction transaction)
    {
        var addresses = new HashSet<string>();
        var save = false;
        string redeemAddress = null;

        foreach (var output in transaction.Outputs)
        {
            if (output.Address?.Value is { } address)
            {
                if (_watchingAddresses.ContainsKey(address))
                {
                    save = true;
                    addresses.Add(address);
                }

                if (output.Idx == 0 && _watchingTokensRedeemAddresses.ContainsKey(address))
                {
                    save = true;
                    redeemAddress = address;
                }
            }

            if (output.Type is ScriptType.P2STAS or ScriptType.DSTAS &&
                output.TokenId.IsNotNullOrEmpty() &&
                _watchingTokens.ContainsKey(output.TokenId))
            {
                save = true;

                if (output.Address != null)
                    addresses.Add(output.Address.Value);
            }

            // Radiant Glyph: index outputs carrying a watched induction ref.
            if (!_watchingGlyphRefs.IsEmpty)
            {
                foreach (var refHex in output.GetGlyph(transaction).RefHexes)
                {
                    if (_watchingGlyphRefs.ContainsKey(refHex))
                    {
                        save = true;
                        if (output.Address?.Value is { } glyphAddress)
                            addresses.Add(glyphAddress);
                        break;
                    }
                }
            }
        }

        foreach (var input in transaction.Inputs)
        {
            if (input.Address == null) continue;

            if (!save && _watchingAddresses.ContainsKey(input.Address.Value))
                save = true;

            addresses.Add(input.Address.Value);
        }

        return new TransactionFilterMatchResult(save, redeemAddress, addresses.Count > 0 ? addresses : null);
    }
}

internal sealed record TransactionFilterMatchResult(bool Save, string RedeemAddress, HashSet<string> Addresses);
