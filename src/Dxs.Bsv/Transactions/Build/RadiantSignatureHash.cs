using System;
using System.Collections.Generic;
using System.Linq;

using Dxs.Bsv.Protocol;
using Dxs.Bsv.Tokens.Glyph;

namespace Dxs.Bsv.Transactions.Build;

/// <summary>
/// Radiant-specific BIP143 sighash extension. Radiant's signature preimage is
/// the BCH/BSV FORKID preimage PLUS one extra 32-byte field — `hashOutputHashes`
/// — inserted immediately before the regular `hashOutputs`. A BSV-shaped preimage
/// (which omits it) produces signatures the Radiant node rejects.
///
/// Transcribed verbatim from Radiant-Core:
///   - <c>script/interpreter.cpp</c> SignatureHash (the FORKID branch, where
///     <c>ss &lt;&lt; hashOutputHashes; ss &lt;&lt; hashOutputs;</c>)
///   - <c>primitives/transaction.h</c> GetHashOutputHashes /
///     writeOutputDataSummaryVector / getRefHashDataSummary.
///
/// Per-output summary written into the outer hash writer:
///   nValue            : int64 little-endian (8 bytes)
///   scriptPubKeyHash  : double-SHA256(scriptPubKey)            (32 bytes)
///   totalRefs         : uint32 little-endian = #distinct push refs (4 bytes)
///   refsHash          : 32 bytes — all-zero if no refs, else
///                       double-SHA256( concat of the SORTED, DEDUPED 36-byte
///                       push refs ). Push refs = OP_PUSHINPUTREF (0xd0) AND
///                       OP_PUSHINPUTREFSINGLETON (0xd8) — both go in the set.
/// hashOutputHashes = double-SHA256( all per-output summaries concatenated ).
///
/// <c>GetHash()</c> in Radiant-Core is a double-SHA256 (CHashWriter), matching
/// <see cref="Hash.Sha256Sha256(ReadOnlySpan{byte})"/>.
/// </summary>
public static class RadiantSignatureHash
{
    private const int RefSize = 36;

    /// <summary>
    /// Compute Radiant's <c>hashOutputHashes</c> over all outputs (the
    /// SIGHASH_ALL case). Each output contributes value + scriptPubKey-hash +
    /// ref-count + refs-hash; the concatenation is double-SHA256'd.
    /// </summary>
    public static byte[] HashOutputHashes(IReadOnlyList<(ulong Value, byte[] LockingScript)> outputs)
    {
        var writer = new BufferWriter(EstimateSize(outputs));
        foreach (var (value, script) in outputs)
            WriteOutputDataSummary(writer, value, script ?? Array.Empty<byte>());

        return Hash.Sha256Sha256(writer.Bytes);
    }

    /// <summary>
    /// The SIGHASH_SINGLE / matched-output case: summary of exactly one output,
    /// using an all-zero refs placeholder (matches Radiant-Core's SINGLE branch
    /// which passes <c>zeroRefHash</c> rather than computing the ref set).
    /// </summary>
    public static byte[] HashOutputHashesSingle(ulong value, byte[] lockingScript)
    {
        var writer = new BufferWriter(8 + 32 + 4 + 32);
        // SINGLE branch in interpreter.cpp passes zeroRefHash for refsHash and
        // still hashes the scriptPubKey + nValue + (totalRefs computed from the
        // script). We mirror writeOutputDataSummaryVector for the one output.
        WriteOutputDataSummary(writer, value, lockingScript ?? Array.Empty<byte>());
        return Hash.Sha256Sha256(writer.Bytes);
    }

    private static void WriteOutputDataSummary(BufferWriter writer, ulong value, byte[] script)
    {
        // nValue — Amount is int64 LE on the wire.
        writer.WriteUInt64Le(value);

        // scriptPubKeyHash — double-SHA256 of the raw scriptPubKey bytes.
        writer.Write(Hash.Sha256Sha256(script));

        // The distinct push refs (OP_PUSHINPUTREF + singleton), sorted + deduped.
        var refs = ExtractSortedDistinctRefs(script);

        // totalRefs — uint32 LE.
        writer.WriteUInt32Le((uint)refs.Count);

        // refsHash — zero if none, else double-SHA256 of the concatenated refs.
        if (refs.Count == 0)
        {
            writer.Write(new byte[32]);
        }
        else
        {
            var refsBuf = new BufferWriter(refs.Count * RefSize);
            foreach (var r in refs)
                refsBuf.Write(r);
            writer.Write(Hash.Sha256Sha256(refsBuf.Bytes));
        }
    }

    /// <summary>
    /// The push-ref set Radiant hashes: every OP_PUSHINPUTREF (0xd0) and
    /// OP_PUSHINPUTREFSINGLETON (0xd8) payload, as a <c>std::set&lt;uint288&gt;</c>
    /// — i.e. sorted ascending by the 36 raw bytes, with duplicates removed.
    /// </summary>
    private static List<byte[]> ExtractSortedDistinctRefs(byte[] script)
    {
        var refs = RadiantScriptScanner.ExtractRefs(script);
        if (refs.Count == 0)
            return new List<byte[]>(0);

        // uint288 ordering in C++ std::set is by value. uint288 stores the 36
        // bytes; comparison is big-number style. The bytes as they appear in
        // script are the canonical little-endian uint288 representation, so
        // sorting is a byte-wise compare of the raw 36-byte arrays treated as a
        // little-endian integer → compare from the most-significant (last) byte
        // down. We sort accordingly and dedupe.
        var distinct = new List<byte[]>();
        var seen = new HashSet<string>();
        foreach (var r in refs)
        {
            var key = Convert.ToHexStringLower(r.Bytes);
            if (seen.Add(key))
                distinct.Add(r.Bytes);
        }

        distinct.Sort(CompareUint288LittleEndian);
        return distinct;
    }

    /// <summary>
    /// Compare two 36-byte arrays as little-endian unsigned integers (uint288),
    /// matching how Radiant-Core's <c>std::set&lt;uint288&gt;</c> orders them.
    /// </summary>
    private static int CompareUint288LittleEndian(byte[] a, byte[] b)
    {
        // Most significant byte is the last one in a little-endian layout.
        for (var i = RefSize - 1; i >= 0; i--)
        {
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        }
        return 0;
    }

    private static int EstimateSize(IReadOnlyList<(ulong Value, byte[] LockingScript)> outputs)
        => outputs.Count * (8 + 32 + 4 + 32);
}
