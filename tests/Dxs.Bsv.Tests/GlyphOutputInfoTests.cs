using System;
using System.Formats.Cbor;
using System.Linq;

using Dxs.Bsv.Tokens.Glyph;

using Xunit;

namespace Dxs.Bsv.Tests;

public class GlyphOutputInfoTests
{
    private static byte[] FtRevealCbor()
    {
        var w = new CborWriter(CborConformanceMode.Lax, convertIndefiniteLengthEncodings: true);
        w.WriteStartMap(3);
        w.WriteTextString("p"); w.WriteStartArray(1); w.WriteInt32(1); w.WriteEndArray();
        w.WriteTextString("name"); w.WriteTextString("ConsigToken");
        w.WriteTextString("ticker"); w.WriteTextString("CSG");
        w.WriteEndMap();
        return w.Encode();
    }

    private const byte OP_PUSHINPUTREF = 0xd0;
    private const byte OP_PUSHINPUTREFSINGLETON = 0xd8;
    private static readonly byte[] Magic = "gly"u8.ToArray();

    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    private static byte[] Push(byte[] data) =>
        data.Length <= 0x4b ? Concat(new[] { (byte)data.Length }, data)
                            : Concat(new byte[] { 0x4c, (byte)data.Length }, data);

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
    public void PlainP2PkhIsNotGlyph()
    {
        var g = GlyphOutputInfo.FromScript(P2Pkh());
        Assert.False(g.HasRef);
        Assert.False(g.IsReveal);
        Assert.Null(g.PrimaryRefHex);
        Assert.Empty(g.Refs);
    }

    [Fact]
    public void EmptyScriptIsNotGlyph()
    {
        var g = GlyphOutputInfo.FromScript(ReadOnlySpan<byte>.Empty);
        Assert.False(g.HasRef);
        Assert.Empty(g.RefHexes);
    }

    [Fact]
    public void FungibleRefOutputExposesPrimaryRef()
    {
        var refb = RefBytes(0x11, 3);
        var script = Concat(new[] { OP_PUSHINPUTREF }, refb, P2Pkh());

        var g = GlyphOutputInfo.FromScript(script);
        Assert.True(g.HasRef);
        Assert.False(g.IsSingleton);
        Assert.Equal(Convert.ToHexStringLower(refb), g.PrimaryRefHex);
        Assert.Single(g.Refs);
    }

    [Fact]
    public void SingletonIsPreferredAsPrimaryRefEvenIfNotFirst()
    {
        // A normal ref appears first, a singleton second. The balance key
        // (PrimaryRef) must be the singleton.
        var normal = RefBytes(0x22, 1);
        var singleton = RefBytes(0x33, 2);
        var script = Concat(
            new[] { OP_PUSHINPUTREF }, normal,
            new[] { OP_PUSHINPUTREFSINGLETON }, singleton);

        var g = GlyphOutputInfo.FromScript(script);
        Assert.True(g.IsSingleton);
        Assert.Equal(Convert.ToHexStringLower(singleton), g.PrimaryRefHex);
        Assert.Equal(2, g.Refs.Count);
        // RefHexes preserves script order.
        Assert.Equal(Convert.ToHexStringLower(normal), g.RefHexes[0]);
        Assert.Equal(Convert.ToHexStringLower(singleton), g.RefHexes[1]);
    }

    [Fact]
    public void RevealOutputExposesTokenInfo()
    {
        // A realistic FT reveal: a normal ref guard then the 'gly' envelope.
        var cbor = FtRevealCbor();
        var script = Concat(new[] { OP_PUSHINPUTREF }, RefBytes(0x44, 0), Push(Magic), Push(cbor));

        var g = GlyphOutputInfo.FromScript(script);
        Assert.True(g.HasRef);
        Assert.True(g.IsReveal);
        Assert.NotNull(g.TokenInfo);
        Assert.Equal("ConsigToken", g.TokenInfo.Name);
        Assert.Equal("CSG", g.TokenInfo.Ticker);
        Assert.Equal(GlyphTokenType.Ft, g.TokenInfo.TokenType);
    }

    [Fact]
    public void RefWithoutEnvelopeHasNoTokenInfo()
    {
        var script = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(0x55, 0), P2Pkh());
        var g = GlyphOutputInfo.FromScript(script);

        Assert.True(g.HasRef);
        Assert.True(g.IsSingleton);
        Assert.False(g.IsReveal);
        Assert.Null(g.TokenInfo);
    }
}
