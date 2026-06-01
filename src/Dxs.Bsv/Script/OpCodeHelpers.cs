namespace Dxs.Bsv.Script;

public static class OpCodeHelpers
{
    /// <summary>
    /// Size in bytes of a Radiant input ref carried inline by the "push ref"
    /// and ref-guard opcodes: 32-byte txid + 4-byte vout.
    /// </summary>
    public const int InputRefSize = 36;

    public static bool IsOpCode(this byte opCodeNum, out OpCode opCode)
    {
        if (opCodeNum is (byte)OpCode.OP_0 or >= (byte)OpCode.OP_PUSHDATA1 and <= (byte)OpCode.OP_INVALIDOPCODE)
        {
            opCode = (OpCode)opCodeNum;
            return true;
        }

        opCode = OpCode.OP_INVALIDOPCODE;

        return false;
    }

    /// <summary>
    /// True for the Radiant opcodes that are followed by exactly
    /// <see cref="InputRefSize"/> inline bytes in the script — the two push-ref
    /// opcodes plus the require/disallow ref guards. This mirrors the node
    /// tokenizer (GetScriptOp: nSize = 36 for {0xd0,0xd1,0xd2,0xd3,0xd8}) and
    /// RXinDexer's `glyph.py` push scanner. A scanner MUST skip these 36 bytes
    /// or it will desync — e.g. read a singleton (NFT) ref's payload as opcodes.
    /// </summary>
    public static bool ConsumesInlineRef(this OpCode opCode)
        => opCode is OpCode.OP_PUSHINPUTREF
            or OpCode.OP_REQUIREINPUTREF
            or OpCode.OP_DISALLOWPUSHINPUTREF
            or OpCode.OP_DISALLOWPUSHINPUTREFSIBLING
            or OpCode.OP_PUSHINPUTREFSINGLETON;

    /// <summary>
    /// True only for the two opcodes that actually PUSH a ref a token output
    /// carries as its identity: OP_PUSHINPUTREF (0xd0, normal/fungible) and
    /// OP_PUSHINPUTREFSINGLETON (0xd8, singleton/NFT). The guard opcodes
    /// (require/disallow) reference a ref but do not establish token identity.
    /// </summary>
    public static bool IsRefPush(this OpCode opCode)
        => opCode is OpCode.OP_PUSHINPUTREF or OpCode.OP_PUSHINPUTREFSINGLETON;

    /// <summary>True only for OP_PUSHINPUTREFSINGLETON (0xd8) — a singleton/NFT ref.</summary>
    public static bool IsRefSingleton(this OpCode opCode)
        => opCode is OpCode.OP_PUSHINPUTREFSINGLETON;
}
