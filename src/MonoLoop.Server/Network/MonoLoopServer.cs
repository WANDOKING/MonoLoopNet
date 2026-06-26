namespace MonoLoop.Server.Network;

using Microsoft.Extensions.Logging;
using MonoLoop.Server.Misc;
using MonoLoop.Server.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// 싱글스레드를 생성하여 동작하는 서버입니다.
/// <see cref="MonoLoopServerBuilder"/>를 통해 생성하며,
/// <see cref="Run"/> 메서드를 호출하여 실행합니다.
/// </summary>
public sealed class MonoLoopServer : IDisposable
{
    private readonly int backlog;
    private readonly int? frameRateLimitFps;
    private readonly Socket listenSocket;
    private readonly ILogger logger;
    private readonly int maxSessionCount;
    private readonly Func<INetworkProtocol> protocolFactory;
    private readonly Thread serverThread;
    private readonly Dictionary<ulong, ServerSession> sessionStorage = new Dictionary<ulong, ServerSession>();

    private ulong sessionIndexer;

    static MonoLoopServer()
    {
        // NOTE: 안정적인 TickRate를 위해 시스템 타이머 해상도를 상향한다.
        TimerResolutionHelper.EnsureHighResolution();
    }

    /// <summary>
    /// 서버를 생성합니다.
    /// </summary>
    internal MonoLoopServer(
        ILogger logger,
        Func<INetworkProtocol> protocolFactory,
        IPEndPoint endPoint,
        string name,
        int backlog,
        int maxSessionCount,
        int? frameRateLimitFps)
    {
        this.logger = logger;
        this.protocolFactory = protocolFactory;
        this.Name = name;
        this.backlog = backlog;
        this.maxSessionCount = maxSessionCount;
        this.frameRateLimitFps = frameRateLimitFps;

        if (string.IsNullOrWhiteSpace(name))
        {
            this.Name = $"{nameof(MonoLoopServer)}-{endPoint.Port}";
        }
        else
        {
            this.Name = name;
        }

        serverThread = new Thread(ServerThreadLoop)
        {
            Name = name,
        };

        listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            Blocking = false,
        };

        listenSocket.Bind(endPoint);

        LocalEndPoint = listenSocket.LocalEndPoint as IPEndPoint
            ?? throw new InvalidCastException($"{nameof(listenSocket.LocalEndPoint)} of {nameof(listenSocket)} is not {nameof(IPEndPoint)}");

