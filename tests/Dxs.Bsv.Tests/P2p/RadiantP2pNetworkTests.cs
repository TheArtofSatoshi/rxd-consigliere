using Dxs.Bsv.P2p;

using Xunit;

namespace Dxs.Bsv.Tests.P2p;

/// <summary>
/// Radiant P2P network variants + resolver. The on-wire magic bytes are the
/// handshake gate (FrameCodec rejects any frame whose magic != network.Magic),
/// so the byte order MUST be exactly the Radiant-Core netMagic. These vectors
/// are transcribed from Radiant-Core/src/chainparams*.cpp.
/// </summary>
public class RadiantP2pNetworkTests
{
    [Fact]
    public void RadiantMainnet_MagicBytes_And_Port()
    {
        var n = P2pNetwork.RadiantMainnet;
        // netMagic mainnet: e3 e1 f3 e8 (identical to BSV mainnet).
        Assert.Equal(new byte[] { 0xe3, 0xe1, 0xf3, 0xe8 }, n.MagicBytes);
        Assert.Equal(7333, n.DefaultPort);
    }

    [Fact]
    public void RadiantTestnet_MagicBytes_And_Port()
    {
        var n = P2pNetwork.RadiantTestnet;
        // netMagic testnet: f4 e5 f3 f4.
        Assert.Equal(new byte[] { 0xf4, 0xe5, 0xf3, 0xf4 }, n.MagicBytes);
        Assert.Equal(27333, n.DefaultPort);
    }

    [Fact]
    public void RadiantRegtest_MagicBytes_And_Port()
    {
        var n = P2pNetwork.RadiantRegtest;
        // netMagic regtest: da b5 bf fa.
        Assert.Equal(new byte[] { 0xda, 0xb5, 0xbf, 0xfa }, n.MagicBytes);
        Assert.Equal(18444, n.DefaultPort);
    }

    [Theory]
    [InlineData("mainnet", "mainnet")]
    [InlineData("radiant-mainnet", "radiant-mainnet")]
    [InlineData("RADIANT-MAINNET", "radiant-mainnet")]
    [InlineData("radiant-testnet", "radiant-testnet")]
    [InlineData("testnet", "radiant-testnet")]
    [InlineData("radiant-regtest", "radiant-regtest")]
    [InlineData("regtest", "radiant-regtest")]
    public void Resolve_KnownNames(string input, string expectedName)
    {
        var n = P2pNetwork.Resolve(input);
        Assert.NotNull(n);
        Assert.Equal(expectedName, n!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("litecoin")]
    public void Resolve_UnknownNames_ReturnNull(string input)
        => Assert.Null(P2pNetwork.Resolve(input));

    [Fact]
    public void RadiantMainnet_SharesBsvMagic_ButDifferentPort()
    {
        // Sanity: Radiant mainnet reuses BSV's wire magic (so the BSV-derived
        // frame codec just works) but listens on Radiant's 7333, not 8333.
        Assert.Equal(P2pNetwork.Mainnet.MagicBytes, P2pNetwork.RadiantMainnet.MagicBytes);
        Assert.NotEqual(P2pNetwork.Mainnet.DefaultPort, P2pNetwork.RadiantMainnet.DefaultPort);
    }
}
