using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.Text;

namespace Dxs.Bsv.Tokens.Glyph;

/// <summary>
/// Parses Glyph token envelopes (v1 + v2 Style A/B) and extracts normalised
/// token info from CBOR reveal metadata. A faithful C# port of RXinDexer
/// `electrumx/lib/glyph.py` (parse_glyph_envelope / _parse_script_pushes /
/// parse_glyph_metadata / extract_token_info) so the two indexers agree.
///
/// Envelope formats (see the Python docstring for the byte layouts):
///   • v1 / v2 Style B — 'gly' is a standalone 3-byte push; the next push is
///     either raw CBOR (reveal) or version||flags||... (v2B commit).
///   • v2 Style A — 'gly' is the prefix of a larger push, immediately followed
///     by a version byte and flags; reveal metadata is in the next push,
///     commit hash follows inline.
/// </summary>
public static class GlyphParser
{
    public static readonly byte[] Magic = "gly"u8.ToArray(); // 0x67 0x6c 0x79

    /// <summary>True if the raw script bytes contain the Glyph magic anywhere.</summary>
    public static bool ContainsMagic(ReadOnlySpan<byte> script)
        => script.IndexOf(Magic) >= 0;

    /// <summary>
    /// Parse a Glyph envelope from raw script bytes (the full scriptSig or
    /// scriptPubKey). Returns null if no valid Glyph envelope is present.
    /// </summary>
    public static GlyphEnvelope ParseEnvelope(ReadOnlySpan<byte> script)
    {
        if (script.IndexOf(Magic) < 0)
            return null;

        List<byte[]> pushes;
        try
        {
            pushes = ParseScriptPushes(script);
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < pushes.Count; i++)
        {
            var push = pushes[i];

            // Case A: 'gly' as a standalone 3-byte push (v1 or v2 Style B).
            if (push.Length == 3 && PrefixIsMagic(push))
            {
                if (i + 1 >= pushes.Count)
                    continue;

                var payload = pushes[i + 1];
                if (payload == null || payload.Length < 2)
                    continue;

                // Most common: the next push is raw CBOR (a reveal).
                if (TryReadCborMap(payload, out var versionFromCbor))
                {
                    return new GlyphEnvelope
                    {
                        Version = versionFromCbor ?? GlyphVersion.V1,
                        Flags = GlyphEnvelopeFlags.IsReveal,
                        IsReveal = true,
                        MetadataBytes = payload,
                    };
                }

                // Otherwise try a v2 structured payload (version||flags||...).
                if (payload[0] is GlyphVersion.V1 or GlyphVersion.V2)
                {
                    var structured = ParseV2Structured(payload);
                    if (structured != null)
                        return structured;
                }

                continue;
            }

            // Case B: 'gly' is the prefix of a larger push (v2 Style A).
            if (push.Length > 3 && PrefixIsMagic(push))
            {
                var inner = push.AsSpan(3);
                if (inner.Length < 2)
                    continue;

                int version = inner[0];
                if (version is not (GlyphVersion.V1 or GlyphVersion.V2))
                    continue;

                var flags = (GlyphEnvelopeFlags)inner[1];
                var isReveal = (flags & GlyphEnvelopeFlags.IsReveal) != 0;

                if (isReveal)
                {
                    byte[] metadata = null;
                    IReadOnlyList<byte[]> fileChunks = null;
                    if (i + 1 < pushes.Count)
                    {
                        metadata = pushes[i + 1];
                        if (i + 2 < pushes.Count)
                            fileChunks = pushes.GetRange(i + 2, pushes.Count - (i + 2));
                    }

                    return new GlyphEnvelope
                    {
                        Version = version,
                        Flags = flags,
                        IsReveal = true,
                        MetadataBytes = metadata,
                        FileChunks = fileChunks,
                    };
                }

                return ParseV2CommitInline(version, flags, inner[2..]);
            }
        }

        return null;
    }

