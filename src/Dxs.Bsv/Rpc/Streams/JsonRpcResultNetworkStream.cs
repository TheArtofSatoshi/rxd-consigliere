using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dxs.Bsv.Rpc.Streams;

public class JsonRpcResultNetworkStream(Stream stream) : Stream
{
    private static readonly Dictionary<byte, byte> Utf8ToHex = new()
    {
        //Numbers
        { 0x30, 0 },
        { 0x31, 1 },
        { 0x32, 2 },
        { 0x33, 3 },
        { 0x34, 4 },
        { 0x35, 5 },
        { 0x36, 6 },
        { 0x37, 7 },
        { 0x38, 8 },
        { 0x39, 9 },

        //Capital Letters
        { 0x41, 10 },
        { 0x42, 11 },
        { 0x43, 12 },
        { 0x44, 13 },
        { 0x45, 14 },
        { 0x46, 15 },

        //small Letters
        { 0x61, 10 },
        { 0x62, 11 },
        { 0x63, 12 },
        { 0x64, 13 },
        { 0x65, 14 },
        { 0x66, 15 },
    };

    // The hex payload is the value of the first JSON member of a Bitcoin/Radiant
    // JSON-RPC response: {"result":"<hex>","error":null,"id":...}. Radiant (a Bitcoin
    // Core fork) emits compact JSON with no space after the colon; BSV nodes inserted
    // one. We scan for the "result" key tolerant of optional whitespace so both shapes
    // decode. The previous code matched the literal `{"result": "` (with a space) and,
    // on Radiant's compact form, silently produced zero payload bytes — which surfaced
    // downstream as EndOfStreamException in BlockReader.ReadHeader.
    private static readonly byte[] ResultKey = "result"u8.ToArray();
    private const byte Quote = (byte)'"';

    private enum Scan { OpenBrace, KeyOpenQuote, KeyName, KeyCloseQuote, Colon, ValueOpenQuote }

    private Scan _scan = Scan.OpenBrace;
    private int _keyMatchIdx;

    private static bool IsJsonWhitespace(byte b) => b is 0x20 or 0x09 or 0x0a or 0x0d;

    private const int MaxBufferSize = 1024 * 4 * 2;

    private readonly byte[] _buffer = new byte[MaxBufferSize];

    private readonly byte[] _payloadBuffer = new byte[MaxBufferSize / 2];
    private int _payloadBufferReadCursor;
    private int _payloadBufferWriteCursor;

    private readonly byte[] _hexCharBuffer = new byte[2];
    private int _hexCharIdx;

    private bool _payloadStarted;
    private bool _payloadReadFinished;
    private bool _networkStreamFinished;

    private int _availableBytes;

    public override void Flush() => stream.Flush();

