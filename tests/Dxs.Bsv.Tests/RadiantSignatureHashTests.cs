using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

using Dxs.Bsv.Transactions.Build;

using Xunit;

namespace Dxs.Bsv.Tests;

/// <summary>
/// Radiant-specific hashOutputHashes (the extra BIP143 preimage field). Verifies
/// the structure transcribed from Radiant-Core primitives/transaction.h:
///   per output: nValue(8 LE) ‖ dSHA256(scriptPubKey) ‖ totalRefs(4 LE) ‖ refsHash
///   refsHash = zero32 if no refs, else dSHA256(sorted-distinct 36-byte refs)
///   result   = dSHA256(all per-output summaries)
/// where dSHA256 = SHA256(SHA256(x)) (CHashWriter.GetHash).
/// </summary>
public class RadiantSignatureHashTests
{
    private const byte OP_PUSHINPUTREF = 0xd0;
    private const byte OP_PUSHINPUTREFSINGLETON = 0xd8;

    private static byte[] DSha256(byte[] b) => SHA256.HashData(SHA256.HashData(b));

    private static byte[] LeU64(ulong v) => BitConverter.GetBytes(v); // x86/CI are little-endian
    private static byte[] LeU32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => p.Length)];
        var n = 0;
        foreach (var p in parts) { Array.Copy(p, 0, o, n, p.Length); n += p.Length; }
        return o;
    }

    private static byte[] P2Pkh() =>
        Concat(new byte[] { 0x76, 0xa9, 0x14 }, new byte[20], new byte[] { 0x88, 0xac });

    private static byte[] RefBytes(byte fill, uint vout)
    {
        var b = new byte[36];
        for (var i = 0; i < 32; i++) b[i] = fill;
        BitConverter.GetBytes(vout).CopyTo(b, 32);
        return b;
    }

    [Fact]
    public void SingleP2PkhOutput_NoRefs_MatchesManualConstruction()
    {
        ulong value = 12345;
        var script = P2Pkh();

        // Expected per-output summary: value ‖ dSHA256(script) ‖ totalRefs=0 ‖ zero32
        var summary = Concat(LeU64(value), DSha256(script), LeU32(0), new byte[32]);
        var expected = DSha256(summary);

        var actual = RadiantSignatureHash.HashOutputHashes(
            new (ulong, byte[])[] { (value, script) });

        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void OutputWithOneRef_RefsHashIsDSha256OfRef()
    {
        ulong value = 1000;
        var refb = RefBytes(0x11, 1);
        var script = Concat(new[] { OP_PUSHINPUTREF }, refb, P2Pkh());

        var refsHash = DSha256(refb);
        var summary = Concat(LeU64(value), DSha256(script), LeU32(1), refsHash);
        var expected = DSha256(summary);

        var actual = RadiantSignatureHash.HashOutputHashes(
            new (ulong, byte[])[] { (value, script) });

        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void Refs_AreSortedAscending_AsLittleEndianUint288_AndDeduped()
    {
        // Two distinct refs pushed in DESCENDING order + a duplicate of the first.
        // The summary must hash them sorted ascending (LE uint288) and deduped.
        var hi = RefBytes(0x80, 9); // larger most-significant bytes
        var lo = RefBytes(0x01, 2);
        ulong value = 7;
        var script = Concat(
            new[] { OP_PUSHINPUTREF }, hi,
            new[] { OP_PUSHINPUTREFSINGLETON }, lo,   // singleton refs ALSO count
            new[] { OP_PUSHINPUTREF }, hi);           // duplicate → deduped

        // sorted ascending LE uint288: lo (0x01..) before hi (0x80..)
        var refsHash = DSha256(Concat(lo, hi));
        var summary = Concat(LeU64(value), DSha256(script), LeU32(2), refsHash);
        var expected = DSha256(summary);

        var actual = RadiantSignatureHash.HashOutputHashes(
            new (ulong, byte[])[] { (value, script) });

        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void MultipleOutputs_AreConcatenatedInOrderThenHashed()
    {
        var o1 = ((ulong)100, P2Pkh());
        var refb = RefBytes(0x22, 0);
        var o2 = ((ulong)200, Concat(new[] { OP_PUSHINPUTREF }, refb, P2Pkh()));

        var s1 = Concat(LeU64(o1.Item1), DSha256(o1.Item2), LeU32(0), new byte[32]);
        var s2 = Concat(LeU64(o2.Item1), DSha256(o2.Item2), LeU32(1), DSha256(refb));
        var expected = DSha256(Concat(s1, s2));

        var actual = RadiantSignatureHash.HashOutputHashes(new[] { o1, o2 });

        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void DiffersFromPlainBsvOutputsHash()
    {
        // Sanity: Radiant's hashOutputHashes is NOT the same as the plain BSV
        // hashOutputs (value ‖ chunked-script). If they were equal, the extra
        // field would be pointless and the preimage wouldn't actually differ.
        ulong value = 50;
        var script = P2Pkh();
        var radiant = RadiantSignatureHash.HashOutputHashes(new (ulong, byte[])[] { (value, script) });

        // plain BSV hashOutputs for one output: dSHA256( value(8 LE) ‖ varint(len) ‖ script )
        var bsv = DSha256(Concat(LeU64(value), new byte[] { (byte)script.Length }, script));

        Assert.NotEqual(Convert.ToHexStringLower(bsv), Convert.ToHexStringLower(radiant));
    }
}
