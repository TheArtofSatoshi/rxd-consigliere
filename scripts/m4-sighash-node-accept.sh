#!/usr/bin/env bash
# M4 (tx building / signing) — node-acceptance proof, one command.
#
# Auto-provisions a real regtest UTXO + WIF + destination, then runs the
# canonical opt-in test (LiveRadiantSigningTests) in the dotnet SDK container.
# That test builds + signs a plain P2PKH RXD spend with the DXS TransactionBuilder
# (exercising Radiant's hashOutputHashes preimage extension via
# RadiantSignatureHash) and submits the raw hex to the node's `testmempoolaccept`,
# which runs full script verification WITHOUT broadcasting. `allowed:true` is the
# only proof that the transcribed Radiant sighash matches what radiantd enforces —
# Risk #1 in RADIANT_ADAPTATION.md. A wrong preimage yields
# mandatory-script-verify-flag-failed and the test fails.
#
# Defaults target the local regtest node + Radiant-Core build. Override via env:
#   RADIANT_CLI / RADIANT_DATADIR / RADIANT_CONF / RADIANT_NET   cli connection
#   RADIANT_RPC_HOST   host the SDK container uses to reach RPC (default:
#                      host.docker.internal — covered by rpcallowip 172.16.0.0/12)
#
# Run:  bash scripts/m4-sighash-node-accept.sh
# Exit: 0 if the node accepts the signed tx, non-zero otherwise.

set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)

RADIANT_CLI=${RADIANT_CLI:-/Users/macbookair/CascadeProjects/Radiant-Core/build/src/radiant-cli}
RADIANT_DATADIR=${RADIANT_DATADIR:-/Users/macbookair/Library/Application Support/Radiant}
RADIANT_CONF=${RADIANT_CONF:-radiant.conf.regtest}
RADIANT_NET=${RADIANT_NET:--regtest}
RPC_HOST=${RADIANT_RPC_HOST:-host.docker.internal}

cli() { "$RADIANT_CLI" -datadir="$RADIANT_DATADIR" -conf="$RADIANT_CONF" $RADIANT_NET "$@"; }

# RPC credentials + port straight from the node's config (no secrets in the repo).
CONF_FILE="$RADIANT_DATADIR/$RADIANT_CONF"
RPC_USER=$(grep -E '^rpcuser=' "$CONF_FILE" | head -1 | cut -d= -f2-)
RPC_PASS=$(grep -E '^rpcpassword=' "$CONF_FILE" | head -1 | cut -d= -f2-)
RPC_PORT=$(grep -E '^rpcport=' "$CONF_FILE" | head -1 | cut -d= -f2-)
RPC_PORT=${RPC_PORT:-17443}

echo "==> node: $(cli getblockchaininfo | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d["chain"],"height",d["blocks"])')"

echo "==> selecting a spendable P2PKH UTXO"
read -r TXID VOUT SATS SPK < <(
  cli listunspent 1 9999999 | python3 -c '
import sys, json
for u in json.load(sys.stdin):
    spk = u.get("scriptPubKey", "")
    if u.get("spendable") and spk.startswith("76a914") and spk.endswith("88ac") and u.get("amount", 0) > 0.1:
        print(u["txid"], u["vout"], int(round(u["amount"] * 1e8)), spk)
        break
else:
    sys.exit("no suitable spendable P2PKH UTXO found")
'
)
SPK_ADDR=$(cli listunspent 1 9999999 | python3 -c "import sys,json;print(next(u['address'] for u in json.load(sys.stdin) if u['txid']=='$TXID' and u['vout']==$VOUT))")
echo "    utxo=$TXID:$VOUT  value=$SATS photons  addr=$SPK_ADDR"

WIF=$(cli dumpprivkey "$SPK_ADDR")
DEST=$(cli getnewaddress)
echo "    dest=$DEST"

echo "==> build + sign + testmempoolaccept via dotnet SDK container"
docker run --rm -u "$(id -u):$(id -g)" \
  -e HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  -e RADIANT_RPC_URL="http://$RPC_HOST:$RPC_PORT/" \
  -e RADIANT_RPC_USER="$RPC_USER" \
  -e RADIANT_RPC_PASS="$RPC_PASS" \
  -e RADIANT_TEST_UTXO_TXID="$TXID" \
  -e RADIANT_TEST_UTXO_VOUT="$VOUT" \
  -e RADIANT_TEST_UTXO_SATS="$SATS" \
  -e RADIANT_TEST_UTXO_SPK="$SPK" \
  -e RADIANT_TEST_UTXO_WIF="$WIF" \
  -e RADIANT_TEST_DEST_ADDR="$DEST" \
  -v "$ROOT":/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/Dxs.Bsv.Tests/Dxs.Bsv.Tests.csproj \
    --filter 'FullyQualifiedName~LiveRadiantSigningTests' \
    --nologo

echo "==> RESULT: ACCEPTED ✓ — Radiant sighash verified end-to-end against the node"