    // Errors are deliberately NOT swallowed here. This is a one-shot Stream over a
    // single RPC response and has no retry contract: returning false means EOF to the
    // consumer (BitcoinStreamReader treats a 0-byte read as end-of-stream and throws).
    // So turning a malformed envelope or a mid-read IO failure into `return false`
    // doesn't "retry" anything — it disguises the real failure as a clean EOF, which
    // then resurfaces as a context-free EndOfStreamException in BlockReader.ReadHeader
    // (and, for a truncated large block, as a silently short read that corrupts
    // indexing). We let the descriptive exceptions below propagate out of Read() so the
    // caller sees the actual cause. See RADIANT_ADAPTATION.md, section "M1b".
    private bool ReadStream()
    {
        // Once the closing quote of the result string is seen we are done; ignore any
        // trailing envelope JSON (",\"error\":null,...}") regardless of how stream
        // chunk boundaries fall, and report EOF to the consumer.
        if (_networkStreamFinished || _payloadReadFinished)
            return false;

        var actualCount = stream.Read(_buffer, 0, _buffer.Length);

        if (actualCount is 0 or -1)
        {
            _networkStreamFinished = true;

            if (!_payloadReadFinished)
                throw new RpcResponseException(
                    "RPC stream ended before the result payload was fully read (truncated response).");

            return false;
        }

        for (var i = 0; i < actualCount; i++)
        {
            var b = _buffer[i];

            if (!_payloadStarted)
            {
                // Whitespace-tolerant scan up to the opening quote of the "result"
                // string value, after which the hex payload begins.
                switch (_scan)
                {
                    case Scan.OpenBrace:
                        if (IsJsonWhitespace(b)) break;
                        if (b != (byte)'{') throw new RpcResponseException($"Unexpected RPC response, expected '{{' got 0x{b:x2}");
                        _scan = Scan.KeyOpenQuote;
                        break;

                    case Scan.KeyOpenQuote:
                        if (IsJsonWhitespace(b)) break;
                        if (b != Quote) throw new RpcResponseException($"Unexpected RPC response, expected '\"' got 0x{b:x2}");
                        _scan = Scan.KeyName;
                        _keyMatchIdx = 0;
                        break;

                    case Scan.KeyName:
                        if (b != ResultKey[_keyMatchIdx]) throw new RpcResponseException("Unexpected RPC response, first member is not \"result\"");
                        _keyMatchIdx++;
                        if (_keyMatchIdx == ResultKey.Length) _scan = Scan.KeyCloseQuote;
                        break;

                    case Scan.KeyCloseQuote:
                        if (b != Quote) throw new RpcResponseException("Unexpected RPC response, malformed \"result\" key");
                        _scan = Scan.Colon;
                        break;

                    case Scan.Colon:
                        if (IsJsonWhitespace(b)) break;
                        if (b != (byte)':') throw new RpcResponseException($"Unexpected RPC response, expected ':' got 0x{b:x2}");
                        _scan = Scan.ValueOpenQuote;
                        break;

                    case Scan.ValueOpenQuote:
                        if (IsJsonWhitespace(b)) break;
                        // A successful getblock always returns the block as a hex string.
                        // A non-string here means an error envelope
                        // ({"result":null,"error":{...}}) or otherwise malformed
                        // response — surface the raw text so the real error is visible.
                        if (b != Quote) throw new RpcResponseException(DescribeNonStringResult(actualCount));
                        _payloadStarted = true;
                        break;
                }
            }
            else
            {
                if (b == Quote)
                {
                    if (_hexCharIdx == 1)
                        throw new RpcResponseException(
                            "Unexpected RPC response, \"result\" payload ended on a half-byte (odd number of hex characters).");

                    _payloadReadFinished = true;
                    return false;
                }

                if (!Utf8ToHex.TryGetValue(b, out var nibble))
                    throw new RpcResponseException($"Unexpected RPC response, non-hex character 0x{b:x2} in \"result\" payload");

                _hexCharBuffer[_hexCharIdx] = nibble;

                if (_hexCharIdx == 1)
                {
                    _payloadBuffer[_payloadBufferWriteCursor] = BinaryHelpers.HexToByte(_hexCharBuffer[0], _hexCharBuffer[1]);

                    _payloadBufferWriteCursor++;

                    if (_payloadBufferWriteCursor == _payloadBuffer.Length)
                        _payloadBufferWriteCursor = 0;

                    _availableBytes++;
                    _hexCharIdx = 0;
                }
                else
                {
                    _hexCharIdx = 1;
                }
            }
        }

        return true;
    }

    // Decodes the bytes read so far as text and embeds them in the exception message so
    // an error envelope's {"error":{...}} (or any other non-string result) is reported
    // verbatim instead of hidden behind a downstream EndOfStreamException. Capped so a
    // pathological response can't produce a multi-megabyte message.
    private string DescribeNonStringResult(int bufferedCount)
    {
        const int max = 1024;

        var raw = Encoding.UTF8.GetString(_buffer, 0, bufferedCount);
        if (raw.Length > max) raw = raw[..max] + "…";

        return $"Unexpected RPC response, \"result\" is not a string (expected block hex). Raw response: {raw}";
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            throw new Exception("Too low count");
        }

        var readBytes = 0;

        if (_availableBytes > 0)
        {
            readBytes = Math.Min(count, _availableBytes);
            Buffer.BlockCopy(_payloadBuffer, _payloadBufferReadCursor, buffer, offset, readBytes);

            SyncPayloadReadCursor(readBytes);
        }

        var needMore = count - readBytes;

        if (needMore > 0)
        {
            while (_availableBytes < needMore && ReadStream()) { }

            var moreBytes = Math.Min(needMore, _availableBytes);

            if (moreBytes > 0)
            {
                Buffer.BlockCopy(_payloadBuffer, _payloadBufferReadCursor, buffer, offset + readBytes, moreBytes);

                SyncPayloadReadCursor(moreBytes);
                readBytes += moreBytes;
            }
        }

        return readBytes;
    }

    private void SyncPayloadReadCursor(int readBytes)
    {
        IncreasePayloadReadCursor(readBytes);
        _availableBytes -= readBytes;

        Position += readBytes;
    }

    private void IncreasePayloadReadCursor(int readBytes)
    {
        _payloadBufferReadCursor += readBytes;

        if (_payloadBufferReadCursor >= _payloadBuffer.Length)
            _payloadBufferReadCursor -= _payloadBuffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

    public override void SetLength(long value) => stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

    public override bool CanRead => true;
    public override bool CanSeek => stream.CanSeek;

    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;

    public override long Position { get; set; }
}
