using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Dxs.Bsv.P2p.Observer;

using Xunit;
using Xunit.Abstractions;

namespace Dxs.Bsv.Tests.P2p.Observer;

/// <summary>
/// Throughput characterization of the thin-node hot path — the per-output work
/// done for every transaction the P2P observer sees: Glyph ref extraction
/// (<see cref="TxScriptParser.TryParseGlyphRefs"/>) + watchlist match
/// (<see cref="WatchlistMatcher.Match"/>). This is the cost that gates how many
/// tx/sec a single Consigliere-RXD instance can screen.
///
/// Reported via ITestOutputHelper (run with -v normal to see numbers) and gated
/// by a deliberately-conservative floor so it doubles as a perf regression guard
/// without being flaky on slow CI. It exercises pure CPU (no IO/DB/network), so
/// it isolates the matcher/parser cost specifically.
/// </summary>
public class WatchlistThroughputBenchmark(ITestOutputHelper output)
{
    private const byte OP_PUSHINPUTREF = 0xd0;
    private const byte OP_PUSHINPUTREFSINGLETON = 0xd8;

    private static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => p.Length)];
        var n = 0;
        foreach (var p in parts) { Array.Copy(p, 0, o, n, p.Length); n += p.Length; }
        return o;
    }

    private static byte[] P2Pkh(byte fill)
    {
        var h = new byte[20];
        for (var i = 0; i < 20; i++) h[i] = fill;
        return Concat(new byte[] { 0x76, 0xa9, 0x14 }, h, new byte[] { 0x88, 0xac });
    }

    private static byte[] RefBytes(byte fill, uint vout)
    {
        var b = new byte[36];
        for (var i = 0; i < 32; i++) b[i] = fill;
        BitConverter.GetBytes(vout).CopyTo(b, 32);
        return b;
    }

    private static string RefHex(byte fill, uint vout) => Convert.ToHexStringLower(RefBytes(fill, vout));

    // A realistic mixed transaction: some plain P2PKH outputs + a Glyph token
    // output carrying a singleton ref. Mirrors what ObservedTxIngestor.ToParsedTx
    // builds per tx.
    private static ParsedTx BuildParsedTx(byte seed, bool withWatchedRef)
    {
        var glyphScript = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(0xAB, 0), P2Pkh(seed));
        var refs = TxScriptParser.TryParseGlyphRefs(glyphScript, out var r) ? r : Array.Empty<string>();

        var outHashes = new List<byte[]>
        {
            ExtractHash(P2Pkh(seed)),
            ExtractHash(P2Pkh((byte)(seed + 1))),
        };
        return new ParsedTx(
            $"tx{seed}",
            outHashes,
            new List<byte[]>(),
            Array.Empty<string>(),
            withWatchedRef ? refs : Array.Empty<string>());
    }

    private static byte[] ExtractHash(byte[] p2pkh) => p2pkh.AsSpan(3, 20).ToArray();

    [Fact]
    public void HotPath_ParseAndMatch_ThroughputFloor()
    {
        // Watchlist: a few hundred addresses + one watched Glyph ref (the
        // singleton above). Representative of an exchange watching many deposit
        // addresses + a tracked token.
        var matcher = new WatchlistMatcher();
        for (var i = 0; i < 500; i++)
            matcher.AddAddress(P2Pkh((byte)i).AsSpan(3, 20).ToArray());
        matcher.AddGlyphRef(RefHex(0xAB, 0));
        matcher.MarkLoaded();

        // Pre-build a pool of parsed tx (half carry the watched ref).
        const int poolSize = 2_000;
        var pool = new ParsedTx[poolSize];
        for (var i = 0; i < poolSize; i++)
            pool[i] = BuildParsedTx((byte)(i % 250), withWatchedRef: i % 2 == 0);

        // Warm up (JIT + caches).
        var warm = 0;
        for (var i = 0; i < poolSize; i++)
            if (matcher.Match(pool[i]) is not MatchResult.None) warm++;
        Assert.True(warm > 0);

        // Measure: also re-run the raw script parser to include extraction cost.
        var glyphScript = Concat(new[] { OP_PUSHINPUTREFSINGLETON }, RefBytes(0xAB, 0), P2Pkh(7));
        const int iterations = 200_000;
        var hits = 0L;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            TxScriptParser.TryParseGlyphRefs(glyphScript, out _);   // per-output extraction
            if (matcher.Match(pool[i % poolSize]) is not MatchResult.None) hits++;
        }
        sw.Stop();

        var perSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"matcher+parser hot path: {iterations:N0} ops in {sw.ElapsedMilliseconds} ms");
        output.WriteLine($"  => {perSec:N0} ops/sec ({sw.Elapsed.TotalMilliseconds * 1000.0 / iterations:F2} µs/op), hits={hits}");

        // Conservative floor: the pure-CPU screen must clear well above any
        // realistic chain tx rate. 50k ops/sec is ~25x a sustained 2k tx/s feed
        // and leaves generous headroom on slow CI. Tune up if it proves stable.
        Assert.True(perSec > 50_000,
            $"hot-path throughput {perSec:N0} ops/sec below 50k floor — possible perf regression");
    }

    [Fact]
    public void RefExtraction_ScalesWithOutputCount_NotPathological()
    {
        // A tx with many ref-bearing outputs (e.g. a token batch) must stay
        // linear, not quadratic. Measure a 100-output script-parse batch.
        var scripts = new byte[100][];
        for (var i = 0; i < scripts.Length; i++)
            scripts[i] = Concat(new[] { OP_PUSHINPUTREF }, RefBytes((byte)i, (uint)i), P2Pkh((byte)i));

        const int rounds = 5_000;
        var sw = Stopwatch.StartNew();
        var total = 0L;
        for (var r = 0; r < rounds; r++)
            foreach (var s in scripts)
                if (TxScriptParser.TryParseGlyphRefs(s, out var refs)) total += refs.Count;
        sw.Stop();

        var perOutput = sw.Elapsed.TotalMilliseconds * 1000.0 / (rounds * scripts.Length);
        output.WriteLine($"ref extraction: {rounds * scripts.Length:N0} outputs in {sw.ElapsedMilliseconds} ms => {perOutput:F3} µs/output");
        Assert.Equal((long)rounds * scripts.Length, total); // one ref each
        Assert.True(perOutput < 20.0, $"per-output extraction {perOutput:F3} µs too slow");
    }
}
