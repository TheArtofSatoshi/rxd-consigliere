#nullable enable
using System.Collections.Generic;

namespace Dxs.Bsv.P2p.Observer;

/// <summary>
/// Outcome of <see cref="WatchlistMatcher.Match"/>. Discriminated
/// record so the consumer (W2 S4 mempool watcher) can branch on
/// hit / miss without null-checking lists.
/// </summary>
public abstract record MatchResult
{
    public sealed record None : MatchResult
    {
        public static readonly None Instance = new();
        private None() { }
    }

    public sealed record AddressHit(IReadOnlyList<byte[]> Hash160s) : MatchResult;

    public sealed record TokenHit(IReadOnlyList<string> TokenIds) : MatchResult;

    /// <summary>One or more outputs carried a watched Radiant Glyph ref
    /// (compact outpoint hex). The Radiant token analog of <see cref="TokenHit"/>.</summary>
    public sealed record GlyphHit(IReadOnlyList<string> GlyphRefs) : MatchResult;

    /// <summary>
    /// A composite hit spanning more than one category: watched address +
    /// STAS/DSTAS token + Radiant Glyph ref. Lists for categories that did not
    /// hit are empty (never null) so consumers can enumerate without null checks.
    /// (<see cref="GlyphRefs"/> was added for Radiant; the address+token tests
    /// read <see cref="Hash160s"/>/<see cref="TokenIds"/> by name and are
    /// unaffected.)
    /// </summary>
    public sealed record Both(
        IReadOnlyList<byte[]> Hash160s,
        IReadOnlyList<string> TokenIds,
        IReadOnlyList<string> GlyphRefs) : MatchResult;
}
