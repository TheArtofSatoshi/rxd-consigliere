#nullable enable
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Dxs.Bsv.P2p;
using Dxs.Bsv.P2p.Messages;
using Dxs.Bsv.P2p.Session;

using Xunit;

namespace Dxs.Bsv.Tests.P2p;

/// <summary>
/// Opt-in integration test: drives the REAL P2P stack
/// (<see cref="PeerSession.ConnectAsync"/>) against a live Radiant node, proving
/// the Radiant network magic + handshake actually peer with radiantd — the thing
/// unit tests (which use MiniBsvServer) can't prove.
///
/// Skipped (returns early) unless RADIANT_P2P_ENDPOINT is set, keeping the normal
/// unit run hermetic. Run it via Docker pointed at the host regtest node:
///   docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
///     -e RADIANT_P2P_ENDPOINT=host.docker.internal:18444 \
///     -e RADIANT_P2P_NETWORK=radiant-regtest \
///     -v $PWD:/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 \
///     dotnet test tests/Dxs.Bsv.Tests/Dxs.Bsv.Tests.csproj \
///       --filter FullyQualifiedName~LiveRadiantP2pHandshakeTests
/// </summary>
public class LiveRadiantP2pHandshakeTests
{
    private static string? Endpoint => Environment.GetEnvironmentVariable("RADIANT_P2P_ENDPOINT");
    private static string NetworkName =>
        Environment.GetEnvironmentVariable("RADIANT_P2P_NETWORK") ?? "radiant-regtest";

    private static VersionMessage OurVersion() =>
        new(
            ProtocolVersion: 70016,
            Services: 0x25,
            TimestampUnixSeconds: 1700000000L,
            AddrRecv: P2pAddress.FromIPv4(0x01, "127.0.0.1", 18444),
            AddrFrom: P2pAddress.Anonymous(0x25),
            Nonce: 0x1122334455667788UL,
            UserAgent: "/ConsigliereRXD-livetest:0.1/",
            StartHeight: 0,
            Relay: true,
            AssociationId: null);

    [Fact]
    public async Task Handshake_WithLiveRadiantNode_EntersReady()
    {
        var endpoint = Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return; // opt-in: not configured, skip silently.

        var network = P2pNetwork.Resolve(NetworkName);
        Assert.NotNull(network);

        var (host, port) = ParseEndpoint(endpoint!, network!.DefaultPort);
        var ip = await ResolveAsync(host);
        var remote = new IPEndPoint(ip, port);

        await using var session = new PeerSession(network, remote);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await session.ConnectAsync(OurVersion(), cts.Token);

        Assert.True(result.Success,
            $"handshake failed: {result.FailureReason} {result.FailureDetail}");
        Assert.Equal(PeerSessionState.Ready, session.State);
        Assert.NotNull(result.PeerVersion);
        // The peer's advertised user agent — proof we parsed a real node's version.
        Assert.False(string.IsNullOrEmpty(result.PeerVersion!.UserAgent));
    }

    private static (string host, int port) ParseEndpoint(string raw, int defaultPort)
    {
        var idx = raw.LastIndexOf(':');
        if (idx <= 0 || idx == raw.Length - 1) return (raw, defaultPort);
        var host = raw[..idx];
        return int.TryParse(raw[(idx + 1)..], out var p) ? (host, p) : (host, defaultPort);
    }

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host);
        return addrs.Length > 0 ? addrs[0] : IPAddress.Loopback;
    }
}