    /// <summary>
    /// Decode the CBOR reveal metadata of an envelope into normalised token info.
    /// Returns null for non-reveals or undecodable metadata.
    /// </summary>
    public static GlyphTokenInfo ParseTokenInfo(GlyphEnvelope envelope)
    {
        if (envelope is not { IsReveal: true, MetadataBytes: { Length: > 0 } bytes })
            return null;

        Dictionary<string, object> meta;
        try
        {
            meta = ReadCborMap(bytes);
        }
        catch
        {
            return null;
        }

        if (meta == null)
            return null;

        return ExtractTokenInfo(meta, envelope.Version);
    }

    /// <summary>Convenience: parse the envelope and (if a reveal) its token info.</summary>
    public static GlyphTokenInfo ParseTokenInfo(ReadOnlySpan<byte> script)
        => ParseTokenInfo(ParseEnvelope(script));

    // ------------------------------------------------------------------
    // Script push extraction (mirrors _parse_script_pushes)
    // ------------------------------------------------------------------

    /// <summary>
    /// Extract the ordered list of data-push payloads from raw script bytes,
    /// skipping non-push opcodes and consuming the 36 inline bytes of the Radiant
    /// ref opcodes {0xd0,0xd1,0xd2,0xd3,0xd8}. Truncated pushes stop the scan.
    /// </summary>
    public static List<byte[]> ParseScriptPushes(ReadOnlySpan<byte> data)
    {
        var pushes = new List<byte[]>();
        var pos = 0;
        var length = data.Length;

        while (pos < length)
        {
            var op = data[pos];
            pos++;

            if (op == 0x00) // OP_0 / OP_FALSE
            {
                pushes.Add(Array.Empty<byte>());
            }
            else if (op == 0x4f) // OP_1NEGATE
            {
                pushes.Add(new byte[] { 0x81 });
            }
            else if (op >= 0x51 && op <= 0x60) // OP_1 .. OP_16
            {
                pushes.Add(new[] { (byte)(op - 0x50) });
            }
            else if (op >= 0x01 && op <= 0x4b) // OP_PUSHBYTES_N
            {
                var end = pos + op;
                if (end > length) break;
                pushes.Add(data.Slice(pos, op).ToArray());
                pos = end;
            }
            else if (op == 0x4c) // OP_PUSHDATA1
            {
                if (pos >= length) break;
                int dlen = data[pos]; pos += 1;
                var end = pos + dlen;
                if (end > length) break;
                pushes.Add(data.Slice(pos, dlen).ToArray());
                pos = end;
            }
            else if (op == 0x4d) // OP_PUSHDATA2
            {
                if (pos + 2 > length) break;
                int dlen = data[pos] | (data[pos + 1] << 8); pos += 2;
                var end = pos + dlen;
                if (end > length) break;
                pushes.Add(data.Slice(pos, dlen).ToArray());
                pos = end;
            }
            else if (op == 0x4e) // OP_PUSHDATA4
            {
                if (pos + 4 > length) break;
                long dlen = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
                pos += 4;
                var end = pos + dlen;
                if (end > length) break;
                pushes.Add(data.Slice(pos, (int)dlen).ToArray());
                pos = (int)end;
            }
            else if (op is 0xd0 or 0xd1 or 0xd2 or 0xd3 or 0xd8) // Radiant ref ops
            {
                pos += OpCodeInlineRefSize;
            }
            // else: non-push opcode (OP_RETURN, OP_3, OP_DROP, …) — skip.
        }

        return pushes;
    }

    private const int OpCodeInlineRefSize = 36;

    // ------------------------------------------------------------------
    // v2 structured / commit helpers (mirror the Python helpers)
    // ------------------------------------------------------------------

    private static GlyphEnvelope ParseV2Structured(byte[] payload)
    {
        if (payload.Length < 2)
            return null;

        int version = payload[0];
        var flags = (GlyphEnvelopeFlags)payload[1];
        if (version is not (GlyphVersion.V1 or GlyphVersion.V2))
            return null;

        var isReveal = (flags & GlyphEnvelopeFlags.IsReveal) != 0;

        if (isReveal)
        {
            byte[] metadata = payload.Length > 2 ? payload[2..] : null;
            return new GlyphEnvelope
            {
                Version = version,
                Flags = flags,
                IsReveal = true,
                MetadataBytes = metadata,
            };
        }

        return ParseV2CommitInline(version, flags, payload.AsSpan(2));
    }

