using System;
using System.Linq;

using Dxs.Bsv.P2p.Observer;

using Xunit;

namespace Dxs.Bsv.Tests.P2p.Observer;

/// <summary>
/// TxScriptParser.TryParseGlyphRefs — extracts Radiant Glyph induction refs from
/// an output locking script (the P2P thin-node analog of TryParseTokenId).
/// </summary>
public class TxScriptParserGlyphTests
{
    private const byte OP_PUSHINPUTREF = 0xd0;
    private const byte OP_PUSHINPUTREFSINGLETON = 0xd8;

    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    private static byte[] RefBytes(byte fill, uint vout)
    {
        var b = new byte[36];
        for (var i = 0; i < 32; i++) b[i] = fill;
        BitConverter.GetBytes(vout).CopyTo(b, 32);
        return b;
    }

    private static byte[] P2Pkh() =>
        Concat(new byte[] { 0x76, 0xa9, 0x14 }, new byte[20], new byte[] { 0x88, 0xac });

    [Fact]
    public void PlainP2Pkh_NoGlyphRefs()
    {
        Assert.False(TxScriptParser.TryParseGlyphRefs(P2Pkh(), out var refs));
        Assert.Empty(refs);
    }

    [Fact]
    public void EmptyScript_NoGlyphRefs()
    {
        Assert.False(TxScriptParser.TryParseGlyphRefs(ReadOnlySpan<byte>.Empty, out var refs));
        Assert.Empty(refs);
    }

    [Fact]
    public void NormalRefOutput_YieldsCompactOutpointHex()
    {
        var refb = RefBytes(0x11, 1);
        var script = Concat(new[] { OP_PUSHINPUTREF }, refb, P2Pkh());

        Assert.True(TxScriptParser.TryParseGlyphRefs(script, out var refs));
        Assert.Single(refs);
        Assert.Equal(Convert.ToHexStringLower(refb), refs[0]);
    }

    [Fact]
    public void SingletonRefOutput_IsExtracted()
    {
        var refb = RefBytes(0x22, 7);
        var script = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, refb, P2Pkh());

        Assert.True(TxScriptParser.TryParseGlyphRefs(script, out var refs));
        Assert.Single(refs);
        Assert.Equal(Convert.ToHexStringLower(refb), refs[0]);
    }

    [Fact]
    public void MultipleRefs_AllExtractedInScriptOrder()
    {
        var a = RefBytes(0x33, 1);
        var b = RefBytes(0x44, 2);
        var script = Concat(
            new[] { OP_PUSHINPUTREF }, a,
            new[] { OP_PUSHINPUTREFSINGLETON }, b);

        Assert.True(TxScriptParser.TryParseGlyphRefs(script, out var refs));
        Assert.Equal(2, refs.Count);
        Assert.Equal(Convert.ToHexStringLower(a), refs[0]);
        Assert.Equal(Convert.ToHexStringLower(b), refs[1]);
    }
}
