using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.Linq;
using System.Security.Cryptography;

using Dxs.Bsv.Tokens.Glyph;

using Xunit;

namespace Dxs.Bsv.Tests;

public class GlyphParserTests
{
    private static readonly byte[] Magic = "gly"u8.ToArray();

    // --- script construction helpers (mirror RXinDexer test builders) ---

    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    /// <summary>Encode a single data push (OP_PUSHBYTES_N / OP_PUSHDATA1/2).</summary>
    private static byte[] Push(byte[] data)
    {
        if (data.Length <= 0x4b)
            return Concat(new[] { (byte)data.Length }, data);
        if (data.Length <= 0xff)
            return Concat(new byte[] { 0x4c, (byte)data.Length }, data);
        return Concat(new byte[] { 0x4d, (byte)(data.Length & 0xff), (byte)(data.Length >> 8) }, data);
    }

    // CBOR map writer matching cbor2.dumps for our test maps.
    private static byte[] Cbor(Action<CborWriter> build)
    {
        var w = new CborWriter(CborConformanceMode.Lax, convertIndefiniteLengthEncodings: true);
        build(w);
        return w.Encode();
    }

    private static byte[] NftMetadata() => Cbor(w =>
    {
        w.WriteStartMap(3);
        w.WriteTextString("p"); w.WriteStartArray(1); w.WriteInt32(2); w.WriteEndArray();
        w.WriteTextString("name"); w.WriteTextString("TestNFT");
        w.WriteTextString("desc"); w.WriteTextString("A test NFT");
        w.WriteEndMap();
    });

    private static byte[] FtMetadata() => Cbor(w =>
    {
        w.WriteStartMap(4);
        w.WriteTextString("p"); w.WriteStartArray(1); w.WriteInt32(1); w.WriteEndArray();
        w.WriteTextString("name"); w.WriteTextString("TestFT");
        w.WriteTextString("ticker"); w.WriteTextString("TFT");
        w.WriteTextString("decimals"); w.WriteInt32(8);
        w.WriteEndMap();
    });

    private static byte[] DmintMetadata() => Cbor(w =>
    {
        w.WriteStartMap(3);
        w.WriteTextString("p"); w.WriteStartArray(2); w.WriteInt32(1); w.WriteInt32(4); w.WriteEndArray();
        w.WriteTextString("name"); w.WriteTextString("MineCoin");
        w.WriteTextString("dmint");
        w.WriteStartMap(2);
        w.WriteTextString("algo"); w.WriteInt32(1);
        w.WriteTextString("reward"); w.WriteInt32(50);
        w.WriteEndMap();
        w.WriteEndMap();
    });

    // --- v1 / Style B (standalone 'gly' push, then CBOR) ---

    [Fact]
    public void V1Reveal_StandaloneMagicThenCbor()
    {
        var script = Concat(Push(Magic), Push(NftMetadata()));

        var env = GlyphParser.ParseEnvelope(script);
        Assert.NotNull(env);
        Assert.True(env.IsReveal);
        Assert.Equal(GlyphVersion.V1, env.Version);

        var info = GlyphParser.ParseTokenInfo(env);
        Assert.NotNull(info);
        Assert.Equal("TestNFT", info.Name);
        Assert.Equal("A test NFT", info.Description);
        Assert.Equal(GlyphTokenType.Nft, info.TokenType);
        Assert.Contains(GlyphProtocol.Nft, info.Protocols);
    }

    [Fact]
    public void V2StyleB_Reveal_OP3ThenMagicThenCbor()
    {
        // OP_3 (0x53) delimiter, standalone 'gly', then CBOR.
        var script = Concat(new byte[] { 0x53 }, Push(Magic), Push(FtMetadata()));

        var info = GlyphParser.ParseTokenInfo(script);
        Assert.NotNull(info);
        Assert.Equal("TestFT", info.Name);
        Assert.Equal("TFT", info.Ticker);
        Assert.Equal(8, info.Decimals);
        Assert.Equal(GlyphTokenType.Ft, info.TokenType);
    }

    // --- v2 Style A (OP_RETURN, 'gly' concatenated with version+flags) ---

    [Fact]
    public void V2StyleA_Reveal_OpReturnConcatHeaderThenCbor()
    {
        // OP_RETURN, push('gly' || 0x02 || 0x80), push(CBOR)
        var header = Concat(Magic, new byte[] { 0x02, 0x80 });
        var script = Concat(new byte[] { 0x6a }, Push(header), Push(NftMetadata()));

        var env = GlyphParser.ParseEnvelope(script);
        Assert.NotNull(env);
        Assert.True(env.IsReveal);
        Assert.Equal(GlyphVersion.V2, env.Version);
        Assert.True(env.Flags.HasFlag(GlyphEnvelopeFlags.IsReveal));

        var info = GlyphParser.ParseTokenInfo(env);
        Assert.Equal("TestNFT", info.Name);
    }

    [Fact]
    public void V2StyleA_Commit_OpReturnInlineHash()
    {
        // OP_RETURN, push('gly' || 0x02 || 0x00 || 32-byte hash)
        var commitHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var body = Concat(Magic, new byte[] { 0x02, 0x00 }, commitHash);
        var script = Concat(new byte[] { 0x6a }, Push(body));

        var env = GlyphParser.ParseEnvelope(script);
        Assert.NotNull(env);
        Assert.False(env.IsReveal);
        Assert.Equal(GlyphVersion.V2, env.Version);
        Assert.Equal(Convert.ToHexStringLower(commitHash), env.CommitHash);
        Assert.Null(GlyphParser.ParseTokenInfo(env)); // commits carry no metadata
    }

