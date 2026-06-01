# Consigliere → Radiant (RXD) adaptation

This document tracks the port of [DXS Consigliere](https://github.com/dxsapp/dxs-consigliere)
(a Bitcoin SV / STAS indexer) to the Radiant blockchain.

## Why

Consigliere fills a niche the canonical Radiant indexer (RXinDexer) does not:
**selective, address-scoped, real-time UTXO indexing** for payment processors,
exchanges, and merchant backends — plus a server-side tx-building SDK and SignalR
push. Goals: (a) a lean exchange/payment gateway for RXD + Glyph; (b) an
independent second indexer that cross-validates RXinDexer; (c) a strategic
artifact for engaging DXS (whose engine this is) toward Radiant support.

It is **not** a replacement for RXinDexer's full-chain Glyph/WAVE/Swap indexing.

## How different is Radiant from BSV?

Good news: the connectivity and base-layer encodings are nearly identical;
the token model is fundamentally different.

| Aspect | BSV (upstream) | Radiant | Impact |
| --- | --- | --- | --- |
| Node interface | JSON-RPC + ZMQ | JSON-RPC + ZMQ + REST | ✅ portable, config only |
| P2PKH address byte | main `0x00` / test `0x6f` | **same** | ✅ no change |
| P2SH address byte | main `0x05` / test `0xc4` | **same** | ✅ no change |
| WIF byte | main `0x80` / test `0xef` | **same** | ✅ no change |
| Sighash | BIP143 `SIGHASH_FORKID` (0x41) | **same FORKID** (fork id 0) | ✅ preimage already matches (verify PushTX) |
| Base unit | satoshi (1e8/coin) | photon (1e8/RXD) | ✅ same scale |
| Min-relay fee | ~0.05 sat/byte | **10,000 photons/byte** (V2, block 410k+) | ⚠️ constant change (done) |
| Tokens | STAS script template + Back-to-Genesis | **Glyph**: consensus refs (`OP_PUSHINPUTREF`) + CBOR | 🔧 replace token layer |

### Verified Radiant constants (from `radiantjs/lib/networks.js` + `Radiant-Core/src/chainparams*.cpp`)

- **netMagic:** mainnet `e3 e1 f3 e8`, testnet `f4 e5 f3 f4`, regtest `da b5 bf fa`
- **cashaddr prefix:** `radaddr` / `radtest` / `radreg`
- **P2P ports:** mainnet 7333, testnet 27333 (v3), regtest 18444
- **RPC ports:** mainnet 7332, testnet 27332 (v3), regtest **17443**
  (The `Radiant-Node` checkout is older v2.x: testnet 17332/17333.)
- Address/WIF byte prefixes are identical to Bitcoin (see table) — so the
  upstream `BitcoinHelpers` address code is already correct for Radiant.

## What changed in this fork (M0)

- **Fee policy** — `src/Dxs.Bsv/Factories/Models/DefaultFees.cs`: `Rate` set to
  10,000 photons/byte (was 0.05 sat/byte); `Total` flat fee scaled accordingly.
- **Config section rename** — `BsvNodeApi` → `RadiantNodeApi`
  (`src/Dxs.Consigliere/Startup.cs`, `appsettings.json`).
- **appsettings.json** — Radiant branding, Radiant default ports, empty Glyph
  token list, and BSV-SaaS-dependent task `UnconfirmedTransactionsMonitor` moved
  to `DisabledTasks` (JungleBus tasks were already disabled).
- **docker-compose.yml** — new app + RavenDB local stack.
- **README.md** — Radiant rebrand + honest status table.

Baseline upstream and the M0 fork both build cleanly via `DockerFile` on .NET 9.

## Verification log

**M0 (done):** `docker build -f DockerFile` is green (all four projects, .NET 9).
`docker compose up` brings up RavenDB + the indexer; the app connects to RavenDB,
runs migrations, and serves Swagger at `:5000`. (Fixed a startup race: the app
throws fatally if RavenDB isn't ready, so compose now gates it on a RavenDB
healthcheck via `depends_on: condition: service_healthy`.)

**M1 (in progress):** Verified against a **live local Radiant regtest node**
(`radiant.conf.regtest`, RPC 17443, `rpcbind=0.0.0.0`, `txindex=1`). With creds
injected via env, the indexer authenticated over JSON-RPC and read the chain tip
correctly: `BitcoindService: ChainTip is 237 actual chain tip is 237`. A Docker
container reaches the node via `host.docker.internal:17443` (node's `rpcallowip`
covers the Docker subnet). So the BSV-derived RPC client + `RadiantNodeApi`
config work against Radiant unchanged.

**M1 block-path (done) — three Radiant-vs-BSV bugs fixed so blocks actually
ingest.** Verified end-to-end in the full app against the live regtest node
(`BlockCountToScanOnStart=2` triggers block reads on boot): **0**
`EndOfStreamException`, **0** `Sequence contains no elements`, **0** tx-count
mismatches; `sync-status` returns `200 {"height":306,"isSynced":false}`.

1. **JSON-RPC stream decode** (`Rpc/Streams/JsonRpcResultNetworkStream.cs`) —
   upstream matched the literal `{"result": "` (BSV, *with* a space after the
   colon). Radiant/Bitcoin-Core emits **compact** JSON `{"result":"…` (no space),
   confirmed on the wire. The matcher never triggered → 0 payload bytes →
   `EndOfStreamException` in `BlockReader.ReadHeader`. Replaced with a
   whitespace-tolerant state-machine scanner that accepts both shapes. (Radiant
   block header itself is standard 80-byte Bitcoin layout — header parsing was
   never the problem.)
2. **Header tx-count field** (`Rpc/Models/Responses/RpcGetBlockHeaderResponse.cs`)
   — Radiant returns `nTx` (BSV used `num_tx`). Map `nTx`, keep `num_tx` as a
   legacy setter alias.
3. **Count-check ordering** (`BackgroundTasks/Blocks/BlockProcessBackgroundTask.cs`)
   — `context.TransactionsCount` was assigned *after* `ProcessBlock`, so the
   in-process "read N == expected" check always compared against 0. Moved the
   assignment before processing.

Plus a robustness fix: `AdminController.GetSyncState` used `FirstAsync()`, which
throws on a fresh DB with no processed blocks (→ 500); switched to
`FirstOrDefaultAsync()` (the null was already handled).

Tests (in `tests/Dxs.Bsv.Tests/`): `JsonRpcResultNetworkStreamTests`
(Radiant-compact / BSV-spaced / interior-whitespace / uppercase-hex /
buffer-spanning), `BlockReaderTests` (real coinbase-only + multi-tx block hex
fixtures), and the opt-in `LiveNodeBlockReaderTests` that drives the real
`RpcClient.GetBlockAsStream` → `BlockReader` path against a node (run with
`RADIANT_RPC_URL` set). **48 offline tests pass; the live test passes against
the regtest node.**

**Remaining for full M1:** real-time tx push is **ZMQ-driven** —
`BlockProcessBackgroundTask` waits for ZMQ HashBlock messages for *live* updates
(boot-time block scan via RPC already works, per above). The regtest node has no
`zmqpub*` publishers, so to test live deposits, add to `radiant.conf.regtest`
and restart the node:

```ini
zmqpubrawtx=tcp://0.0.0.0:28332
zmqpubrawblock=tcp://0.0.0.0:28332
zmqpubhashblock=tcp://0.0.0.0:28332
```

Then onboard an address (`POST /api/admin/manage/address`), send regtest RXD to
it, and confirm `OnTransactionFound` / `OnBalanceChanged` over SignalR.

> Note: macOS **AirPlay Receiver squats on `localhost:5000`** (returns
> `403 Server: AirTunes`), so the compose `5000:5000` mapping is unreachable from
> the host. Map another host port (e.g. `-p 5066:5000`) for manual API checks, or
> disable AirPlay Receiver in System Settings → General → AirDrop & Handoff.

**M2 (done) — Radiant opcodes + ref tracking:** Added the full Radiant opcode
set to `src/Dxs.Bsv/Script/OpCode.cs`, transcribed verbatim from
`Radiant-Core/src/script/script.h` (the authoritative source — the
`Radiant-Node` checkout is older v2.x). Authoritative values:

| Opcode | Hex | Inline bytes |
| --- | --- | --- |
| OP_STATESEPARATOR / …INDEX_UTXO / …INDEX_OUTPUT | 0xbd / 0xbe / 0xbf | 0 |
| **OP_PUSHINPUTREF** | **0xd0** | **36** (normal/fungible ref) |
| OP_REQUIREINPUTREF | 0xd1 | 36 (guard) |
| OP_DISALLOWPUSHINPUTREF / …SIBLING | 0xd2 / 0xd3 | 36 (guard) |
| OP_REFHASHDATASUMMARY_UTXO … REFHASHVALUESUM_OUTPUTS | 0xd4–0xd7 | 0 |
| **OP_PUSHINPUTREFSINGLETON** | **0xd8** | **36** (singleton/NFT ref) |
| OP_REFTYPE_* / OP_REF* / OP_CODESCRIPT* / introspection | 0xd9–0xec | 0 |
| OP_PUSH_TX_STATE / OP_BLAKE3 / OP_K12 | 0xed / 0xee / 0xef | 0 |

Critical parsing rule (node `GetScriptOp` + RXinDexer `glyph.py:290`): the set
**`{0xd0, 0xd1, 0xd2, 0xd3, 0xd8}`** are *special pushes* — each opcode byte is
followed by exactly **36 inline bytes** = a ref (32-byte txid in internal/LE
order + 4-byte LE vout). Every other Radiant opcode consumes no inline data.
Only **0xd0** (normal) and **0xd8** (singleton) establish a *carried* token
identity; the require/disallow guards (0xd1/0xd2/0xd3) carry a ref argument the
scanner must skip but not record.

> Correction note: an early draft mis-assigned the singleton opcode to 0xd1.
> The authoritative `script.h` value is **0xd8**; 0xd1 is OP_REQUIREINPUTREF.

New code (decoupled, unit-tested — `Dxs.Bsv` helpers extended in place):
- `src/Dxs.Bsv/Script/OpCodeHelpers.cs` — kept the existing `IsOpCode`; added
  `ConsumesInlineRef` ({0xd0,0xd1,0xd2,0xd3,0xd8}), `IsRefPush` ({0xd0,0xd8}),
  `IsRefSingleton` (0xd8), `InputRefSize` (36).
- `src/Dxs.Bsv/Tokens/Glyph/Ref.cs` — the ref value type (raw 36 bytes;
  `OutpointHex`, explorer-style `TxId`/`Vout`, `IsSingleton`). Replaces the BSV
  `TokenId` concept; no Back-to-Genesis trace (refs are consensus-enforced).
- `src/Dxs.Bsv/Tokens/Glyph/RadiantScriptScanner.cs` — `ExtractRefs(script)` /
  `HasRef(script)`. Walks the script honouring every data-push length so a ref
  opcode byte merely *inside* pushed data is never mistaken for an opcode, and
  skips 36 inline bytes for all five ref opcodes (recording only 0xd0/0xd8).
- `tests/Dxs.Bsv.Tests/` — xUnit project, **12 vectors, all passing**: normal/
  singleton refs, P2PKH (no refs), the "ref byte inside pushed data" trap,
  multi-ref order, ref-guards-consume-but-not-recorded, the **singleton-desync
  regression**, introspection-op desync, truncated refs, txid endianness.

Run tests (no local dotnet needed):
```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  -v "$PWD":/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/Dxs.Bsv.Tests/Dxs.Bsv.Tests.csproj
```

## M3b (done) — Glyph indexing integration

The parser (below) is now wired into the indexing pipeline so refs and token
metadata flow through to storage, the filter, and the admin API:

- `src/Dxs.Bsv/Tokens/Glyph/GlyphOutputInfo.cs` — the single per-output Glyph
  extraction seam: `FromScript(scriptPubKey)` → carried refs + `PrimaryRef`
  (singleton preferred, else first) + parsed envelope/token info. Cheap on
  non-Glyph scripts (returns an empty shared instance); never throws.
- `Models/Output.cs` — `GetGlyph(transaction)` lazily computes + caches the
  `GlyphOutputInfo` from the materialized script bytes. The delicate streaming
  `LockingScriptReader`/`ScriptType` path is left untouched.
- `Data/Models/Transactions/MetaOutput.cs` — persisted Glyph fields
  (`GlyphRef`, `GlyphRefs`, `IsGlyphSingleton`, `GlyphTokenType`, `GlyphName`,
  `GlyphTicker`), populated in `FromOutput` from the already-materialized
  scriptPubKey. Per-token balances can key off `GlyphRef`.
- `BitcoinMonitor/{ITransactionFilter,Impl/TransactionFilter}.cs` — new
  `ManageUtxoSetForGlyphRef` + a `_watchingGlyphRefs` set; the output loop saves
  any tx with an output carrying a watched ref (alongside watched addresses/STAS).
- Bootstrap parity with STAS tokens: `TransactionFilterConfig.GlyphRefs[]`,
  `WatchingGlyphRef` entity, `ITransactionStore.GetWatchingGlyphRefs()` (config +
  DB), loaded in `TransactionFilter.InitAsync`, and a `WatchGlyphRefRequest` DTO.
- `Controllers/AdminController.cs` — `POST /api/admin/manage/glyph-ref`
  (validates a 72-hex ref, persists, registers in the filter).

**Verified end-to-end** against the live regtest node + RavenDB (probe container
on host port 5055 — note macOS **AirPlay Receiver squats on :5000** and returns
`403 Server: AirTunes`, so the compose mapping is unreachable from the host; use
another host port for manual API checks):
- `POST manage/glyph-ref` valid 72-hex → **200**; short/non-hex → **400**;
  duplicate → **200** (idempotent).
- New route appears in Swagger; `WatchingGlyphRef` doc persists in RavenDB
  (so it reloads via `GetWatchingGlyphRefs` on restart → re-registers in filter).
- `dotnet test` → **31 passing** (added `GlyphOutputInfoTests`: P2PKH/empty →
  not-Glyph, FT ref → PrimaryRef, singleton-preferred-as-primary, reveal →
  TokenInfo, ref-without-envelope → no TokenInfo). Full image
  `consigliere-rxd:m3int` builds clean (0 errors).

> ~~Pre-existing finding (M1, not introduced here): on the regtest node,
> `BlockProcessBackgroundTask` fails with `EndOfStreamException` in
> `BlockReader.ReadHeader` (`Dxs.Bsv/Block/BlockReader.cs:32`).~~ **RESOLVED — see
> M1b below.** It was *not* a header-format difference: Radiant's block header is the
> standard 80 bytes and `BlockReader` reads it correctly. The bug was one layer up,
> in the JSON-RPC stream reader.

## M1b (done) — block-path parsing on Radiant

Root cause of the `EndOfStreamException` above: `getblock <hash> 0` returns the raw
block hex inside a JSON-RPC envelope, and `JsonRpcResultNetworkStream` decodes that
hex on the fly. Its prefix detector matched the **literal** `{"result": "` — with a
space after the colon, the formatting BSV nodes emit. Radiant (a Bitcoin Core fork)
emits **compact** JSON, `{"result":"…`, with no space. The prefix never matched, so
the stream produced **zero** payload bytes; `BlockReader.ReadHeader` then hit EOF on
its first `ReadUInt32Le`. (Verified against the live node: `curl … getblock <hash> 0`
returns `{"result":"<hex>","error":null,"id":"t"}`.)

Confirmed there is **no** header difference — manually decoding a real regtest block
shows the classic 80-byte layout (version | prevhash | merkleroot | time | bits |
nonce), and Radiant txids are still double-SHA256 (only the PoW *block* hash uses a
different algorithm, which the indexer never recomputes).

Fix — `src/Dxs.Bsv/Rpc/Streams/JsonRpcResultNetworkStream.cs`: replaced the brittle
literal compare with a small whitespace-tolerant state machine that scans
`{` → `"result"` → `:` → `"` (ignoring any JSON whitespace), then streams the hex
value as before. Decodes both Radiant (compact) and BSV (spaced) responses.

Tests — `tests/Dxs.Bsv.Tests/BlockReaderTests.cs` (7 vectors, real regtest fixtures:
block 2 coinbase-only + block 211 coinbase+spend): compact-envelope decode (the
regression), spaced-BSV and pretty-printed/uppercase variants, full header-field +
txid parse of both blocks, and an **end-to-end** test piping the exact RPC envelope
through `JsonRpcResultNetworkStream` → `BlockReader.Parse(stream)` — the production
path from `NodeBlockchainDataProvider.ProcessBlock`.

Also added `tests/Dxs.Bsv.Tests/LiveNodeBlockReaderTests.cs` — an opt-in integration
test (gated on `RADIANT_RPC_URL`, a no-op otherwise so the suite stays hermetic) that
drives the **real** `RpcClient.GetBlockAsStream` over a live HTTP response into
`BlockReader`, exercising the actual chunked network stream rather than an in-memory
envelope. Verified green against the running regtest node (`docker run … -e
RADIANT_RPC_URL=http://host.docker.internal:17443/ … --filter
FullyQualifiedName~LiveNodeBlockReaderTests`): a live block parsed cleanly, header
count == parsed tx count. `dotnet test` → **39 passing** (38 hermetic + the gated
live test when a node is configured).

> Note: block-path ingestion is still *triggered* by ZMQ hashblock notifications
> (see "Remaining for full M1" above) — the regtest node needs `zmqpub*` publishers
> before `BlockProcessBackgroundTask` runs end-to-end. The parsing blocker is gone;
> the Glyph layer is independent and was already verified.

### M1b hardening — stop swallowing stream errors

Follow-up to the above: `JsonRpcResultNetworkStream.ReadStream()` wrapped its whole
body in `try { … } catch (Exception) { return false; }`, turning *any* failure into a
clean end-of-stream. That is what made the compact-JSON prefix bug so hard to
diagnose — the descriptive `Unexpected RPC response, …` throws never escaped; the
consumer only ever saw a bare `EndOfStreamException` from `BlockReader.ReadHeader`. It
also meant a transient IO error part-way through a large block read looked like a
*successful but truncated* read, silently corrupting indexing. (The reader has no
retry contract: `return false` means EOF to `BitcoinStreamReader`, so "swallow and
return false" never retried anything — it only disguised the cause.)

Fix — `src/Dxs.Bsv/Rpc/Streams/JsonRpcResultNetworkStream.cs`: removed the blanket
catch so the structural/parse throws propagate out of `Read()` to the caller. Added a
dedicated `RpcResponseException` (new file alongside the stream) for every malformed
condition, and made three cases explicit instead of silent: a truncated stream
("ended before the result payload was fully read"), a non-string `result` — including
an error envelope `{"result":null,"error":{…}}`, whose raw text (e.g. the node's
`"Block not found"`) is now embedded in the message — and a non-hex / half-byte
payload. Also short-circuit once the closing quote is seen so trailing envelope JSON
across chunk boundaries can't be mis-scanned.

Tests — `BlockReaderTests.cs` +5: error envelope, non-string result, truncated
payload, odd/half-byte payload (each asserts a descriptive `RpcResponseException`, not
a silent `EndOfStreamException`), plus an end-to-end test driving an error envelope
through the production `JsonRpcResultNetworkStream` → `BlockReader.Parse` path. The
happy-path decode/parse vectors are unchanged. `dotnet test` → **44 passing**.

## M3a (done) — Glyph envelopes + CBOR (parser)

Ported RXinDexer `electrumx/lib/glyph.py` → C# so the two indexers agree on token
detection. Added `System.Formats.Cbor` to `Dxs.Bsv.csproj` and, under
`src/Dxs.Bsv/Tokens/Glyph/`:
- `GlyphProtocol.cs` — protocol IDs (FT=1 … WAVE=11), `GlyphTokenType`,
  `GlyphEnvelopeFlags` (IsReveal=0x80, HasContentRoot=0x01, HasController=0x02),
  and the `ToTokenType()` classifier (mirrors `get_token_type_id`).
  (`IReadOnlyCollection<T>.Contains` is a `System.Linq` extension, not an
  interface member — `ToTokenType` calls `Enumerable.Contains` explicitly.)
- `GlyphEnvelope.cs` / `GlyphTokenInfo.cs` — parsed-envelope + normalised
  token-info records (version, protocols, name, ticker, decimals, desc, dMint).
- `GlyphParser.cs` — `ParseEnvelope` / `ParseTokenInfo` / `ParseScriptPushes` /
  `ContainsMagic`. Handles v1 / v2 Style B (standalone `gly` push + CBOR or
  version‖flags…) and v2 Style A (`OP_RETURN`, `gly`-prefixed push,
  reveal-in-next-push or inline commit hash + optional content-root/controller).
  CBOR via a recursive `CborReader` walker.

Robustness fix (matters for real chain data): CBOR detection requires the payload
to *start* with a CBOR map **and** be fully consumed (no trailing bytes). Without
it, a script that merely contains the 3 bytes `gly` followed by arbitrary data was
mis-detected as a reveal.

`tests/Dxs.Bsv.Tests/GlyphParserTests.cs` covers v1 reveal, v2 Style B reveal,
v2 Style A reveal + commit, controller-flag commit, SHA256(commit)==SHA256(metadata),
dMint classification + config, v1 legacy `type` inference, ref-opcodes-before-
envelope skip, non-Glyph → null, magic-then-garbage → null, embedded-magic
no-false-positive. With the ref-scanner suite the project runs **25 tests, all
passing** (`Passed! Failed: 0, Passed: 25`). Full image `consigliere-rxd:m3`
builds clean (0 errors).

> Build/test gotcha: `bin/obj` live on the bind-mounted volume, so a stale
> incremental build can mask source changes. Clean before a test run:
> `find src tests -type d \( -name bin -o -name obj \) -exec rm -rf {} +`.

**Remaining (M3 → indexing integration):** wire refs + token info into the UTXO
pipeline — call `RadiantScriptScanner.ExtractRefs` / `GlyphParser` from
`Models/Output.Parse`, add a `RadiantRef`/`Glyph` script-type and per-ref balance
keying in the RavenDB layer, and a `ref` filter alongside the address filter. Then
Glyph-aware DTOs + SignalR token-balance notifications, cross-checked vs RXinDexer.

## Architecture map (keep / rework / drop)

Chain-specific code lives in `src/Dxs.Bsv` (built on **NBitcoin**, used only for
keys/secp256k1/Base58 — all chain rules are DXS's own code). The app layer
(`src/Dxs.Consigliere`) and selective-indexing engine are chain-agnostic.

### Keep (works on Radiant as-is)
- `src/Dxs.Bsv/Rpc/*`, `src/Dxs.Bsv/Zmq/*` — JSON-RPC + ZMQ clients.
- `src/Dxs.Bsv/BitcoinMonitor/*` — selective indexing + filtering engine.
- `src/Dxs.Bsv/Address.cs`, `BitcoinHelpers.cs` — address/key encoding (Radiant == Bitcoin bytes).
- `src/Dxs.Bsv/Transactions/Build/*` — BIP143 FORKID preimage (verify for Radiant; see M4).
- `src/Dxs.Consigliere/*` — REST admin, SignalR hub, RavenDB store, reorg-safe block processing.

### Rework (token layer + opcodes)
- `src/Dxs.Bsv/Script/OpCode.cs`, `OpCodeHelpers.cs`, `Read/*` — add Radiant
  opcodes: `OP_PUSHINPUTREF` (0xd0), `OP_PUSHINPUTREFSINGLETON` (0xd8),
  `OP_REQUIREINPUTREF`, `OP_STATESEPARATOR`, etc. Recognize ref-bearing
  `scriptPubKey`s and extract the 36-byte ref (txid+vout).
- `src/Dxs.Bsv/Tokens/Stas/*`, `StasHelpers.cs`, `TokenId.cs` — **replace STAS +
  Back-to-Genesis with Glyph**: ref = token identity; parse Glyph envelopes
  (v1 scriptSig `676c79` magic; v2 Style A `OP_RETURN`; v2 Style B `OP_3` chunked)
  + CBOR decode. No genesis tracing (consensus-enforced refs).
- `src/Dxs.Bsv/Script/Build/P2StasScriptBuilder.cs`, `Mnee1SatScriptBuilder.cs`
  and `src/Dxs.Bsv/Factories/*Stas*` — Glyph script builders (or delegate token
  tx-building to `@photonic/lib`).
- App-layer STAS surfaces: `Data/Indexes/StasUtxoSetIndex.cs`,
  `Dto/Requests/WatchStasTokenRequest.cs`, `Dto/Responses/ValidateStasResponse.cs`,
  `BackgroundTasks/StasAttributes*` → Glyph equivalents.

### Drop / replace (BSV-only external SaaS)
- `src/Dxs.Infrastructure/Bitails/*`, `WoC/*`, `JungleBus/*` — BSV SaaS clients.
  Currently disabled via config (kept compiling). Remove DI registrations in
  `Startup.cs:91-94,108-124` and refactor consumers
  (`BackgroundTasks/UnconfirmedTransactionsMonitor.cs:33-34`,
  `Services/Impl/JungleBusBlockchainDataProvider.cs`). The node path
  (`NodeBlockchainDataProvider`) is the Radiant ingestion route.

## Remaining milestones

- **M1 — Connectivity + payment MVP:** point RPC/ZMQ at the local regtest node;
  confirm P2PKH RXD indexing, dynamic address onboarding
  (`POST /api/admin/manage/address`), and SignalR `OnTransactionFound`/
  `OnBalanceChanged`. Validate `GetBalance`/`GetUtxoSet`.
- **M2 — Opcodes + ref tracking:** add Radiant opcodes; track ref → balance per
  address (no metadata).
- **M3 — Glyph envelopes + CBOR:** parse v1/v2 envelopes; token metadata;
  Glyph-aware DTOs + SignalR notifications. Cross-check vs RXinDexer.
- **M4 — Tx building:** P2PKH RXD payments. **Verify the FORKID preimage against
  the Radiant node** — Radiant has a PushTX/native-introspection feature
  (`PushTXStateHeight`); confirm plain-P2PKH sighash still matches BIP143.
  Also fix the hardcoded `Network.Mainnet` in
  `Factories/Impl/P2PkhTransactionFactory.cs:136`.
- **M5 — Hardening:** reorg depth, mainnet params, throughput test, observability,
  ops docs.

## Risks

1. **Signing / PushTX** — verify Radiant's post-`PushTXStateHeight` sighash
   preimage equals the standard BIP143 FORKID preimage for plain P2PKH before
   relying on the tx builder. Cross-check against a tx produced by `@photonic/lib`.
2. **Custom opcodes** are absent from NBitcoin — ref extraction needs a custom parser.
3. **Large CBOR payloads** (Glyph v2 Style B up to ~12 MB) vs RavenDB doc limits.
4. **Token-logic overlap with RXinDexer** — treat RXinDexer + REP specs as the
   canonical reference and reuse REP test vectors so both indexers agree.

## References

- Glyph/ref protocol: `REP/REP-1001.md`, test vectors `REP/REP-3003.md`,
  `RXinDexer/docs/GLYPH_PARSING.md`.
- Canonical parsing source of truth: `RXinDexer/electrumx/lib/glyph.py`,
  `RXinDexer/electrumx/server/glyph_index.py`.
- Tx builders: `Photonic-Wallet/packages/lib/src/{mint,transfer,token,script}.ts`.
- Chain constants: `radiantjs/lib/networks.js`, `Radiant-Core/src/chainparams*.cpp`.

## P2P thin-node Glyph watching (done, on radiant/vnext)

vnext ships a native Bitcoin-P2P observer (`src/Dxs.Bsv/P2p/`) + thin-node mode:
it watches mempool/headers directly over the P2P protocol, so an operator does
NOT need a full node with ZMQ enabled. This wires Radiant Glyph token watching
into that path — the analog of the existing STAS/DSTAS token watching — so the
thin-node tracks tokens by induction ref, not just addresses.

Pipeline (parse → match → ingest → persist):
- `P2p/Observer/TxScriptParser.TryParseGlyphRefs(script)` — extract a locking
  script's induction refs (OP_PUSHINPUTREF 0xd0 / singleton 0xd8) as compact
  outpoint hex, via `RadiantScriptScanner`. Total, throw-free.
- `P2p/Observer/ParsedTx.OutputGlyphRefs` — optional/defaulted field (existing
  4-arg call sites + tests keep compiling).
- `P2p/Observer/WatchlistMatcher` — `_glyphRefs` set + `AddGlyphRef`/
  `RemoveGlyphRef`/`IsWatchedGlyphRef`/`HasAnyGlyphRefs`/`WatchedGlyphRefCount`;
  `Match()` resolves address/token/glyph into a single-hit record or composite
  `Both`. New `MatchResult.GlyphHit`; `Both` gained a `GlyphRefs` list.
- `Services/P2p/ObservedTxIngestor` — extracts output glyph refs into `ParsedTx`.
- `Services/P2p/RavenWatchlistLoader` — bulk-load + Changes-API live add/remove +
  `TrackGlyphRefNow` for the `WatchingGlyphRefs` collection, seeded at init
  (mirrors the WatchingTokens path).

Tests: `tests/Dxs.Bsv.Tests/P2p/Observer/WatchlistMatcherGlyphTests.cs` +
`TxScriptParserGlyphTests.cs`. The full P2P observer suite (vnext's existing
tests + new) runs **97 passing** — confirming the shared `ParsedTx` /
`MatchResult` / `Both` changes are backward-compatible. Full image
`consigliere-vnext:radiant-p2p` builds clean (admin-ui + .NET, 0 errors).

**Net effect:** the M1 ZMQ blocker is now bypassable — onboard a Glyph ref via
`POST /api/admin/manage/glyph-ref`, and thin-node mode will surface mempool +
block transactions carrying that ref over P2P, with no node ZMQ/reconfiguration.
