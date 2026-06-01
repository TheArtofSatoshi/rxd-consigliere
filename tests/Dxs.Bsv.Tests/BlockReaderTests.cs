using System;
using System.IO;
using System.Linq;
using System.Text;

using Dxs.Bsv;
using Dxs.Bsv.Block;
using Dxs.Bsv.Rpc.Streams;

using Xunit;

namespace Dxs.Bsv.Tests;

/// <summary>
/// Regression coverage for block-path indexing against a Radiant node.
///
/// Radiant is a Bitcoin Core fork: its block header is the classic 80 bytes
/// (version | prevhash | merkleroot | time | bits | nonce) — no extra fields —
/// and txids are still double-SHA256. So <see cref="BlockReader"/> parses Radiant
/// blocks unchanged. The bug that broke ingestion lived one layer up: the node's
/// JSON-RPC <c>getblock &lt;hash&gt; 0</c> response is compact JSON
/// (<c>{"result":"&lt;hex&gt;",...}</c>, no space after the colon) but
/// <see cref="JsonRpcResultNetworkStream"/> scanned for the literal <c>{"result": "</c>
/// (with a space, the BSV formatting). The prefix never matched, zero payload bytes
/// were produced, and <c>BlockReader.ReadHeader</c> threw EndOfStreamException on the
/// first ReadUInt32Le.
///
/// Fixtures are real blocks fetched from the local Radiant regtest node (RPC 17443):
/// block 2 (coinbase only) and block 211 (coinbase + 1 spend).
/// </summary>
public class BlockReaderTests
{
    private const Network Net = Network.Testnet; // regtest shares testnet address bytes

    // --- Real regtest block 2 (coinbase only, nTx = 1) ---
    private const string Block2Hex =
        "00000020fd07d496241b25a346545798bbe5a1840ec4a3ed53b84f00379dd3c9b7c0d851" +
        "c257c1643684aa084ada5fdc8ac33ffdf76d158e1c995ea5504ceb269995ae99bd2f1a6a" +
        "ffff7f200000000001020000000100000000000000000000000000000000000000000000" +
        "00000000000000000000ffffffff0d520101092f45423235362e302fffffffff01005039" +
        "278c0400001976a9144e43cf43ebcb0bba1da0a79152e359d80df2ccb288ac00000000";

    private const uint Block2Version = 536870912;
    private const string Block2PrevHash = "51d8c0b7c9d39d37004fb853eda3c40e84a1e5bb98575446a3251b2496d407fd";
    private const string Block2MerkleRoot = "99ae959926eb4c50a55e991c8e156df7fd3fc38adc5fda4a08aa843664c157c2";
    private const long Block2Time = 1780101053;
    private const string Block2Bits = "207fffff";
    private const string Block2CoinbaseTxId = "99ae959926eb4c50a55e991c8e156df7fd3fc38adc5fda4a08aa843664c157c2";

    // --- Real regtest block 211 (coinbase + 1 spend, nTx = 2) ---
    private const string Block211Hex =
        "000000206fa6b706f892ed711e920cbefcd6d098000ca0d9b861dd3d5481784201e43847" +
        "f2afc4de5884ef024233d2709cc0c7729bfd4b2151bb0ef835008cd05da4335aa04d1a6a" +
        "ffff7f200000000002020000000100000000000000000000000000000000000000000000" +
        "00000000000000000000ffffffff0f02d3000101092f45423235362e302fffffffff01e8" +
        "16a013460200001976a914754a8f6f0a89c74598eb7c4bd0944385a08da2b488ac000000" +
        "000200000001a655763bf890989de0797481b57b9b431afe689eac1b4bb4d776eae1ac23" +
        "aed6000000006a47304402206935fccad33b93c76dd991f6a495c728174235bca4dcf275" +
        "79198bb7aab0aa330220779e442888656d9acda8fadf2404a5845fa06f0a7a3d1c00c272" +
        "7f42f71b1cbf41210394299259b37befc15245a74092a03a3fcd5fdc24084ed062e31884" +
        "b67d2683e4feffffff0200e87648170000001976a91455ff8f32c1a7e6a5664609e9f1e0" +
        "7ca396de3fb788ac18f9bede740400001976a914493bf9a9d44eda74d4cda7a1c51ec811" +
        "1bd61b7e88acd2000000";

