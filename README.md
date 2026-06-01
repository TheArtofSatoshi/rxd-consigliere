# Consigliere-RXD

A high-performance **Radiant (RXD)** indexer for scalable payment processing and
real-time UTXO tracking. A fork of [DXS Consigliere](https://github.com/dxsapp/dxs-consigliere)
(originally a Bitcoin SV / STAS indexer) adapted for the Radiant blockchain and
its **Glyph** token protocol.

Built for exchanges, payment processors, and merchant backends that need
real-time visibility, low latency, and cost-efficient blockchain monitoring —
**without** running a full-chain indexer.

- **Selective UTXO indexing** – Track only relevant payment/settlement addresses (and Glyph tokens), not the full chain
- **Thin-node mode** – Watch mempool + blocks over the native Radiant **P2P** protocol, no node ZMQ required
- **Dynamic onboarding** – Add addresses or Glyph token refs at runtime, no reindex
- **Real-time updates** – Live transaction and balance notifications via SignalR
- **RavenDB-powered** – Fast, scalable document storage for UTXO state and history
- **Reorg-safe** – Detects and reverses chain reorganizations

> **Relationship to RXinDexer:** Consigliere-RXD is *not* a replacement for
> RXinDexer (the canonical full-chain Glyph/WAVE/Swap explorer & inventory
> indexer). It is complementary — a lean, address-scoped, .NET-native
> payment/exchange gateway, and an independent second implementation useful for
> cross-validation.

---

## 📌 Overview

**Consigliere-RXD** is a selective indexer for the Radiant (RXD) network. Rather
than indexing the whole chain, it tracks only explicitly configured **addresses**
and **Glyph token refs** — well suited for exchange deposit/hot-wallet
monitoring, payment processing, and merchant settlement at low infrastructure
cost. Watched entities can be added dynamically at runtime, and Consigliere-RXD
also **builds and signs** consensus-valid Radiant transactions for the outbound
(payout/withdrawal) side.

### How Radiant differs from BSV (what this fork changes)

| Aspect | BSV (upstream) | Radiant (this fork) |
| --- | --- | --- |
| Tokens | STAS script template + Back-to-Genesis tracing | **Glyph**: consensus-enforced induction refs (`OP_PUSHINPUTREF`) + CBOR metadata — no genesis trace |
| Token identity | script hash | 36-byte induction **ref** (txid+vout) in the scriptPubKey |
| Signing | BIP143 `SIGHASH_FORKID` | same FORKID **plus** an extra `hashOutputHashes` preimage field |
| Min-relay fee | ~0.05 sat/byte | **10,000 photons/byte** (post-V2 fork) |
| Address/WIF bytes | — | **identical to Bitcoin** (no change needed) |
| Realtime source | node ZMQ / SaaS | node ZMQ **or** native Radiant P2P (thin-node) |

The full engineering record — every Radiant-specific change, the file map, and
how each layer was verified against a live node — is in
[`RADIANT_ADAPTATION.md`](./RADIANT_ADAPTATION.md). Operations guidance (reorg
safety, mainnet readiness, throughput, observability, and **when to run
Consigliere-RXD vs. RXinDexer**) is in [`docs/RADIANT_OPS.md`](./docs/RADIANT_OPS.md).

## 🚦 Status

Adapted and verified against a live Radiant v3.0.0 regtest node:

| Capability | State |
| --- | --- |
| Node JSON-RPC connectivity + block-path ingest | ✅ verified on regtest |
| Radiant ref parsing (`OP_PUSHINPUTREF`/singleton) | ✅ unit-tested |
| Glyph envelope + CBOR metadata (v1, v2 Style A/B) | ✅ unit-tested |
| Glyph indexing wired into pipeline + `manage/glyph-ref` API | ✅ verified e2e |
| Native Radiant P2P transport + handshake | ✅ live handshake with `radiantd` |
| P2P thin-node Glyph watching | ✅ unit-tested |
| Transaction signing (Radiant `hashOutputHashes` preimage) | ✅ **node-validated** via `testmempoolaccept` |

---

## 🛠 Tech Stack

- **Language:** C# / .NET 9
- **Blockchain:** Radiant (RXD)
- **Database:** RavenDB
- **Realtime Updates:** SignalR WebSockets; native Radiant P2P (thin-node)

---

## Docker Setup

> **Note on data sources.** Upstream's "managed providers" wizard step uses
> **JungleBus** (a BSV SaaS with no Radiant equivalent). On Radiant, use a
> **Radiant node** instead — either the node JSON-RPC/ZMQ path or the **P2P
> thin-node** path (no node ZMQ required). See *Advanced* below.

### Run locally (recommended for self-hosting)

The fastest path on your own machine: one compose command, then finish the
first-run wizard in the browser (admin account + RavenDB). No domain, no TLS
cert. Configure the Radiant data source via the `RadiantNodeApi` / `ZmqClient`
env vars (or the P2P pool config), not the BSV provider wizard step.

> **No published image yet.** Build from source with the local-build overlay
> (first command below). Once a release image is published, drop the overlay
> and `compose.local.yml` pulls it instead (second command).

```bash
# 1. Start the stack (RavenDB + Consigliere-RXD).
#    NOW (build from source — no published image yet):
docker compose -f compose.local.yml -f compose.local-build.yml up -d --build
#    LATER (once a release is published — pull, no build):
docker compose -f compose.local.yml up -d

# 2. Open the admin UI and create the admin account:
#    http://localhost:5000

# 3. Add a watched address (Tracked Addresses screen) or a Glyph ref
#    (POST /api/admin/manage/glyph-ref). It starts indexing live — no
#    restart needed: the block-sync + realtime ingest tasks watch the
#    config and re-bind themselves when it changes.
```

Pin a specific release instead of `latest` (post-release path):

```bash
CONSIGLIERE_TAG=1.2.3 docker compose -f compose.local.yml up -d
```

Stop / wipe (add `-f compose.local-build.yml` too if you started
with the build overlay):

```bash
docker compose -f compose.local.yml down       # stop
docker compose -f compose.local.yml down -v     # stop + delete data
```

> This local profile serves **plain HTTP on `localhost:5000`**
> (cookie `Secure` flag relaxed so login works over http). It is
> for a single machine on a trusted network — **do not expose it
> to the public internet as-is**. For an internet-facing
> deployment use the TLS-fronted prod profile
> (`docker compose -f compose.yml -f compose.prod.yml --profile
> prod up -d`) and follow [`docs/runbook.md`](docs/runbook.md).

### Advanced: bring-your-own RavenDB + Radiant node (ZMQ)

If you run your own RavenDB + a Radiant node and prefer the node/ZMQ ingest path,
run the image directly and add watched addresses/refs via the Admin API. (For the
ZMQ path the node needs `zmqpubrawtx`/`zmqpubrawblock`/`zmqpubhashblock` enabled;
to avoid that, use the **P2P thin-node** path instead — see below.)

```bash
docker run -p 5000:5000 \
  -e "RavenDb__Urls__0=http://ravendb:8080" \
  -e "RavenDb__DbName=Consigliere" \
  -e "Network=Testnet" \
  -e "RadiantNodeApi__BaseUrl=http://your-node:17443" \
  -e "RadiantNodeApi__User=your_user" \
  -e "RadiantNodeApi__Password=your_password" \
  -e "ZmqClient__RawTx2Address=tcp://your-node:28332" \
  -e "ZmqClient__RemovedFromMempoolBlockAddress=tcp://your-node:28332" \
  -e "ZmqClient__DiscardedFromMempoolAddress=tcp://your-node:28332" \
  -e "ZmqClient__HashBlock2Address=tcp://your-node:28332" \
  consigliere-rxd:latest
```

### P2P thin-node (no node ZMQ required)

Instead of ZMQ, watch the chain over Radiant's native P2P protocol. Enable the
pool under `Consigliere:Broadcast:P2p` and point it at a Radiant node:

```json
"Consigliere": { "Broadcast": { "P2p": {
  "Enabled": true,
  "Network": "radiant-regtest",          // or radiant-mainnet / radiant-testnet
  "InitialPeers": ["host.docker.internal:18444"]
}}}
```

Use the Admin API to add addresses/tokens to watch after startup.

### Docker Release Policy

Release images are published automatically from Git tags.

Stable release trigger:

- push tag `vX.Y.Z`

Published Docker tags for `vX.Y.Z`:

- `dxs/consigliere:X.Y.Z`
- `dxs/consigliere:X.Y`
- `dxs/consigliere:X`
- `dxs/consigliere:latest`

Notes:

- prerelease tags are ignored by the DockerHub workflow in v1
- `latest` always points to the most recent stable `vX.Y.Z` release
- Git tag is the release source of truth

Maintainer release steps:

```bash
git checkout main
git pull --ff-only
git tag vX.Y.Z
git push origin vX.Y.Z
```

Required GitHub secrets for the workflow:

- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

### Docker Compose E2E Smoke (contributors / CI)

> Running the product? Use [Run locally](#run-locally-recommended-for-self-hosting)
> above — it pulls the published image and gives you live ingest
> through the wizard. The `compose.yml` stack below **builds from
> source** and **disables background tasks** (no live ingest); it
> exists for admin-shell / API smoke + SPA validation, not as a
> product run.

For local end-to-end smoke testing of the source tree, the repository includes a root `compose.yml`.
This stack is intentionally minimal:

- `ravendb`
- `consigliere` (built from source)

It is designed for admin-shell and API smoke testing, not for live chain ingest.
The compose profile:

- enables admin auth
- disables background tasks that require node/ZMQ connectivity
- uses RavenDB only

Run:

```bash
docker compose up --build
```

Stop and clean it:

```bash
docker compose down -v
```

Endpoints:

- Consigliere: `http://localhost:5000`
- Swagger: `http://localhost:5000/swagger`
- RavenDB Studio: `http://localhost:8080`

Default admin credentials for compose smoke:

- username: `admin`
- password: `admin123!`

Notes:

- compose uses `ravendb/ravendb:7.1-latest`, which resolves to a native multi-arch RavenDB image on both `amd64` and `arm64`
- the compose profile is intended for admin/API smoke and SPA validation, not live node/ZMQ ingest

Deep-link SPA routes are expected to work in this mode because the admin bundle is published into `wwwroot` and ASP.NET serves `index.html` as fallback.

## 📦 Manual Setup

> ⚠️ Upstream Consigliere was developed by **DXS** for internal BSV operations;
> this is a Radiant fork. External deployment may require adjustments.

```bash
# Clone the repository
git clone https://github.com/TheArtofSatoshi/rxd-consigliere.git
cd rxd-consigliere/src/Dxs.Consigliere
```

## Configuration

### Using appsettings.json

Create `src/Dxs.Consigliere/appsettings.Development.json` for local development.
Point `RadiantNodeApi` at a Radiant node (default RPC ports: mainnet 7332,
testnet 27332, regtest 17443 — use `Network: "Testnet"` for regtest, which shares
testnet address/key params):

```json
{
  "Network": "Testnet",
  "RavenDb": {
    "Urls": ["http://localhost:8080"],
    "DbName": "Consigliere"
  },
  "ZmqClient": {
    "RawTx2Address": "tcp://localhost:28332",
    "RemovedFromMempoolBlockAddress": "tcp://localhost:28332",
    "DiscardedFromMempoolAddress": "tcp://localhost:28332",
    "HashBlock2Address": "tcp://localhost:28332"
  },
  "RadiantNodeApi": {
    "BaseUrl": "http://localhost:17443",
    "User": "your_rpc_user",
    "Password": "your_rpc_password"
  },
  "TransactionFilter": {
    "Addresses": [],
    "GlyphRefs": []
  }
}
```

**Configuration Notes**:
- `Network`: `"Mainnet"` or `"Testnet"` (use `Testnet` for regtest — it shares testnet params)
- RavenDB: `8080` (default)
- Radiant Node RPC: `7332` (mainnet) / `27332` (testnet) / `17443` (regtest)
- Radiant Node ZMQ: `28332` (whatever you set `-zmqpub*` to)
- Radiant Node P2P: `7333` (mainnet) / `27333` (testnet) / `18444` (regtest)

### Managing Watched Addresses & Glyph Tokens

Use the **Admin API** to dynamically add addresses and Glyph token refs to watch:

```bash
# Add a Radiant (RXD) address to watch
POST /api/admin/manage/address
{
  "address": "1YourRadiantP2PKHAddress...",
  "name": "Exchange hot wallet"
}

# Add a Radiant Glyph token to watch, by its induction ref
# (compact outpoint hex: 32-byte txid LE + 4-byte vout LE = 72 hex chars)
POST /api/admin/manage/glyph-ref
{
  "glyphRef": "<72-hex-char-ref>",
  "name": "MyToken"
}
```

A watched ref makes the indexer surface every transaction whose output carries
that ref — over both the node block/ZMQ path and the P2P thin-node path. These
settings persist in RavenDB and survive restarts; you can also bootstrap them via
the `TransactionFilter` config (`Addresses` / `GlyphRefs`).

> The upstream `manage/stas-token` endpoint remains for BSV-STAS compatibility
> but is not used on Radiant — use `manage/glyph-ref` instead.

## Run

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the project
dotnet run
```

## Usage

Swagger can be found at the http://localhost:5000/swagger

## WebSocket API (SignalR)

Hub route: `/ws/consigliere`

Server methods (client calls):
- `SubscribeToTransactionStream({ address, slim })`
- `UnsubscribeToTransactionStream({ address, slim })`
- `GetBalance({ addresses, tokenIds })`
- `GetHistory({ address, tokenIds, desc, skipZeroBalance, skip, take })`
- `GetUtxoSet({ tokenId, address, satoshis })`
- `GetTransactions([txId, ...])`
- `Broadcast(rawTxHex)`

Client callbacks (server calls):
- `OnTransactionFound(hex)`
- `OnTransactionDeleted(txid)`
- `OnBalanceChanged(balanceDto)`

### Client example (JavaScript, SignalR)

```js
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://localhost:5000/ws/consigliere")
  .withAutomaticReconnect()
  .build();

connection.on("OnTransactionFound", (hex) => {
  console.log("tx found", hex);
});

connection.on("OnBalanceChanged", (balanceDto) => {
  console.log("balance changed", balanceDto);
});

await connection.start();

await connection.invoke("SubscribeToTransactionStream", {
  address: "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa",
  slim: false
});
```

### Client example (.NET, SignalR)

```csharp
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5000/ws/consigliere")
    .WithAutomaticReconnect()
    .Build();

connection.On<string>("OnTransactionFound", hex =>
{
    Console.WriteLine($"tx found {hex}");
});

connection.On<object>("OnBalanceChanged", balance =>
{
    Console.WriteLine($"balance changed {balance}");
});

await connection.StartAsync();

await connection.InvokeAsync("SubscribeToTransactionStream", new
{
    address = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa",
    slim = false
});
```

## Ops

Production deployment, monitoring, secret rotation, and
disaster recovery are documented in
[`docs/runbook.md`](docs/runbook.md). That document is the
operator-facing source of truth — read it instead of the
source code for any production-operations question.

## Credits

Upstream **Consigliere** (the BSV indexer + SDK this is forked from) was created by
[Oleg Panagushin](https://github.com/panagushin) / DXS — CTO / System Architect,
Crypto & FinTech — and is MIT-licensed.

This **Consigliere-RXD** fork adapts it to the Radiant (RXD) blockchain and the
Glyph token protocol; see [`RADIANT_ADAPTATION.md`](./RADIANT_ADAPTATION.md) for
the full record of what changed and how it was verified.
