using System;
using System.Collections.Generic;

using Dxs.Bsv.Script;

namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// Scans a Radiant script for induction refs (OP_PUSHINPUTREF /
/// OP_PUSHINPUTREFSINGLETON). The scanner walks the script the same way the node
/// tokenizer does — honouring every data-push length so a ref-opcode byte that
/// is merely part of pushed data is never mistaken for an opcode — and treats
/// the ref opcodes {0xd0,0xd1,0xd2,0xd3,0xd8} as special pushes that each
/// consume 36 inline bytes (GetScriptOp: nSize = 36 for that set).
///
/// A token-bearing output is recognised simply by the presence of one or more
/// refs; no Back-to-Genesis tracing is required (refs are consensus-enforced).
/// Only the two genuine push-ref opcodes (0xd0 normal, 0xd8 singleton) establish
/// a carried token identity and are returned; the require/disallow guards
/// (0xd1/0xd2/0xd3) are skipped over (their 36 bytes consumed) but not recorded.
/// </summary>
public static class RadiantScriptScanner
{
    /// <summary>
    /// Returns every carried ref in <paramref name="script"/>, in script order.
    /// Empty if the script contains none (e.g. plain P2PKH). Malformed/truncated
    /// pushes terminate the scan and return whatever was found before them.
    /// </summary>
    public static IReadOnlyList<Ref> ExtractRefs(ReadOnlySpan<byte> script)
    {
        List<Ref> refs = null;
        var pos = 0;

        while (pos < script.Length)
        {
            var opByte = script[pos];
            pos++;

            // Direct push: opcodes 0x01..0x4b push that many bytes inline.
            if (opByte >= 0x01 && opByte <= 0x4b)
            {
                if (!Skip(ref pos, opByte, script.Length)) break;
                continue;
            }

            var op = (OpCode)opByte;

            switch (op)
            {
                case OpCode.OP_PUSHDATA1:
                {
                    if (pos + 1 > script.Length) { pos = script.Length; break; }
                    int len = script[pos];
                    pos += 1;
                    if (!Skip(ref pos, len, script.Length)) pos = script.Length;
                    break;
                }
                case OpCode.OP_PUSHDATA2:
                {
                    if (pos + 2 > script.Length) { pos = script.Length; break; }
                    int len = script[pos] | (script[pos + 1] << 8);
                    pos += 2;
                    if (!Skip(ref pos, len, script.Length)) pos = script.Length;
                    break;
                }
                case OpCode.OP_PUSHDATA4:
                {
                    if (pos + 4 > script.Length) { pos = script.Length; break; }
                    long len = (uint)(script[pos] | (script[pos + 1] << 8) | (script[pos + 2] << 16) | (script[pos + 3] << 24));
                    pos += 4;
                    if (!Skip(ref pos, len, script.Length)) pos = script.Length;
                    break;
                }
                default:
                {
                    if (op.ConsumesInlineRef())
                    {
                        if (pos + OpCodeHelpers.InputRefSize > script.Length)
                        {
                            // Truncated ref — stop scanning.
                            pos = script.Length;
                            break;
                        }

                        if (op.IsRefPush())
                        {
                            (refs ??= new List<Ref>()).Add(
                                new Ref(script.Slice(pos, OpCodeHelpers.InputRefSize), op.IsRefSingleton()));
                        }

                        pos += OpCodeHelpers.InputRefSize;
                    }

                    // All other opcodes consume no inline data.
                    break;
                }
            }
        }

        return (IReadOnlyList<Ref>)refs ?? Array.Empty<Ref>();
    }

    /// <summary>True if the script pushes at least one carried ref (0xd0 or 0xd8).</summary>
    public static bool HasRef(ReadOnlySpan<byte> script)
    {
        foreach (var r in ExtractRefs(script))
        {
            _ = r;
            return true;
        }
        return false;
    }

    private static bool Skip(ref int pos, long count, int length)
    {
        if (pos + count > length)
            return false;
        pos += (int)count;
        return true;
    }
}
