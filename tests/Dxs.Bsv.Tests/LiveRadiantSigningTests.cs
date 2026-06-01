#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Dxs.Bsv;
using Dxs.Bsv.Models;
using Dxs.Bsv.Script;
using Dxs.Bsv.Transactions.Build;

using Xunit;

namespace Dxs.Bsv.Tests;

/// <summary>
/// Opt-in integration test proving Consigliere's transaction signing produces a
/// CONSENSUS-VALID Radiant signature — i.e. the Radiant BIP143 preimage
/// (BSV FORKID + the extra hashOutputHashes field, see
/// <see cref="RadiantSignatureHash"/>) is correct. It builds + signs a real
/// P2PKH spend of a live regtest UTXO with <see cref="TransactionBuilder"/>, then
/// submits it to the node's <c>testmempoolaccept</c> — which runs full script
/// verification WITHOUT broadcasting (non-mutating). <c>allowed:true</c> is proof
/// the signature verified on-chain; a wrong preimage yields
/// <c>mandatory-script-verify-flag-failed</c>.
///
/// Skipped unless the UTXO inputs are supplied via env (keeps the WIF out of the
/// repo + the normal unit run hermetic):
///   RADIANT_RPC_URL, RADIANT_RPC_USER, RADIANT_RPC_PASS,
///   RADIANT_TEST_UTXO_TXID, RADIANT_TEST_UTXO_VOUT,
///   RADIANT_TEST_UTXO_SATS, RADIANT_TEST_UTXO_SPK (scriptPubKey hex),
///   RADIANT_TEST_UTXO_WIF, RADIANT_TEST_DEST_ADDR
/// </summary>
public class LiveRadiantSigningTests
{
    private static string? Env(string k) => Environment.GetEnvironmentVariable(k);

    [Fact]
    public async Task SignedP2pkhSpend_IsAcceptedByTestMempoolAccept()
    {
        var rpcUrl = Env("RADIANT_RPC_URL");
        var wif = Env("RADIANT_TEST_UTXO_WIF");
        var txid = Env("RADIANT_TEST_UTXO_TXID");
        var spk = Env("RADIANT_TEST_UTXO_SPK");
        var dest = Env("RADIANT_TEST_DEST_ADDR");
        if (string.IsNullOrWhiteSpace(rpcUrl) || string.IsNullOrWhiteSpace(wif) ||
            string.IsNullOrWhiteSpace(txid) || string.IsNullOrWhiteSpace(spk) ||
            string.IsNullOrWhiteSpace(dest))
            return; // opt-in: not configured, skip.

        var vout = uint.Parse(Env("RADIANT_TEST_UTXO_VOUT") ?? "0");
        var sats = ulong.Parse(Env("RADIANT_TEST_UTXO_SATS")!); // photons
        var signer = new PrivateKey(wif!, Network.Testnet); // regtest shares testnet params

        // Spend the whole UTXO minus a fee to a single P2PKH destination.
        // Radiant min-relay floor is 10,000 photons/byte; a 1-in/1-out P2PKH is
        // ~192 bytes → ~1.92M photons. Use 3,000,000 for comfortable headroom so
        // the ONLY thing under test is signature/preimage validity, not fee.
        var fee = 3_000_000UL;
        Assert.True(sats > fee, "utxo too small for the fee");

        var builder = TransactionBuilder.Init();
        builder.AddP2PkhOutput(sats - fee, new Address(dest!));

        var outPoint = new OutPoint(
            txid!,
            signer.P2PkhAddress,
            tokenId: null,
            satoshis: sats,
            vout: vout,
            scriptPubKeyHex: spk!,
            scriptType: ScriptType.P2PKH);
        builder.AddInput(outPoint, signer);

        var tx = builder.SignAndBuildTransaction(Network.Testnet);
        var rawHex = tx.Raw.ToHexString();

        var (allowed, reason) = await TestMempoolAccept(rpcUrl!, Env("RADIANT_RPC_USER")!, Env("RADIANT_RPC_PASS")!, rawHex);

        Assert.True(allowed,
            $"testmempoolaccept rejected the signed tx — preimage likely wrong. reason: {reason}");
    }

    private static async Task<(bool allowed, string reason)> TestMempoolAccept(
        string url, string user, string pass, string rawHex)
    {
        using var http = new HttpClient();
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}"));
        http.DefaultRequestHeaders.Add("Authorization", $"Basic {auth}");

        var body = "{\"jsonrpc\":\"1.0\",\"id\":\"t\",\"method\":\"testmempoolaccept\",\"params\":[[\"" + rawHex + "\"]]}";
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var resp = await http.PostAsync(url, content);
        var json = await resp.Content.ReadAsStringAsync();

        // Parse minimally: look for "allowed":true and "reject-reason".
        var allowed = json.Contains("\"allowed\":true");
        var reason = json;
        var ri = json.IndexOf("reject-reason", StringComparison.Ordinal);
        if (ri >= 0) reason = json.Substring(ri, Math.Min(120, json.Length - ri));
        return (allowed, reason);
    }
}
