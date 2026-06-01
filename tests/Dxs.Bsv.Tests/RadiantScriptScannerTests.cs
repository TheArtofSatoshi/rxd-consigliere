using System;
using System.Linq;

using Dxs.Bsv.Tokens.Glyph;

using Xunit;

namespace Dxs.Bsv.Tests;

public class RadiantScriptScannerTests
{
    // Opcode bytes (from Radiant-Core/src/script/script.h).
    private const byte OP_PUSHINPUTREF = 0xd0;
    private const byte OP_REQUIREINPUTREF = 0xd1;            // +36 bytes, not a carried push
    private const byte OP_DISALLOWPUSHINPUTREF = 0xd2;       // +36 bytes, not a carried push
    private const byte OP_PUSHINPUTREFSINGLETON = 0xd8;      // +36 bytes, singleton (NFT)
    private const byte OP_STATESEPARATOR = 0xbd;             // no inline data
    private const byte OP_CODESCRIPTHASHOUTPUTCOUNT_UTXOS = 0xe5; // no inline data

    // 32-byte txid (internal/little-endian order) used across vectors.
    private static readonly byte[] TxId32 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static byte[] RefBytes(uint vout)
    {
        var b = new byte[36];
        Array.Copy(TxId32, 0, b, 0, 32);
        BitConverter.GetBytes(vout).CopyTo(b, 32); // little-endian vout
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    private static byte[] P2Pkh() =>
        Concat(new byte[] { 0x76, 0xa9, 0x14 }, new byte[20], new byte[] { 0x88, 0xac });

    [Fact]
    public void ExtractsSingleNormalRef()
    {
        var script = Concat(new[] { OP_PUSHINPUTREF }, RefBytes(1));
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Single(refs);
        Assert.False(refs[0].IsSingleton);
        Assert.Equal(1u, refs[0].Vout);
        Assert.Equal(Convert.ToHexStringLower(RefBytes(1)), refs[0].OutpointHex);
        Assert.True(RadiantScriptScanner.HasRef(script));
    }

    [Fact]
    public void ExtractsSingletonRef()
    {
        var script = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(7));
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Single(refs);
        Assert.True(refs[0].IsSingleton);
        Assert.Equal(7u, refs[0].Vout);
    }

    [Fact]
    public void TxIdIsBigEndianReversedFromOutpoint()
    {
        var script = Concat(new[] { OP_PUSHINPUTREF }, RefBytes(0));
        var r = RadiantScriptScanner.ExtractRefs(script).Single();

        // TxId32 is 00,01,...,1f → reversed big-endian hex is 1f,1e,...,00
        var expected = Convert.ToHexStringLower(TxId32.Reverse().ToArray());
        Assert.Equal(expected, r.TxId);
        Assert.Equal($"{expected}:0", r.Value);
    }

    [Fact]
    public void PlainP2PkhHasNoRefs()
    {
        var script = P2Pkh();
        Assert.Empty(RadiantScriptScanner.ExtractRefs(script));
        Assert.False(RadiantScriptScanner.HasRef(script));
    }

    [Fact]
    public void RefByteInsidePushedDataIsNotMistakenForRef()
    {
        // Push a single 0xd0 byte as data (0x01 0xd0). Must NOT be read as a ref.
        var script = new byte[] { 0x01, OP_PUSHINPUTREF };
        Assert.Empty(RadiantScriptScanner.ExtractRefs(script));
        Assert.False(RadiantScriptScanner.HasRef(script));

        // Push 36 bytes of data that contain ref opcode bytes — still not a ref.
        var data = new byte[36];
        data[0] = OP_PUSHINPUTREF;
        data[10] = OP_PUSHINPUTREFSINGLETON;
        var script2 = Concat(new byte[] { 0x24 }, data); // 0x24 = push 36 bytes
        Assert.Empty(RadiantScriptScanner.ExtractRefs(script2));
    }

    [Fact]
    public void ExtractsRefThenP2Pkh_SingletonNftOutputShape()
    {
        // Typical singleton NFT output: ref guard followed by a P2PKH payout.
        var script = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(0), P2Pkh());
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Single(refs);
        Assert.True(refs[0].IsSingleton);
    }

    [Fact]
    public void ExtractsMultipleRefsInScriptOrder()
    {
        var script = Concat(
            new[] { OP_PUSHINPUTREF }, RefBytes(1),
            new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(2));
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Equal(2, refs.Count);
        Assert.Equal(1u, refs[0].Vout);
        Assert.False(refs[0].IsSingleton);
        Assert.Equal(2u, refs[1].Vout);
        Assert.True(refs[1].IsSingleton);
    }

    [Fact]
    public void RefGuardsConsume36BytesButAreNotRecorded()
    {
        // OP_REQUIREINPUTREF (0xd1) and OP_DISALLOWPUSHINPUTREF (0xd2) each carry
        // a 36-byte ref but do NOT establish carried token identity. The scanner
        // must skip their payloads (or it desyncs) yet not record them, while a
        // genuine push-ref between them IS recorded.
        var script = Concat(
            new[] { OP_REQUIREINPUTREF }, RefBytes(9),
            new[] { OP_PUSHINPUTREF }, RefBytes(5),
            new[] { OP_DISALLOWPUSHINPUTREF }, RefBytes(8));
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Single(refs);
        Assert.Equal(5u, refs[0].Vout);
        Assert.False(refs[0].IsSingleton);
    }

    [Fact]
    public void SingletonGuardDoesNotDesyncScan()
    {
        // Regression: the singleton opcode (0xd8) must consume its 36 bytes.
        // If it were treated as a no-data op, the following ref bytes would be
        // misread as opcodes and a later genuine ref would be lost.
        var script = Concat(
            new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(1),
            new[] { OP_PUSHINPUTREF }, RefBytes(2));
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Equal(2, refs.Count);
        Assert.True(refs[0].IsSingleton);
        Assert.Equal(1u, refs[0].Vout);
        Assert.False(refs[1].IsSingleton);
        Assert.Equal(2u, refs[1].Vout);
    }

    [Fact]
    public void IntrospectionOpcodesConsumeNoInlineData()
    {
        // State separator + introspection ops around a ref must not desync.
        var script = Concat(
            new[] { OP_STATESEPARATOR },
            new[] { OP_PUSHINPUTREF }, RefBytes(3),
            new[] { OP_CODESCRIPTHASHOUTPUTCOUNT_UTXOS });
        var refs = RadiantScriptScanner.ExtractRefs(script);

        Assert.Single(refs);
        Assert.Equal(3u, refs[0].Vout);
    }

    [Fact]
    public void TruncatedRefIsHandledGracefully()
    {
        var script = Concat(new[] { OP_PUSHINPUTREF }, new byte[10]); // only 10 of 36 bytes
        Assert.Empty(RadiantScriptScanner.ExtractRefs(script));
        Assert.False(RadiantScriptScanner.HasRef(script));
    }

    [Fact]
    public void EmptyScriptYieldsNoRefs()
    {
        Assert.Empty(RadiantScriptScanner.ExtractRefs(ReadOnlySpan<byte>.Empty));
        Assert.False(RadiantScriptScanner.HasRef(ReadOnlySpan<byte>.Empty));
    }
}
