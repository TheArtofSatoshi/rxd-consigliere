using System;

namespace Dxs.Bsv.Rpc.Streams;

/// <summary>
/// Thrown when a JSON-RPC response cannot be decoded into a result payload: a
/// malformed/unexpected envelope, an error envelope
/// (<c>{"result":null,"error":{…}}</c>), a non-string <c>result</c>, a non-hex
/// character in the payload, or a stream that ended mid-payload (truncated).
///
/// Having a dedicated exception — rather than swallowing everything and reporting a
/// clean end-of-stream — keeps the real cause visible. Previously these conditions
/// collapsed into a context-free <see cref="System.IO.EndOfStreamException"/> thrown by
/// <c>BlockReader.ReadHeader</c> on its first read, which is exactly what made the
/// compact-JSON prefix bug (see RADIANT_ADAPTATION.md, section "M1b") so hard to
/// diagnose.
/// </summary>
public class RpcResponseException : Exception
{
    public RpcResponseException(string message) : base(message) { }

    public RpcResponseException(string message, Exception innerException) : base(message, innerException) { }
}