    private const uint Block211Version = 536870912;
    private const string Block211PrevHash = "4738e401427881543ddd61b8d9a00c0098d0d6fcbe0c921e71ed92f806b7a66f";
    private const string Block211MerkleRoot = "5a33a45dd08c0035f80ebb51214bfd9b72c7c09c70d2334202ef8458dec4aff2";
    private const long Block211Time = 1780108704;
    private const string Block211Tx0Id = "a4ffa04645d7fd5061673f4febed464818731af1a1a329d50ba4c9537bda60a0";
    private const string Block211Tx1Id = "d2e80d939cd4fa0f8a5dd20f1f7f45be01d470c747e8d9907b94a39870165ba3";

    // ---------------------------------------------------------------------
    // BlockReader: Radiant headers are the standard 80 bytes, txids double-SHA256.
    // ---------------------------------------------------------------------

    [Fact]
    public void BlockReader_parses_coinbase_only_block()
    {
        using var reader = BlockReader.Parse(Block2Hex, Net);

        Assert.Equal(Block2Version, reader.Version);
        Assert.Equal(Block2PrevHash, Hex(reader.PrevBlockHash));          // ReadHeader reverses to display order
        Assert.Equal(Block2MerkleRoot, ReversedHex(reader.MerkleRoot));   // stored internal/LE order
        Assert.Equal(Block2Time, reader.Timestamp.ToUnixTimeSeconds());
        Assert.Equal(Block2Bits, reader.Bits);
        Assert.Equal(0u, reader.Nonce);
        Assert.Equal(1UL, reader.TransactionCount);

        var txs = reader.Transactions().ToList();
        Assert.Single(txs);
        // Single-tx block: merkleroot == coinbase txid. Confirms tx parse + txid hashing.
        Assert.Equal(Block2CoinbaseTxId, txs[0].Id);
    }

    [Fact]
    public void BlockReader_parses_multi_tx_block()
    {
        using var reader = BlockReader.Parse(Block211Hex, Net);

        Assert.Equal(Block211Version, reader.Version);
        Assert.Equal(Block211PrevHash, Hex(reader.PrevBlockHash));
        Assert.Equal(Block211MerkleRoot, ReversedHex(reader.MerkleRoot));
        Assert.Equal(Block211Time, reader.Timestamp.ToUnixTimeSeconds());
        Assert.Equal(2UL, reader.TransactionCount);

        var txs = reader.Transactions().ToList();
        Assert.Equal(2, txs.Count);
        Assert.Equal(Block211Tx0Id, txs[0].Id);
        Assert.Equal(Block211Tx1Id, txs[1].Id);
        // The spend has a real input + two outputs.
        Assert.Single(txs[1].Inputs);
        Assert.Equal(2, txs[1].Outputs.Count);
    }

    // ---------------------------------------------------------------------
    // JsonRpcResultNetworkStream: decode the "result" hex out of the RPC envelope.
    // This is where the actual bug was.
    // ---------------------------------------------------------------------

    [Fact]
    public void Stream_decodes_compact_radiant_envelope()
    {
        // Radiant / Bitcoin Core: no space after the colon. This is the regression.
        var envelope = Envelope("{\"result\":\"", Block2Hex);

        Assert.Equal(Block2Hex.FromHexString(), Decode(envelope));
    }

    [Fact]
    public void Stream_decodes_spaced_bsv_envelope()
    {
        // BSV upstream formatting (space after colon) must keep working.
        var envelope = Envelope("{\"result\": \"", Block2Hex);

        Assert.Equal(Block2Hex.FromHexString(), Decode(envelope));
    }

    [Fact]
    public void Stream_decodes_pretty_printed_envelope()
    {
        // Arbitrary JSON whitespace around the key/colon/value must be tolerated.
        var envelope = Envelope("{\n  \"result\" :  \"", Block2Hex);

        Assert.Equal(Block2Hex.FromHexString(), Decode(envelope));
    }

    [Fact]
    public void Stream_decodes_uppercase_hex()
    {
        // Hex casing must not matter (the Utf8ToHex map covers A-F and a-f).
        var envelope = Envelope("{\"result\":\"", Block2Hex.ToUpperInvariant());

        Assert.Equal(Block2Hex.FromHexString(), Decode(envelope));
    }

    // ---------------------------------------------------------------------
    // End-to-end: exactly the production path — raw RPC envelope bytes ->
    // JsonRpcResultNetworkStream -> BlockReader.Parse(stream). This is what
    // NodeBlockchainDataProvider.ProcessBlock does, and what used to throw
    // EndOfStreamException on every Radiant block.
    // ---------------------------------------------------------------------

