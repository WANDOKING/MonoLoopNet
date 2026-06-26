namespace MonoLoop.Server.Protocol;

using System;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// <see cref="INetworkProtocol"/>을 구현하는 아무것도 하지 않는 프로토콜입니다.
/// 상태가 존재하지 않는 프로토콜이며, 싱글턴 클래스입니다.
/// </summary>
internal sealed class NullNetworkProtocol : INetworkProtocol
{
    private NullNetworkProtocol()
    {
    }

    public static NullNetworkProtocol Instance { get; } = new NullNetworkProtocol();

    /// <inheritdoc/>
    public byte[] EncapsulatePayload(byte[] payload)
    {
        return payload;
    }

    /// <inheritdoc/>
    public bool TryDecapsulatePayload(ReadOnlySpan<byte> receivedData, [NotNullWhen(true)] out byte[]? decapsulatedPayload, out int processedBytesCount)
    {
        if (receivedData.Length == 0)
        {
            decapsulatedPayload = null;
            processedBytesCount = 0;
            return false;
        }

        decapsulatedPayload = receivedData.ToArray();
        processedBytesCount = receivedData.Length;
        return true;
    }
}
