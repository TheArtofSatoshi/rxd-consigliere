using NBitcoin;

namespace Dxs.Bsv.ScriptEvaluation;

public sealed class BsvScriptExecutionPolicy
{
    public static BsvScriptExecutionPolicy RepoDefault { get; } = new();

    public ScriptVerify ScriptVerify { get; init; } = ScriptVerify.Mandatory | ScriptVerify.ForkId;
    public bool AllowOpReturn { get; init; } = true;

    /// <summary>
    /// Sighash dialect. <c>true</c> (default) computes Radiant's BIP143 preimage,
    /// which inserts <c>hashOutputHashes</c> before <c>hashOutputs</c> (see
    /// <see cref="Transactions.Build.RadiantSignatureHash"/>); <c>false</c> uses
    /// the plain BSV/BCH FORKID preimage. This fork is Radiant-first, so the
    /// default is Radiant. Set <c>false</c> only to validate genuine BSV
    /// transactions (e.g. the vendored DSTAS conformance vectors), whose
    /// signatures were produced with the BSV preimage.
    /// </summary>
    public bool UseRadiantSigHash { get; init; } = true;

    /// <summary>BSV/BCH dialect policy — plain FORKID preimage, no hashOutputHashes.</summary>
    public static BsvScriptExecutionPolicy BsvCompat { get; } = new() { UseRadiantSigHash = false };
}
