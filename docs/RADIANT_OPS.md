# Consigliere-RXD — Operations & Hardening Notes

Operator-facing companion to [`RADIANT_ADAPTATION.md`](../RADIANT_ADAPTATION.md)
(which is the engineering record of the BSV→Radiant port). This file covers the
M5 hardening review: reorg safety, mainnet readiness, throughput, observability,
and **when to run Consigliere-RXD vs. RXinDexer**.

---

## 1. Consigliere-RXD vs. RXinDexer — when to run which

They are complementary, not competing. Pick by **what question you need answered**.

| | **Consigliere-RXD** | **RXinDexer** |
| --- | --- | --- |
| Model | **Selective** — indexes only onboarded addresses + Glyph refs | **Full-chain** — every Glyph token, WAVE name, swap |
| Stack | C# / .NET 9, RavenDB, SignalR | Python / ElectrumX, RocksDB |
| Ingest | Radiant node RPC+ZMQ **or** native P2P thin-node | Full node, ElectrumX protocol |
| Built for | Exchange deposit/hot-wallet watching, payment processing, **outbound signing** | Explorers, wallets, marketplaces, token inventory & history |
| Footprint | Low — scales with #watched entities, not chain size | High — full UTXO + token state |
| Outbound tx | **Yes** — builds + signs Radiant P2PKH (node-validated) | No |

**Run Consigliere-RXD when** you are an exchange / payment processor / custodial
backend that needs: real-time deposit detection on a known set of addresses or
tokens; a lean .NET service that doesn't carry the whole chain; the ability to
*build and broadcast* payouts; or an independent second indexer to cross-check
RXinDexer.

**Run RXinDexer when** you need full-chain answers: "all holders of token X",
"this address's complete history", WAVE name resolution, swap order books, or a
block explorer / wallet backend.

**Run both when** you want defense-in-depth: Consigliere-RXD watches your
exchange's hot addresses while RXinDexer answers ecosystem-wide queries — and the
two independent implementations catch each other's indexing bugs (the original
rationale for this fork).

---

## 2. Reorg safety — reviewed, adequate

**Radiant-Core finalizes blocks `DEFAULT_MAX_REORG_DEPTH = 6` deep**
(`validation.h:178`, `-maxreorgdepth`, `parkdeepreorg=true` by default) — the
chain itself rejects new headers below the finalized block, so a consensus-legal
reorg is bounded at 6.

Consigliere-RXD handles reorgs on both ingest paths, each with margin far beyond 6:

- **RPC path** (`ActualChainTipVerifyBackgroundTask`): on a tip
  height-match-but-hash-mismatch, it streams known blocks newest→oldest, compares
  each height's hash against the node, and re-processes from the first divergence.
  **Depth-unbounded** — walks as deep as needed.
- **P2P thin-node path** (`ReorgDetector` + `HeadersChain`): keeps a working
  window of `RetainedHeaderCount = 200` headers and walks back to the common
  ancestor, emitting a `ReorgPlan` (orphaned + new-chain hashes). A reorg deeper
  than the 200-header window degrades **gracefully** — it emits a single
  `OnReorg(DegradedState = true)` and stops rather than corrupting state.

**Verdict:** 200-header window ≫ the chain's 6-block finalization, and the RPC
path is unbounded. Reorg depth is more than adequate. The 200 default is tunable
via `HeadersChainOptions.RetainedHeaderCount` if an operator wants a deeper P2P
window (costs memory + a little Raven).

---

## 3. Mainnet parameter sweep — clean

Checked every network-sensitive constant for mainnet correctness:

| Parameter | Mainnet value | Status |
| --- | --- | --- |
| P2PKH / P2SH / WIF address bytes | `0x00` / `0x05` / `0x80` | ✅ identical to Bitcoin; `BitcoinHelpers` already correct |
| Fee floor | 10,000 photons/byte | ✅ `DefaultFees.Rate` (post-V2 mainnet floor) |
| Sighash | FORKID + `hashOutputHashes` | ✅ `RadiantSignatureHash`, network-independent |
| Block-header tx-count field | `nTx` | ✅ parsed for all networks |
| P2P magic / port (mainnet) | `e3 e1 f3 e8` / 7333 | ✅ `P2pNetwork.RadiantMainnet` |
| RPC port (mainnet) | 7332 | ✅ documented; operator-supplied via `RadiantNodeApi` |

