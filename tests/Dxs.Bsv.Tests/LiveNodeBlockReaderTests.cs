using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Dxs.Bsv;
using Dxs.Bsv.Block;
using Dxs.Bsv.Rpc.Configs;
using Dxs.Bsv.Rpc.Services.Impl;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dxs.Bsv.Tests;

/// <summary>
/// Opt-in integration test: drives the REAL production decode path against a live
/// Radiant node — <see cref="RpcClient.GetBlockAsStream"/> (which returns the
/// <c>JsonRpcResultNetworkStream</c> over the live HTTP response) -> <see cref="BlockReader"/>,
/// i.e. exactly what <c>NodeBlockchainDataProvider.ProcessBlock</c> does. Unlike
/// <see cref="BlockReaderTests"/> (which feeds an in-memory envelope), this exercises the
/// real chunked HTTP stream, so it catches any transport-level streaming regressions.
///
/// Skipped (returns early) unless RADIANT_RPC_URL is set, keeping the normal unit-test
/// run hermetic. Run it via Docker pointed at the host node:
///   docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
///     -e RADIANT_RPC_URL=http://host.docker.internal:17443/ \
///     -e RADIANT_RPC_USER=radiantrpc -e RADIANT_RPC_PASS=&lt;cookie&gt; \
///     -v $PWD:/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 \
///     dotnet test tests/Dxs.Bsv.Tests/Dxs.Bsv.Tests.csproj --filter FullyQualifiedName~LiveNodeBlockReaderTests
/// </summary>
public class LiveNodeBlockReaderTests
{
    private static string Url => Environment.GetEnvironmentVariable("RADIANT_RPC_URL");
    private static string User => Environment.GetEnvironmentVariable("RADIANT_RPC_USER") ?? "radiantrpc";
    private static string Pass => Environment.GetEnvironmentVariable("RADIANT_RPC_PASS");

    [Fact]
    public async Task Parses_a_live_block_through_the_real_rpc_client()
    {
        if (string.IsNullOrEmpty(Url))
            return; // not configured for this environment -> no-op

        using var http = new HttpClient();
        var cfg = Options.Create(new RpcConfig { BaseUrl = Url, User = User, Password = Pass });
        var rpc = new RpcClient(cfg, http, NullLogger<RpcClient>.Instance);

        var tip = (await rpc.GetBlockCount()).Result;
        Assert.True(tip >= 2, $"chain too short to test (tip={tip})");

        // Block 211 is a known coinbase + spend on this regtest chain; fall back to the
        // tip if the chain was reset shorter than that.
        var height = Math.Min(211, tip);
        var hash = (await rpc.GetBlockHash(height)).Result;

        using var stream = await rpc.GetBlockAsStream(hash);
        using var reader = BlockReader.Parse(stream, Network.Testnet);

        Assert.True(reader.TransactionCount >= 1, "expected at least the coinbase tx");

        var txs = reader.Transactions().ToList();
        Assert.Equal((int)reader.TransactionCount, txs.Count);          // header count == parsed count
        Assert.All(txs, t => Assert.Equal(64, t.Id.Length));            // every txid is a 32-byte hash

        // Header parsed into the standard 80-byte shape.
        Assert.Equal(32, reader.PrevBlockHash.Length);
        Assert.Equal(32, reader.MerkleRoot.Length);
        Assert.False(string.IsNullOrEmpty(reader.Bits));
    }
}
