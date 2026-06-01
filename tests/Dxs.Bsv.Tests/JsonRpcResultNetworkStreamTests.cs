using System;
using System.IO;
using System.Text;

using Dxs.Bsv.Rpc.Streams;

using Xunit;

namespace Dxs.Bsv.Tests;

public class JsonRpcResultNetworkStreamTests
{
    private static byte[] DrainAll(string jsonRpcResponse)
    {
        var src = new MemoryStream(Encoding.ASCII.GetBytes(jsonRpcResponse));
        using var stream = new JsonRpcResultNetworkStream(src);

        using var outp = new MemoryStream();
        var buf = new byte[64];
        int n;
        // The stream throws "Too low count" on a 0-length read, so always pass >0
        // and stop when a read returns 0 (payload exhausted).
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            outp.Write(buf, 0, n);

        return outp.ToArray();
    }

    [Fact]
    public void DecodesRadiantCompactJson_NoSpaceAfterColon()
    {
        // Radiant / Bitcoin-Core wire format: compact, no space after the colon.
        // This is the exact shape that broke the upstream literal matcher and
        // surfaced as EndOfStreamException in BlockReader.
        var payloadHex = "00000020deadbeef";
        var response = $"{{\"result\":\"{payloadHex}\",\"error\":null,\"id\":\"t\"}}";

        var bytes = DrainAll(response);

        Assert.Equal(payloadHex, Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void DecodesBsvStyleJson_SpaceAfterColon()
    {
        // BSV nodes historically inserted a space after the colon — must still work.
        var payloadHex = "abcdef0123456789";
        var response = $"{{\"result\": \"{payloadHex}\",\"error\":null,\"id\":1}}";

        var bytes = DrainAll(response);

        Assert.Equal(payloadHex, Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void DecodesWithLeadingAndInteriorWhitespace()
    {
        var payloadHex = "0011223344556677";
        var response = $"  {{ \"result\" :  \"{payloadHex}\" , \"error\":null }}";

        var bytes = DrainAll(response);

        Assert.Equal(payloadHex, Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void DecodesUppercaseHexPayload()
    {
        var response = "{\"result\":\"DEADBEEF\",\"error\":null,\"id\":\"t\"}";

        var bytes = DrainAll(response);

        Assert.Equal("deadbeef", Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void DecodesLargePayloadSpanningInternalBuffer()
    {
        // Exceed MaxBufferSize (8 KiB) so the decode loops across multiple reads.
        var sb = new StringBuilder();
        for (var i = 0; i < 20_000; i++) sb.Append("ab");
        var payloadHex = sb.ToString();
        var response = $"{{\"result\":\"{payloadHex}\",\"error\":null,\"id\":\"t\"}}";

        var bytes = DrainAll(response);

        Assert.Equal(payloadHex.Length / 2, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0xab, b));
    }
}