No testnet/regtest values are hardcoded in runtime code. The only literal
`:8333` seeds are the **BSV** mainnet fallback seeds on the BSV `P2pNetwork.Mainnet`
variant (correct for that variant); the Radiant variants ship empty seed lists
and rely on operator-supplied `InitialPeers`.

**To run against mainnet:** set `Network=Mainnet`, point `RadiantNodeApi` at the
mainnet RPC (port 7332), and for P2P set `Network: "radiant-mainnet"` with a
mainnet `InitialPeers` entry. No code change required.

> ⚠️ **Radiant has no public P2P DNS seeds wired in this fork.** P2P thin-node on
> mainnet currently requires at least one known-good `InitialPeers` entry to
> bootstrap (DNS-seed crawling is a future item). The node RPC/ZMQ path has no
> such requirement.

---

## 4. Throughput — measured

The thin-node **hot path** (per-output Glyph ref extraction +
`WatchlistMatcher.Match`, pure CPU, no IO) measured on a dev laptop via
`WatchlistThroughputBenchmark`:

| Metric | Result |
| --- | --- |
| Parse + match hot path | **~721,000 ops/sec (~1.39 µs/op)** |
| Ref extraction per output | **~0.108 µs/output** (linear in output count) |

That is ~360× a sustained 2,000 tx/s feed, so **the matcher/parser is not the
throughput bottleneck** — sustained ingest is gated by RavenDB writes + the node
/ P2P feed, not by Radiant Glyph screening. The benchmark asserts a conservative
50k ops/sec floor so it doubles as a perf-regression guard:

```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  -v "$PWD":/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/Dxs.Bsv.Tests/Dxs.Bsv.Tests.csproj \
    --filter FullyQualifiedName~WatchlistThroughputBenchmark \
    --logger "console;verbosity=detailed"
```

> Not yet measured: end-to-end ingest tx/sec *with* RavenDB persistence under
> load (the realistic ceiling). That needs a loaded Raven instance — see the
> `Dxs.Consigliere.Benchmarks` project's Raven-backed harnesses for the pattern.

---

## 5. Observability / metrics — current state + gap

**Present:**
- `TransactionFilterMetrics` — per-status tx counters in the filter, logged
  periodically (1-min) via Serilog.
- `SourceMetricsSnapshot` persisted to RavenDB + exposed at
  `GET /api/admin/metrics/sources` (per-source ingest snapshots, history).
- Filter init logs watched address / token / **Glyph-ref** counts.
- Structured Serilog throughout (console sink; Grafana-Loki sink available via
  the upstream `Serilog.Sinks.Grafana.Loki` package).

**Gap (documented, not yet closed):** the project references
`OpenTelemetry.Instrumentation.AspNetCore` + `.Runtime` but **does not wire a
`MeterProvider` or expose a Prometheus/OTLP metrics endpoint.** So today metrics
are pull-via-admin-REST + log scraping, not a scrape-able `/metrics` surface.

**Recommended next step** (small, additive): in `Startup`/`Program`, add
`services.AddOpenTelemetry().WithMetrics(...)` with an OTLP or Prometheus
exporter, and register a `Meter` that mirrors `TransactionFilterMetrics`
(tx-seen, tx-matched, tx-saved, reorg-count, watched-entity gauges). This turns
the existing in-process counters into a standard scrape target without changing
the ingest logic.

---

## Summary

| M5 item | Outcome |
| --- | --- |
| Reorg-depth review | ✅ Adequate — 200-header P2P window + unbounded RPC walk vs. chain's 6-block finalization |
| Mainnet param sweep | ✅ Clean — no blocking hardcodes; mainnet is config-only (caveat: no P2P DNS seeds) |
| Throughput test | ✅ ~721k ops/sec hot path; matcher is not the bottleneck; regression guard added |
| Metrics/observability audit | ⚠️ Solid logging + admin-REST snapshots; **OTel `/metrics` endpoint not wired** (documented next step) |
| Consigliere-RXD vs RXinDexer doc | ✅ Section 1 above |
