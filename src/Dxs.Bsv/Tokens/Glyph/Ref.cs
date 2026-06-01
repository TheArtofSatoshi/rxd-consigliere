using System;

namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// A Radiant induction ref — the on-chain identity of a Glyph token. Pushed by
/// OP_PUSHINPUTREF (0xd0, normal/fungible) or OP_PUSHINPUTREFSINGLETON (0xd1,
/// singleton/non-fungible) as 36 inline bytes: a 32-byte txid (internal,
/// little-endian byte order) followed by a 4-byte little-endian output index.
///
/// This replaces the BSV "TokenId" concept: Radiant token provenance is enforced
/// at the consensus/VM level by these refs, so there is no Back-to-Genesis trace.
/// </summary>
public readonly struct Ref : IEquatable<Ref>
{
    public Ref(ReadOnlySpan<byte> bytes36, bool isSingleton)
    {
        if (bytes36.Length != OutpointSize)
            throw new ArgumentException($"A ref must be {OutpointSize} bytes, got {bytes36.Length}", nameof(bytes36));

        Bytes = bytes36.ToArray();
        IsSingleton = isSingleton;
    }

    public const int OutpointSize = 36;
    public const int TxIdSize = 32;

    /// <summary>Raw 36 bytes exactly as they appear in script (txid LE + vout LE).</summary>
    public byte[] Bytes { get; }

    /// <summary>True if pushed by OP_PUSHINPUTREFSINGLETON (non-fungible).</summary>
    public bool IsSingleton { get; }

    /// <summary>
    /// Hex of the raw 36-byte outpoint as stored in script. This is the compact
    /// form commonly used to key a ref across the Radiant/Glyph tooling.
    /// </summary>
    public string OutpointHex => Convert.ToHexStringLower(Bytes);

    /// <summary>Output index (little-endian uint32 of the trailing 4 bytes).</summary>
    public uint Vout => BitConverter.ToUInt32(Bytes, TxIdSize);

    /// <summary>
    /// Big-endian txid hex (the form shown by block explorers): the 32-byte
    /// internal txid reversed.
    /// </summary>
    public string TxId
    {
        get
        {
            Span<byte> be = stackalloc byte[TxIdSize];
            for (var i = 0; i < TxIdSize; i++)
                be[i] = Bytes[TxIdSize - 1 - i];
            return Convert.ToHexStringLower(be);
        }
    }

    /// <summary>Human-friendly identity: explorer-style "txid:vout".</summary>
    public string Value => $"{TxId}:{Vout}";

    public bool Equals(Ref other)
    {
        if (IsSingleton != other.IsSingleton) return false;
        if (Bytes is null || other.Bytes is null) return ReferenceEquals(Bytes, other.Bytes);
        return Bytes.AsSpan().SequenceEqual(other.Bytes);
    }

    public override bool Equals(object obj) => obj is Ref other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsSingleton);
        if (Bytes != null)
            hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{(IsSingleton ? "singleton" : "ref")} {OutpointHex}";

    public static bool operator ==(Ref left, Ref right) => left.Equals(right);
    public static bool operator !=(Ref left, Ref right) => !left.Equals(right);
}
