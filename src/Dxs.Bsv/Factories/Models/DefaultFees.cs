namespace Dxs.Bsv.Factories.Models;

public static class DefaultFees
{
    // Radiant (RXD) fee policy.
    //
    // Radiant's mainnet minimum-relay floor is 10,000 photons/byte after the V2
    // fork (active from block 410,000). 1 RXD = 100,000,000 photons (same scale
    // as a BSV satoshi), so the values below are expressed in the same units the
    // tx builder already uses: `Rate` is photons-per-byte, `Total` is in RXD.
    //
    // NOTE: BSV upstream used Rate = 0.05 sat/byte — far below Radiant's floor.
    // Sending a Radiant tx at the old rate would be rejected by the node.

    /// <summary>Per-byte fee rate, in photons/byte. Radiant V2 min-relay floor.</summary>
    public const decimal Rate = 10_000m;

    /// <summary>
    /// Flat fee used by FeeType.Constant, in RXD. ~2,500,000 photons covers a
    /// typical ~250-byte P2PKH transaction at the 10,000 photons/byte floor.
    /// </summary>
    public const decimal Total = 2_500_000m / 100_000_000m;
}
