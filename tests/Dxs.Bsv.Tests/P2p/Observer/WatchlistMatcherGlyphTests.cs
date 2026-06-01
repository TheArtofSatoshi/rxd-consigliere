using System;
using System.Collections.Generic;
using System.Linq;

using Dxs.Bsv.P2p.Observer;

using Xunit;

namespace Dxs.Bsv.Tests.P2p.Observer;

/// <summary>
/// Radiant Glyph-ref matching in the P2P thin-node watchlist matcher: the token
/// analog of the address/STAS-token tests in WatchlistMatcherTests. A Glyph ref
/// is a 72-char compact outpoint hex (32-byte txid LE + 4-byte vout LE).
/// </summary>
public class WatchlistMatcherGlyphTests
{
    private static byte[] Hash(byte fill)
    {
        var h = new byte[20];
        for (var i = 0; i < 20; i++) h[i] = fill;
        return h;
    }

    private static string Ref(byte fill, uint vout = 0)
    {
        var b = new byte[36];
        for (var i = 0; i < 32; i++) b[i] = fill;
        BitConverter.GetBytes(vout).CopyTo(b, 32);
        return Convert.ToHexStringLower(b);
    }

    private static ParsedTx Tx(
        string txid = "tx-1",
        IReadOnlyList<byte[]>? outputs = null,
        IReadOnlyList<byte[]>? inputs = null,
        IReadOnlyList<string>? tokens = null,
        IReadOnlyList<string>? glyphRefs = null)
        => new(
            txid,
            outputs ?? Array.Empty<byte[]>(),
            inputs ?? Array.Empty<byte[]>(),
            tokens ?? Array.Empty<string>(),
            glyphRefs ?? Array.Empty<string>());

    [Fact]
    public void Empty_Matcher_HasNoGlyphRefs()
    {
        var m = new WatchlistMatcher();
        Assert.False(m.HasAnyGlyphRefs);
        Assert.Equal(0, m.WatchedGlyphRefCount);
    }

    [Fact]
    public void AddGlyphRef_OutputHit_ReturnsGlyphHit()
    {
        var r = Ref(0x11, 1);
        var m = new WatchlistMatcher();
        m.AddGlyphRef(r);

        Assert.True(m.HasAnyGlyphRefs);
        Assert.Equal(1, m.WatchedGlyphRefCount);

        var result = m.Match(Tx(glyphRefs: new[] { r }));
        var hit = Assert.IsType<MatchResult.GlyphHit>(result);
        Assert.Single(hit.GlyphRefs);
        Assert.Equal(r, hit.GlyphRefs[0]);
    }

    [Fact]
    public void UnwatchedGlyphRef_ReturnsNone()
    {
        var m = new WatchlistMatcher();
        m.AddGlyphRef(Ref(0x22));
        Assert.Same(MatchResult.None.Instance, m.Match(Tx(glyphRefs: new[] { Ref(0x33) })));
    }

    [Fact]
    public void GlyphRefMatchIsCaseInsensitive()
    {
        var lower = Ref(0xab, 2);
        var upper = lower.ToUpperInvariant();
        var m = new WatchlistMatcher();
        m.AddGlyphRef(upper); // registered uppercase

        // ParsedTx refs are always lowercase (OutpointHex); must still match.
        var hit = Assert.IsType<MatchResult.GlyphHit>(m.Match(Tx(glyphRefs: new[] { lower })));
        Assert.Single(hit.GlyphRefs);
        Assert.True(m.IsWatchedGlyphRef(lower));
        Assert.True(m.IsWatchedGlyphRef(upper));
    }

    [Fact]
    public void RemoveGlyphRef_Idempotent()
    {
        var r = Ref(0x44);
        var m = new WatchlistMatcher();
        m.AddGlyphRef(r);
        m.RemoveGlyphRef(r);
        m.RemoveGlyphRef(r); // no throw
        Assert.False(m.HasAnyGlyphRefs);
        Assert.Same(MatchResult.None.Instance, m.Match(Tx(glyphRefs: new[] { r })));
    }

    [Fact]
    public void AddressAndGlyphRef_OnSameTx_ReturnsBothWithGlyphRefs()
    {
        var h = Hash(0x99);
        var r = Ref(0x55, 3);
        var m = new WatchlistMatcher();
        m.AddAddress(h);
        m.AddGlyphRef(r);

        var result = m.Match(Tx(outputs: new[] { h }, glyphRefs: new[] { r }));
        var both = Assert.IsType<MatchResult.Both>(result);
        Assert.Single(both.Hash160s);
        Assert.Empty(both.TokenIds);
        Assert.Single(both.GlyphRefs);
        Assert.Equal(r, both.GlyphRefs[0]);
    }

    [Fact]
    public void TokenAndGlyphRef_OnSameTx_ReturnsBoth()
    {
        var token = "542a56ec7a307fd68bf925d8f4d525ca61e868ad";
        var r = Ref(0x66, 4);
        var m = new WatchlistMatcher();
        m.AddToken(token);
        m.AddGlyphRef(r);

        var both = Assert.IsType<MatchResult.Both>(
            m.Match(Tx(tokens: new[] { token }, glyphRefs: new[] { r })));
        Assert.Empty(both.Hash160s);
        Assert.Single(both.TokenIds);
        Assert.Single(both.GlyphRefs);
    }

    [Fact]
    public void MultipleGlyphRefsOnOutput_OnlyWatchedAreHit()
    {
        var watched = Ref(0x77, 1);
        var other = Ref(0x88, 2);
        var m = new WatchlistMatcher();
        m.AddGlyphRef(watched);

        var hit = Assert.IsType<MatchResult.GlyphHit>(
            m.Match(Tx(glyphRefs: new[] { other, watched })));
        Assert.Single(hit.GlyphRefs);
        Assert.Equal(watched, hit.GlyphRefs[0]);
    }
}