        logger.LogTrace($"{this.Name} bind at {listenSocket.LocalEndPoint}.");
    }

    internal event Action<ServerSession>? OnConnected;

    internal event Action<ServerSession>? OnDisconnected;

    internal event Action<ServerSession, byte[]>? OnMessageReceived;

    internal event Action<TimeSpan>? OnTick;

    /// <summary>
    /// 현재 실제 초당 Tick 횟수입니다.
    /// </summary>
    public double CurrentFps { get; private set; }

    /// <summary>
    /// 서버가 실행중인지 여부입니다.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 서버가 리슨하고 있는 엔드포인트입니다.
    /// 서버가 종료되어도 변하지 않습니다.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// 세션 개수입니다.
    /// </summary>
    public int SessionCount => sessionStorage.Count;

    /// <summary>
    /// 서버의 이름입니다.
    /// 디버깅, 모니터링 등의 용도로 사용됩니다.
    /// </summary>
    private string Name { get; }

    /// <summary>
    /// 서버를 종료하고, 리소스를 해제합니다.
    /// </summary>
    public void Dispose()
    {
        listenSocket.Dispose();
    }

    /// <summary>
    /// 서버를 실행합니다.
    /// 서버가 종료될 때까지 스레드를 차단합니다.
    /// </summary>
    /// <exception cref="InvalidOperationException">이미 실행중인 경우 발생합니다.</exception>
    public void Run()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException($"{Name} is already running.");
        }

        listenSocket.Listen(backlog);

        logger.LogTrace($"{this.Name} listen start.");

        IsRunning = true;
        serverThread.Start();

        serverThread.Join();
    }

    /// <summary>
    /// 서버를 종료합니다.
    /// </summary>
    public void Shutdown()
    {
        if (IsRunning is false)
        {
            return;
        }

        IsRunning = false;
        serverThread.Join();

        listenSocket.Close();
    }

    /// <summary>
    /// 목표 타임스탬프(QPC raw)까지 대기합니다.
    /// 대부분의 시간은 <see cref="Thread.Sleep(TimeSpan)"/>로 대기하되,
    /// 목표 직전 구간은 스핀 대기로 정밀하게 맞춥니다.
    /// Sleep의 잔여 지터를 흡수하여 안정적인 TickRate를 달성하기 위함입니다.
    /// </summary>
    private static void WaitUntil(long targetTimestamp)
    {
        // Sleep 정밀도가 1ms 수준이어도 ±1ms 오차가 남으므로, 목표 직전은 스핀으로 마무리한다.
        // 2ms 분량의 타임스탬프 틱 (Stopwatch.Frequency 단위)
        long spinMarginTicks = Stopwatch.Frequency / 500;

        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            long remainingTicks = targetTimestamp - now;

            if (remainingTicks <= 0)
            {
                break;
            }

            if (remainingTicks > spinMarginTicks)
            {
                // 목표 직전(spinMargin)을 남기고 Sleep으로 대기한다.
                Thread.Sleep(Stopwatch.GetElapsedTime(now, targetTimestamp - spinMarginTicks));
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    /// <summary>
    /// 서버의 스레드 루프입니다.
    /// </summary>
    private void ServerThreadLoop()
    {
        logger.LogTrace($"{Name} Thread started. {nameof(Environment.CurrentManagedThreadId)}={Environment.CurrentManagedThreadId}");

        var readSet = new List<Socket>();
        var writeSet = new List<Socket>();
        var sessionsToRemove = new List<ulong>();

        // NOTE: 한 프레임의 길이를 QPC 타임스탬프 틱(Stopwatch.Frequency 단위)으로 환산한다.
        // 이 틱은 TimeSpan.Ticks(100ns)가 아니라 Stopwatch.Frequency 단위이다.
        long targetFrameTicks = frameRateLimitFps.HasValue ? Stopwatch.Frequency / frameRateLimitFps.Value : 0;

        // 이전 프레임의 시작 타임스탬프
        long lastFrameStartTimestamp = Stopwatch.GetTimestamp();

        // 다음 프레임을 시작해야 하는 타임스탬프
        long nextFrameTargetTimestamp = lastFrameStartTimestamp;

        while (IsRunning)
        {
            // NOTE: 프레임 시작 시점에 DeltaTime을 확정한다.
            // 이 프레임의 모든 OnTick 핸들러는 동일한 deltaTime을 받아야만 한다.
            long frameStartTimestamp = Stopwatch.GetTimestamp();

            // NOTE: 첫 프레임의 deltaTime이 TimeSpan.Zero가 아니지만, 무시 가능하다고 판단하여 둔다.
            TimeSpan deltaTime = Stopwatch.GetElapsedTime(lastFrameStartTimestamp, frameStartTimestamp);
            lastFrameStartTimestamp = frameStartTimestamp;

            // 여기서 한 프레임을 처리한다.
            FrameMove(deltaTime);

            // CurrentFPS를 계산한다.
            if (deltaTime > TimeSpan.Zero)
            {
                CurrentFps = 1.0 / deltaTime.TotalSeconds;
            }

            // 프레임 간격을 맞춘다.
            if (frameRateLimitFps.HasValue)
            {
                // NOTE: 상대 시간이 아닌 절대 시각을 누적하여 프레임 간격을 맞춘다.
                // 특정 프레임이 늦어도 다음 프레임 목표가 고정 간격에 맞춰져 평균 FPS가 목표로 수렴한다.
                nextFrameTargetTimestamp += targetFrameTicks;

                long now = Stopwatch.GetTimestamp();

                bool shouldWaitUntilNextFrame = now < nextFrameTargetTimestamp;
                if (shouldWaitUntilNextFrame)
                {
                    WaitUntil(nextFrameTargetTimestamp);
                }
                else
                {
                    // 한 프레임 예산을 초과한 과부하 상태.
                    // 밀린 시간을 따라잡으려 폭주(spiral of death)하지 않도록
                    // 스케줄을 현재 시각으로 재정렬하여 누적 지연을 버린다.
                    nextFrameTargetTimestamp = now;
                }
            }
        }

        // disconnect all sessions
        foreach (ServerSession session in sessionStorage.Values)
        {
            session.InternalDisconnect();
        }

        sessionsToRemove.Clear();

        void FrameMove(TimeSpan deltaTime)
        {
            // 1. Accept
            // NOTE: listenSocket은 그냥 매 루프마다 Accept하도록 하였음
            // 일단은 굳이 Select할 필요가 없다고 생각했음
            while (true)
            {
                Socket acceptedSocket;

                try
                {
                    acceptedSocket = listenSocket.Accept();
                    acceptedSocket.Blocking = false;
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        break;
                    }

                    throw;
                }

                if (SessionCount == maxSessionCount)
                {
                    acceptedSocket.Close();
                    acceptedSocket.Dispose();
                    this.logger.LogWarning($"{Name} deny acceptedSocket because session count reached maxSessionCount. RemoteEndPoint={acceptedSocket.RemoteEndPoint}");
                    break;
                }

                sessionIndexer++;
                ServerSession session = new ServerSession(sessionIndexer, acceptedSocket, protocolFactory.Invoke());

                sessionStorage.Add(sessionIndexer, session);

                this.logger.LogDebug($"{Name} accepted new session({session.Id}) OnConnected will be invoked. RemoteEndPoint={session.RemoteEndPoint}");
                OnConnected?.Invoke(session);
            }

            // 2. prepare to Select()
            readSet.Clear();
            writeSet.Clear();

            foreach (ServerSession session in sessionStorage.Values)
            {
                readSet.Add(session.Socket);

                if (session.HasToSend)
                {
                    writeSet.Add(session.Socket);
                }
            }

            // 3. Select()
            bool shouldSelect = readSet.Count > 0 || writeSet.Count > 0;
            if (shouldSelect)
            {
                // 항상 non-blocking으로 검사한다.
                // IO가 그렇게까지 빠를 필요 없고, FrameRate가 더 중요하다.
                Socket.Select(readSet, writeSet, null, 0);

                // 4. Process WriteSet
                foreach (Socket socket in writeSet)
                {
                    foreach (ServerSession session in sessionStorage.Values)
                    {
                        if (socket.Handle == session.Socket.Handle)
                        {
                            session.InternalSend();
                            break;
                        }
                    }
                }

                // 5. Process ReadSet
                foreach (Socket socket in readSet)
                {
                    // Receive
                    foreach (ServerSession session in sessionStorage.Values)
                    {
                        // NOTE: OnMessageReceived로 Disconnect()를 호출한 세션의 이벤트가 전달되지 않게 해준다.
                        if (session.IsConnected is false)
                        {
                            continue;
                        }

                        if (socket.Handle == session.Socket.Handle)
                        {
                            foreach (byte[] receivedData in session.InternalReceive())
                            {
                                this.logger.LogTrace($"{Name} session({session.Id}) received data. OnMessageReceived will be invoked. {nameof(receivedData.Length)}={receivedData.Length}.");
                                OnMessageReceived?.Invoke(session, receivedData);
                            }

                            break;
                        }
                    }
                }
            }

            // 6. Process Session Disconnect
            sessionsToRemove.Clear();
            foreach (ServerSession session in sessionStorage.Values)
            {
                if (session.IsConnected)
                {
                    continue;
                }

                session.InternalDisconnect();
                sessionsToRemove.Add(session.Id);

                this.logger.LogDebug($"{Name} session({session.Id}) disconnected. OnDisconnected will be invoked.");
                OnDisconnected?.Invoke(session);
            }

            foreach (ulong id in sessionsToRemove)
            {
                sessionStorage.Remove(id);
            }

            // 7. Tick
            OnTick?.Invoke(deltaTime);
        }
    }
}
