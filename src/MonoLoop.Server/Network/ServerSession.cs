namespace MonoLoop.Server.Network;

using MonoLoop.Server.Protocol;
using System;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// 서버 세션입니다.
/// </summary>
public sealed class ServerSession
{
    private const int receiveBufferLength = 1024 * 4;
    private const int sendStreamCapacity = 1024 * 4;

    private readonly INetworkProtocol protocol;

    private readonly byte[] receiveBuffer;
    private readonly MemoryStream sendStream;
    private int receiveBufferPosition;

    internal ServerSession(ulong id, Socket socket, INetworkProtocol protocol)
    {
        this.Id = id;
        this.Socket = socket;
        this.protocol = protocol;

        receiveBuffer = new byte[receiveBufferLength];
        sendStream = new MemoryStream(sendStreamCapacity);
    }

    /// <summary>
    /// 세션의 상태입니다.
    /// </summary>
    public enum SessionState
    {
        /// <summary>
        /// NOTE: 현재 미사용
        /// 로그인 Flow를 만들 때 사용할 예정
        /// </summary>
        Joinning,

        /// <summary>
        /// 세션이 연결되었으며, 통신이 가능한 상태입니다.
        /// </summary>
        Connected,

        /// <summary>
        /// 세션의 연결이 종료되었습니다.
        /// </summary>
        Disconnected,

        /// <summary>
        /// 세션이 파괴되었습니다.
        /// </summary>
        Terminated,
    }

    /// <summary>
    /// 세션의 식별자입니다.
    /// </summary>
    public ulong Id { get; init; }

    /// <summary>
    /// 세션이 연결되어있는지 여부를 반환합니다.
    /// </summary>
    public bool IsConnected => State is SessionState.Connected;

    /// <summary>
    /// 세션의 로컬 엔드포인트입니다.
    /// </summary>
    public EndPoint? LocalEndPoint => Socket.LocalEndPoint;

    /// <summary>
    /// 세션의 리모트 엔드포인트입니다.
    /// </summary>
    public EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;

    /// <summary>
    /// 세션의 연결 상태입니다.
    /// </summary>
    public SessionState State { get; private set; } = SessionState.Connected;

    internal bool HasToSend => sendStream.Position > 0;

    internal Socket Socket { get; init; }

    /// <summary>
    /// 서버 세션을 종료합니다.
    /// </summary>
    public void Disconnect()
    {
        // InternalDisconnect에서 실질적인 삭제를 수행합니다.
        // 로직 수행 중 세션이 지워지지 않게 하기 위함입니다.
        State = SessionState.Disconnected;
    }

    /// <summary>
    /// 데이터를 전송합니다.
    /// </summary>
    /// <param name="data">전송할 데이터입니다.</param>
    /// <returns>전송 큐에 데이터를 큐잉 성공 여부입니다. 송신되었음을 보장하지는 않습니다.</returns>
    public bool Send(byte[] data)
    {
        if (IsConnected is false)
        {
            return false;
        }

        byte[] encapsulated = protocol.EncapsulatePayload(data);

        sendStream.Write(encapsulated);
        return true;
    }

    internal void InternalDisconnect()
    {
        // NOTE: 버퍼에 남아있는 데이터를 전송 시도하고 닫는다.
        // 서버가 연결 종료 사유를 클라이언트에게 전송한 후 연결을 종료할 수도 있기 때문이다.

        InternalSend();

        State = SessionState.Terminated;

        if (Socket.Connected)
        {
            Socket.Close();
        }

        Socket.Dispose();
        sendStream.Dispose();
    }

    internal IEnumerable<byte[]> InternalReceive()
    {
        while (true)
        {
            int receivedByteCount;

            // 1. Receive from socket
            try
            {
                receivedByteCount = Socket.Receive(receiveBuffer, receiveBufferPosition, receiveBuffer.Length - receiveBufferPosition, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Disconnect();
                    break;
                }
                else if (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    break;
                }

                throw;
            }

            if (receivedByteCount <= 0)
            {
                Disconnect();
                break;
            }

            receiveBufferPosition += receivedByteCount;
            int totalReceivedByteCount = receiveBufferPosition;

            int currentRecieveBufferPosition = 0;

            // 2. decapsulate received data
            while (true)
            {
                if (IsConnected is false)
                {
                    break;
                }

                ReadOnlySpan<byte> remainingData = receiveBuffer.AsSpan(currentRecieveBufferPosition, totalReceivedByteCount - currentRecieveBufferPosition);

                bool isDecapsulated = protocol.TryDecapsulatePayload(remainingData, out byte[]? decapsulatedPayload, out int processedBytesCount);
                if (isDecapsulated is false)
                {
                    break;
                }

                currentRecieveBufferPosition += processedBytesCount;

                if (decapsulatedPayload is null)
                {
                    throw new InvalidOperationException($"{nameof(decapsulatedPayload)} must not be null when {nameof(INetworkProtocol.TryDecapsulatePayload)} return true.");
                }

                yield return decapsulatedPayload;
            }

            // 3. cleanup buffer
            if (currentRecieveBufferPosition == totalReceivedByteCount)
            {
                receiveBufferPosition = 0;
                break;
            }

            Array.Copy(receiveBuffer, currentRecieveBufferPosition, receiveBuffer, 0, totalReceivedByteCount - currentRecieveBufferPosition);
        }
    }

    internal void InternalSend()
    {
        try
        {
            Socket.Send(sendStream.GetBuffer(), (int)sendStream.Position, SocketFlags.None);
        }
        catch (SocketException ex)
        {
            if (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                Disconnect();
            }
        }

        sendStream.Position = 0;
    }
}