    private static GlyphEnvelope ParseV2CommitInline(int version, GlyphEnvelopeFlags flags, ReadOnlySpan<byte> remainder)
    {
        string commitHash = null, contentRoot = null, controller = null;
        var pos = 0;

        if (pos + 32 <= remainder.Length)
        {
            commitHash = Convert.ToHexStringLower(remainder.Slice(pos, 32));
            pos += 32;

            if ((flags & GlyphEnvelopeFlags.HasContentRoot) != 0 && pos + 32 <= remainder.Length)
            {
                contentRoot = Convert.ToHexStringLower(remainder.Slice(pos, 32));
                pos += 32;
            }

            if ((flags & GlyphEnvelopeFlags.HasController) != 0 && pos + 36 <= remainder.Length)
            {
                controller = Convert.ToHexStringLower(remainder.Slice(pos, 36));
            }
        }

        return new GlyphEnvelope
        {
            Version = version,
            Flags = flags,
            IsReveal = false,
            CommitHash = commitHash,
            ContentRoot = contentRoot,
            Controller = controller,
        };
    }

    // ------------------------------------------------------------------
    // Token-info extraction (mirrors extract_token_info)
    // ------------------------------------------------------------------

    private static GlyphTokenInfo ExtractTokenInfo(Dictionary<string, object> meta, int envelopeVersion)
    {
        var version = meta.TryGetValue("v", out var vObj) && TryToLong(vObj, out var vLong)
            ? (int)vLong
            : envelopeVersion;

        var protocols = ReadProtocols(meta);

        // v1 legacy: infer protocols from the `type` string.
        if (protocols.Count == 0 && meta.TryGetValue("type", out var typeObj) && typeObj is string typeStr)
        {
            protocols = typeStr switch
            {
                "ft" => new List<GlyphProtocol> { GlyphProtocol.Ft },
                "nft" => new List<GlyphProtocol> { GlyphProtocol.Nft },
                "dat" => new List<GlyphProtocol> { GlyphProtocol.Dat },
                _ => new List<GlyphProtocol>(),
            };
        }

        var tokenType = protocols.ToTokenType();

        return new GlyphTokenInfo
        {
            Version = version,
            Protocols = protocols,
            TokenType = tokenType,
            TokenTypeName = tokenType.ToDisplayName(),
            Name = ReadString(meta, "name"),
            Ticker = ReadString(meta, "ticker") ?? ReadString(meta, "symbol"),
            Decimals = ReadLong(meta, "decimals"),
            Description = ReadString(meta, "desc") ?? ReadString(meta, "description"),
            Dmint = ReadDmint(meta),
        };
    }

    private static List<GlyphProtocol> ReadProtocols(Dictionary<string, object> meta)
    {
        var result = new List<GlyphProtocol>();
        if (meta.TryGetValue("p", out var pObj) && pObj is List<object> list)
        {
            foreach (var item in list)
                if (TryToLong(item, out var id))
                    result.Add((GlyphProtocol)id);
        }
        return result;
    }

    private static GlyphDmintInfo ReadDmint(Dictionary<string, object> meta)
    {
        if (!meta.TryGetValue("dmint", out var dObj) || dObj is not Dictionary<string, object> d)
            return null;

        return new GlyphDmintInfo
        {
            Algorithm = ReadLong(d, "algo") ?? ReadLong(d, "algorithm"),
            MaxHeight = ReadLong(d, "maxHeight") ?? ReadLong(d, "max_height"),
            Reward = ReadLong(d, "reward"),
            Target = d.TryGetValue("target", out var t) && t is byte[] tb ? tb : null,
        };
    }

    // ------------------------------------------------------------------
    // CBOR helpers (System.Formats.Cbor)
    // ------------------------------------------------------------------