    [Fact]
    public void EndToEnd_rpc_envelope_streams_into_blockreader()
    {
        var envelope = Envelope("{\"result\":\"", Block211Hex);

        using var rpcStream = new JsonRpcResultNetworkStream(new MemoryStream(envelope));
        using var reader = BlockReader.Parse(rpcStream, Net);

        Assert.Equal(Block211Version, reader.Version);
        Assert.Equal(Block211PrevHash, Hex(reader.PrevBlockHash));
        Assert.Equal(2UL, reader.TransactionCount);

        var txs = reader.Transactions().ToList();
        Assert.Equal(new[] { Block211Tx0Id, Block211Tx1Id }, txs.Select(t => t.Id).ToArray());
    }

    // ---------------------------------------------------------------------
    // Error surfacing: the stream must raise a descriptive RpcResponseException
    // instead of swallowing the failure and reporting a clean end-of-stream. A
    // silent EOF is exactly what disguised the original compact-JSON bug and what
    // would let a truncated large block read corrupt indexing unnoticed.
    // ---------------------------------------------------------------------

    [Fact]
    public void Stream_surfaces_rpc_error_envelope()
    {
        // `getblock <bad-hash> 0` -> {"result":null,"error":{...}}. The real error
        // message must reach the exception, not collapse into EndOfStreamException.
        var envelope = Encoding.ASCII.GetBytes(
            "{\"result\":null,\"error\":{\"code\":-5,\"message\":\"Block not found\"},\"id\":\"t\"}");

        var ex = Assert.Throws<RpcResponseException>(() => Decode(envelope));
        Assert.Contains("not a string", ex.Message);
        Assert.Contains("Block not found", ex.Message); // the node's error is surfaced verbatim
    }

    [Fact]
    public void Stream_surfaces_non_string_result()
    {
        // A non-string, non-null result (e.g. a number) is still malformed for getblock.
        var envelope = Encoding.ASCII.GetBytes("{\"result\":12345,\"error\":null,\"id\":\"t\"}");

        var ex = Assert.Throws<RpcResponseException>(() => Decode(envelope));
        Assert.Contains("not a string", ex.Message);
    }

    [Fact]
    public void Stream_surfaces_truncated_payload()
    {
        // Payload string opens and some hex arrives, then the stream ends with no
        // closing quote — a transport cut mid-block. Must NOT look like a clean EOF.
        var envelope = Encoding.ASCII.GetBytes("{\"result\":\"" + Block2Hex[..40]);

        var ex = Assert.Throws<RpcResponseException>(() => Decode(envelope));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void Stream_surfaces_odd_hex_payload()
    {
        // Closing quote lands after an odd number of hex chars: a corrupt/half-byte
        // payload, not a valid empty/complete one.
        var envelope = Encoding.ASCII.GetBytes("{\"result\":\"abc\",\"error\":null,\"id\":\"t\"}");

        var ex = Assert.Throws<RpcResponseException>(() => Decode(envelope));
        Assert.Contains("half-byte", ex.Message);
    }

    [Fact]
    public void EndToEnd_error_envelope_throws_RpcResponseException_not_EndOfStream()
    {
        // The exact production path: an error envelope fed through
        // JsonRpcResultNetworkStream -> BlockReader.Parse. Before the fix this threw a
        // bare EndOfStreamException from ReadHeader with no hint of the real cause.
        var envelope = Encoding.ASCII.GetBytes(
            "{\"result\":null,\"error\":{\"code\":-5,\"message\":\"Block not found\"},\"id\":\"t\"}");

        var rpcStream = new JsonRpcResultNetworkStream(new MemoryStream(envelope));

        var ex = Assert.Throws<RpcResponseException>(() => BlockReader.Parse(rpcStream, Net));
        Assert.Contains("Block not found", ex.Message);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>Builds a JSON-RPC response envelope: &lt;prefix&gt;&lt;hex&gt;","error":null,"id":"t"}</summary>
    private static byte[] Envelope(string prefix, string resultHex)
        => Encoding.ASCII.GetBytes($"{prefix}{resultHex}\",\"error\":null,\"id\":\"t\"}}");

    /// <summary>Drains a <see cref="JsonRpcResultNetworkStream"/> over the envelope into the decoded payload bytes.</summary>
    private static byte[] Decode(byte[] envelope)
    {
        using var stream = new JsonRpcResultNetworkStream(new MemoryStream(envelope));
        using var ms = new MemoryStream();

        var buf = new byte[64]; // small chunks to exercise the ring buffer / partial reads
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, n);

        return ms.ToArray();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string ReversedHex(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        Array.Reverse(copy);
        return Hex(copy);
    }
}
