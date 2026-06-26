namespace MonoLoop.Server.Protocol;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Application Layer의 네트워크 프로토콜에 대한 추상화입니다.
/// </summary>
public interface INetworkProtocol
{
    /// <summary>
    /// 송신하고자 하는 페이로드를 네트워크로 전송하기 위한 데이터로 캡슐화합니다.
    /// 수신 측에서 메시지를 정확히 구분할 수 있도록, 페이로드에 길이 정보나 구분자 등을 추가하는 등의 처리를 수행합니다.
    /// </summary>
    /// <param name="payload">
    /// 송신할 페이로드입니다.
    /// 직렬화된 JSON 데이터, protobuf로 직렬화된 데이터, 또는 단순한 바이트 배열 등, 어떠한 형태의 데이터도 페이로드로 사용할 수 있습니다.
    /// </param>
    /// <returns>캡슐화된 데이터입니다. 소켓 버퍼에 쓰기됩니다.</returns>
    public byte[] EncapsulatePayload(byte[] payload);

    /// <summary>
    /// 수신된 데이터에서 페이로드를 디캡슐화합니다.
    /// </summary>
    /// <remarks>
    /// </remarks>
    /// <param name="receivedData"></param>
    /// <param name="decapsulatedPayload"></param>
    /// <param name="processedBytesCount"></param>
    /// <returns>페이로드를 디캡슐화하였을 경우 true를, 그렇지 않으면 false를 반환합니다.</returns>
    public bool TryDecapsulatePayload(ReadOnlySpan<byte> receivedData, [NotNullWhen(true)] out byte[]? decapsulatedPayload, out int processedBytesCount);
}