    [Fact]
    public void V2StyleA_Commit_WithControllerFlag()
    {
        var commitHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var controller = Enumerable.Range(100, 36).Select(i => (byte)i).ToArray();
        var flags = (byte)(GlyphEnvelopeFlags.HasController); // 0x02, commit (no reveal bit)
        var body = Concat(Magic, new byte[] { 0x02, flags }, commitHash, controller);
        var script = Concat(new byte[] { 0x6a }, Push(body));

        var env = GlyphParser.ParseEnvelope(script);
        Assert.NotNull(env);
        Assert.False(env.IsReveal);
        Assert.Equal(Convert.ToHexStringLower(commitHash), env.CommitHash);
        Assert.Equal(Convert.ToHexStringLower(controller), env.Controller);
    }

    // --- commit hash relationship (REP-3003: hash = SHA256(canonical CBOR)) ---

    [Fact]
    public void CommitHashMatchesSha256OfRevealMetadata()
    {
        var metadata = FtMetadata();
        var expectedHash = SHA256.HashData(metadata);

        var body = Concat(Magic, new byte[] { 0x02, 0x00 }, expectedHash);
        var commitScript = Concat(new byte[] { 0x6a }, Push(body));
        var commitEnv = GlyphParser.ParseEnvelope(commitScript);

        Assert.Equal(Convert.ToHexStringLower(expectedHash), commitEnv.CommitHash);
    }

    // --- dMint ---

    [Fact]
    public void DmintTokenClassifiesAsDmintAndReadsConfig()
    {
        var script = Concat(Push(Magic), Push(DmintMetadata()));
        var info = GlyphParser.ParseTokenInfo(script);

        Assert.NotNull(info);
        Assert.Equal(GlyphTokenType.Dmint, info.TokenType);
        Assert.Contains(GlyphProtocol.Ft, info.Protocols);
        Assert.Contains(GlyphProtocol.Dmint, info.Protocols);
        Assert.NotNull(info.Dmint);
        Assert.Equal(1, info.Dmint.Algorithm);
        Assert.Equal(50, info.Dmint.Reward);
    }

    // --- v1 legacy `type` field inference ---

    [Fact]
    public void V1LegacyTypeFieldInfersProtocol()
    {
        var meta = Cbor(w =>
        {
            w.WriteStartMap(2);
            w.WriteTextString("type"); w.WriteTextString("ft");
            w.WriteTextString("name"); w.WriteTextString("LegacyCoin");
            w.WriteEndMap();
        });
        var script = Concat(Push(Magic), Push(meta));

        var info = GlyphParser.ParseTokenInfo(script);
        Assert.NotNull(info);
        Assert.Equal(GlyphTokenType.Ft, info.TokenType);
        Assert.Contains(GlyphProtocol.Ft, info.Protocols);
        Assert.Equal("LegacyCoin", info.Name);
    }

    // --- negative / robustness ---

    [Fact]
    public void NonGlyphScriptReturnsNull()
    {
        var p2pkh = Concat(new byte[] { 0x76, 0xa9, 0x14 }, new byte[20], new byte[] { 0x88, 0xac });
        Assert.Null(GlyphParser.ParseEnvelope(p2pkh));
        Assert.False(GlyphParser.ContainsMagic(p2pkh));
    }

    [Fact]
    public void RefOpcodesBeforeEnvelopeAreSkipped()
    {
        // A realistic mint reveal: an OP_PUSHINPUTREF + 36-byte ref precede the
        // 'gly' envelope. The push scanner must consume the ref's 36 bytes so the
        // envelope is still found.
        var refBytes = Enumerable.Range(0, 36).Select(i => (byte)i).ToArray();
        var script = Concat(
            new byte[] { 0xd0 }, refBytes,          // OP_PUSHINPUTREF + ref
            Push(Magic), Push(NftMetadata()));

        var info = GlyphParser.ParseTokenInfo(script);
        Assert.NotNull(info);
        Assert.Equal("TestNFT", info.Name);
    }

    [Fact]
    public void MagicThenNonMapGarbageIsNotAnEnvelope()
    {
        // 'gly' then a push that is neither a CBOR map nor a version header.
        // 0xff is a CBOR break / not a map start, and not a valid version byte,
        // so this must NOT be mis-detected as a Glyph reveal.
        var script = Concat(Push(Magic), Push(new byte[] { 0xff, 0xff }));
        var env = GlyphParser.ParseEnvelope(script);
        Assert.Null(env);
        Assert.Null(GlyphParser.ParseTokenInfo(script));
    }

    [Fact]
    public void MagicBytesEmbeddedInNonGlyphScriptDoNotFalsePositive()
    {
        // A script that merely *contains* the 3 magic bytes inside a larger data
        // push (not as a standalone 'gly' push) must not be read as a token.
        var blob = Concat("xxgly-not-an-envelope-just-bytes"u8.ToArray(), new byte[40]);
        var script = Push(blob);
        Assert.True(GlyphParser.ContainsMagic(script)); // magic present...
        Assert.Null(GlyphParser.ParseTokenInfo(script)); // ...but no token.
    }

    [Fact]
    public void ContainsMagicDetectsEmbeddedMagic()
    {
        var script = Concat(Push(Magic), Push(NftMetadata()));
        Assert.True(GlyphParser.ContainsMagic(script));
    }
}
