# Consigliere-RXD

A high-performance **Radiant (RXD)** indexer for scalable payment processing and
real-time UTXO tracking. A fork of [DXS Consigliere](https://github.com/dxsapp/dxs-consigliere)
(originally a Bitcoin SV / STAS indexer) adapted for the Radiant blockchain and
its Glyph token protocol.

Built for exchanges, payment processors, and merchant backends that need
real-time visibility, low latency, and cost-efficient blockchain monitoring —
**without** running a full-chain indexer.

- **Selective UTXO indexing** – Track only relevant payment/settlement addresses, not the full chain
- **High-throughput ready** – Designed for large volumes of payments with minimal latency
- **Dynamic address onboarding** – Add new addresses instantly at runtime, without reindexing
- **Real-time updates** – Live transaction and balance notifications via SignalR
- **RavenDB-powered** – Fast, scalable document storage for UTXO state and history
- **Reorg-safe** – Detects and reverses chain reorganizations

> **Relationship to RXinDexer:** Consigliere-RXD is *not* a replacement for
> [RXinDexer](https://github.com/RadiantBlockchain) (the canonical full-chain
> Glyph/WAVE/Swap explorer & inventory indexer). It is complementary: a lean,
> address-scoped, .NET-native payment/exchange gateway and an independent second
> implementation useful for cross-validation.

---

## Status

This fork is an in-progress adaptation. See [`RADIANT_ADAPTATION.md`](./RADIANT_ADAPTATION.md)
for the full roadmap and what has changed vs. upstream.

| Capability | State |
| --- | --- |
| Connect to Radiant node (JSON-RPC) | ✅ verified against regtest — reads chain tip |
| Real-time block/tx ingestion (ZMQ) | ⏸️ needs `zmqpub*` enabled on the node |
| Radiant address/key params (P2PKH, WIF) | ✅ identical to upstream (Radiant == Bitcoin byte prefixes) |
| Radiant fee policy (10,000 photons/byte) | ✅ applied |
| Selective P2PKH RXD indexing + SignalR | ✅ inherited from upstream engine |
| Radiant ref parsing (OP_PUSHINPUTREF/singleton) | ✅ ref scanner, unit-tested |
| Glyph envelope + CBOR metadata parsing | ✅ v1 + v2 Style A/B, unit-tested |
| Glyph indexing wired into pipeline + API | ✅ refs/token on MetaOutput; `manage/glyph-ref`; verified e2e |
| Block-path ingestion on regtest | ✅ RPC block scan parses real regtest blocks (fixed 3 BSV-vs-Radiant bugs) |
| Real-time tx push (ZMQ deposits) | ⏸️ needs `zmqpub*` enabled on the node, then live deposit test |
| **Radiant tx signing (P2PKH)** | 🚧 verify against node — M4 |
| BSV SaaS providers (Bitails / WoC / JungleBus) | ⛔ disabled (BSV-only, no Radiant equivalent) |

---

## Tech Stack

- **Language:** C# / .NET 9
- **Blockchain:** Radiant (RXD)
- **Database:** RavenDB
- **Realtime Updates:** SignalR WebSockets

---

## Quick start (Docker Compose)

Brings up Consigliere-RXD + RavenDB. Point it at a Radiant node:

```bash
# Edit docker-compose.yml env vars (RadiantNodeApi__*, ZmqClient__*, Network),
# then:
docker compose up --build
```

- App / Swagger: http://localhost:5000/swagger
- RavenDB studio: http://localhost:8080

## Docker (single container)

```bash
docker run -p 5000:5000 \
  -e "RavenDb__Urls__0=http://ravendb:8080" \
  -e "RavenDb__DbName=Consigliere" \
  -e "Network=Testnet" \
  -e "RadiantNodeApi__BaseUrl=http://your-radiant-node:17443" \
  -e "RadiantNodeApi__User=your_user" \
  -e "RadiantNodeApi__Password=your_password" \
  -e "ZmqClient__RawTx2Address=tcp://your-radiant-node:28332" \
  -e "ZmqClient__RemovedFromMempoolBlockAddress=tcp://your-radiant-node:28332" \
  -e "ZmqClient__DiscardedFromMempoolAddress=tcp://your-radiant-node:28332" \
  -e "ZmqClient__HashBlock2Address=tcp://your-radiant-node:28332" \
  consigliere-rxd:latest
```

Use the Admin API to add addresses to watch after startup.

## Radiant node requirements

Run a Radiant node with RPC + ZMQ enabled in `radiant.conf`:

```ini
server=1
rpcuser=your_user
rpcpassword=your_password
# default RPC ports: mainnet 7332, testnet 27332, regtest 17443
zmqpubrawtx=tcp://0.0.0.0:28332
zmqpubrawblock=tcp://0.0.0.0:28332
zmqpubhashblock=tcp://0.0.0.0:28332
```

> Radiant regtest uses testnet address/key parameters, so set `Network=Testnet`
> when pointing at a regtest node.

## Configuration

`appsettings.json` keys (override via env vars using `__` as the separator):

- `Network` — `"Mainnet"` or `"Testnet"` (use `Testnet` for regtest)
- `RadiantNodeApi` — `BaseUrl`, `User`, `Password` (JSON-RPC)
- `ZmqClient` — the four ZMQ endpoints
- `RavenDb` — `Urls`, `DbName`
- `TransactionFilter` — bootstrap `Addresses` (and, later, Glyph `Tokens`)

### Managing watched addresses (Admin API)

```bash
# Add an address to watch
POST /api/admin/manage/address
{
  "address": "1YourRadiantP2PKHAddress...",
  "name": "Exchange hot wallet"
}
```

These persist in RavenDB and survive restarts.

## Build & run from source

```bash
dotnet restore   # in ./src
dotnet build
dotnet run --project Dxs.Consigliere
```

Swagger: http://localhost:5000/swagger

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

connection.on("OnTransactionFound", (hex) => console.log("tx found", hex));
connection.on("OnBalanceChanged", (balanceDto) => console.log("balance", balanceDto));

await connection.start();
await connection.invoke("SubscribeToTransactionStream", {
  address: "1YourRadiantP2PKHAddress...",
  slim: false
});
```

## Credits

Forked from [DXS Consigliere](https://github.com/dxsapp/dxs-consigliere) by
[Oleg Panagushin](https://github.com/panagushin) / DXS, MIT-licensed. Radiant
adaptation tracks the original architecture; see `RADIANT_ADAPTATION.md`.