    private static bool PrefixIsMagic(byte[] push)
        => push.Length >= 3 && push[0] == Magic[0] && push[1] == Magic[1] && push[2] == Magic[2];

    /// <summary>
    /// True if the bytes decode to a genuine, fully-consumed CBOR map; out param
    /// is the `v` field if present. Strictly requires the payload to *start* with
    /// a CBOR map and leave no trailing bytes — this is what keeps a script that
    /// merely contains the 3 bytes 'gly' (followed by arbitrary data) from being
    /// mis-detected as a Glyph reveal.
    /// </summary>
    private static bool TryReadCborMap(byte[] bytes, out int? version)
    {
        version = null;
        try
        {
            var map = ReadCborMap(bytes);
            if (map == null) return false;
            if (map.TryGetValue("v", out var vObj) && TryToLong(vObj, out var v))
                version = (int)v;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decode a top-level CBOR map into a Dictionary&lt;string,object&gt;, or null
    /// if the bytes do not begin with a map or contain trailing data after it.
    /// Values are decoded recursively: maps→Dictionary, arrays→List, text→string,
    /// byte strings→byte[], ints→long, bool/null/float passthrough.
    /// </summary>
    private static Dictionary<string, object> ReadCborMap(byte[] bytes)
    {
        var reader = new CborReader(bytes, CborConformanceMode.Lax);

        // Must genuinely start with a map; reject break/garbage/scalars up front.
        if (reader.PeekState() != CborReaderState.StartMap)
            return null;

        var value = ReadCborValue(reader) as Dictionary<string, object>;
        if (value == null)
            return null;

        // Reject payloads with trailing bytes after the map (not a clean reveal).
        if (reader.PeekState() != CborReaderState.Finished)
            return null;

        return value;
    }

    private static object ReadCborValue(CborReader reader)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
            {
                var count = reader.ReadStartMap();
                var dict = new Dictionary<string, object>();
                // count is null for indefinite-length maps; loop until EndMap.
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    var key = ReadMapKey(reader);
                    var val = ReadCborValue(reader);
                    if (key != null)
                        dict[key] = val;
                }
                reader.ReadEndMap();
                return dict;
            }
            case CborReaderState.StartArray:
            {
                reader.ReadStartArray();
                var list = new List<object>();
                while (reader.PeekState() != CborReaderState.EndArray)
                    list.Add(ReadCborValue(reader));
                reader.ReadEndArray();
                return list;
            }
            case CborReaderState.TextString:
                return reader.ReadTextString();
            case CborReaderState.ByteString:
                return reader.ReadByteString();
            case CborReaderState.UnsignedInteger:
            case CborReaderState.NegativeInteger:
                return reader.ReadInt64();
            case CborReaderState.Boolean:
                return reader.ReadBoolean();
            case CborReaderState.Null:
                reader.ReadNull();
                return null;
            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                return reader.ReadDouble();
            case CborReaderState.Tag:
                reader.ReadTag();           // unwrap tag, return tagged value
                return ReadCborValue(reader);
            default:
                reader.SkipValue();
                return null;
        }
    }

    private static string ReadMapKey(CborReader reader)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.TextString:
                return reader.ReadTextString();
            case CborReaderState.UnsignedInteger:
            case CborReaderState.NegativeInteger:
                return reader.ReadInt64().ToString();
            case CborReaderState.ByteString:
                return Convert.ToHexStringLower(reader.ReadByteString());
            default:
                // Non-scalar key — consume value to stay in sync, ignore entry.
                ReadCborValue(reader);
                return null;
        }
    }

    private static string ReadString(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var v) || v == null) return null;
        return v switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => v.ToString(),
        };
    }

    private static long? ReadLong(Dictionary<string, object> meta, string key)
        => meta.TryGetValue(key, out var v) && TryToLong(v, out var l) ? l : null;

    private static bool TryToLong(object v, out long result)
    {
        switch (v)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case ulong u when u <= long.MaxValue: result = (long)u; return true;
            default: result = 0; return false;
        }
    }
}
