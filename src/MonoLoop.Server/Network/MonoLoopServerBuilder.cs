namespace MonoLoop.Server.Network;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoLoop.Server.Protocol;
using System.Net;

/// <summary>
/// <see cref="MonoLoopServer"/>를 생성하는 빌더입니다.
/// </summary>
public sealed class MonoLoopServerBuilder
{
    private readonly List<Action<ServerSession>> onConnectedHandlers = new List<Action<ServerSession>>();
    private readonly List<Action<ServerSession>> onDisconnectedHandlers = new List<Action<ServerSession>>();
    private readonly List<Action<ServerSession, byte[]>> onMessageReceivedHandlers = new List<Action<ServerSession, byte[]>>();
    private readonly List<Action<TimeSpan>> onTickHandlers = new List<Action<TimeSpan>>();

    private int backlog;
    private int? frameRateLimitFps = 60;

    private IPAddress ipAddress = IPAddress.Any;
    private bool isBuilt;
    private Func<ILogger> loggerFactory = () => NullLogger.Instance;

    private int maxSessionCount = int.MaxValue;
    private int port;
    private Func<INetworkProtocol> protocolFactory = () => NullNetworkProtocol.Instance;
    private string serverName = string.Empty;

    /// <summary>
    /// 세션이 접속했을 때 호출되는 핸들러를 등록합니다.
    /// </summary>
    /// <param name="onConnectedHandler">접속 핸들러입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder AddOnConnectedHandler(Action<ServerSession> onConnectedHandler)
    {
        ArgumentNullException.ThrowIfNull(onConnectedHandler);

        onConnectedHandlers.Add(onConnectedHandler);
        return this;
    }

    /// <summary>
    /// 세션의 연결이 종료되었을 때 호출되는 핸들러를 등록합니다.
    /// </summary>
    /// <param name="onDisconnectedHandler">연결 종료 핸들러입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder AddOnDisconnectedHandler(Action<ServerSession> onDisconnectedHandler)
    {
        ArgumentNullException.ThrowIfNull(onDisconnectedHandler);

        onDisconnectedHandlers.Add(onDisconnectedHandler);
        return this;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="onDisconnectedHandler"></param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder AddOnMessageReceivedHandler(Action<ServerSession, byte[]> onDisconnectedHandler)
    {
        ArgumentNullException.ThrowIfNull(onDisconnectedHandler);

        onMessageReceivedHandlers.Add(onDisconnectedHandler);
        return this;
    }

    /// <summary>
    /// 매 프레임마다 호출되는 Tick에 대한 핸들러를 등록합니다.
    /// </summary>
    /// <param name="tickHandler">Tick 핸들러입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder AddOnTickHandler(Action<TimeSpan> tickHandler)
    {
        ArgumentNullException.ThrowIfNull(tickHandler);

        onTickHandlers.Add(tickHandler);
        return this;
    }

    /// <summary>
    /// <see cref="MonoLoopServer"/>를 생성합니다.
    /// </summary>
    /// <returns>생성된 <see cref="MonoLoopServer"/> 객체입니다.</returns>
    public MonoLoopServer Build()
    {
        if (isBuilt)
        {
            throw new InvalidOperationException("The MonoLoopServerBuilder can only build one MonoLoopServer instance.");
        }

        isBuilt = true;

        var server = new MonoLoopServer(
            logger: loggerFactory.Invoke(),
            protocolFactory: protocolFactory,
            endPoint: new IPEndPoint(ipAddress, port),
            name: serverName,
            backlog: backlog,
            maxSessionCount: maxSessionCount,
            frameRateLimitFps: frameRateLimitFps);

        foreach (var handler in onTickHandlers)
        {
            server.OnTick += handler;
        }

        foreach (var handler in onConnectedHandlers)
        {
            server.OnConnected += handler;
        }

        foreach (var handler in onDisconnectedHandlers)
        {
            server.OnDisconnected += handler;
        }

        foreach (var handler in onMessageReceivedHandlers)
        {
            server.OnMessageReceived += handler;
        }

        return server;
    }

    /// <summary>
    /// <see cref="ILogger"/>를 구현하는 로거를 설정합니다.
    /// </summary>
    /// <param name="loggerFactory">로거를 생성하는 팩토리 메서드입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder ConfigureLogger(Func<ILogger> loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// 네트워크 프로토콜을 구성합니다.
    /// 네트워크 프로토콜은 서버 세션이 클라이언트와 주고받는 메시지를 캡슐화/디캡슐화하는 방식입니다.
    /// </summary>
    /// <remarks>
    /// 프로토콜 구현에 오류가 있을 경우 서버가 오작동하기 때문에 구현에 매우 주의해야 합니다.
    /// </remarks>
    /// <param name="protocolFactory"><see cref="INetworkProtocol"/>을 구현하는 프로토콜을 생성하는 팩토리입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder ConfigureProtocol(Func<INetworkProtocol> protocolFactory)
    {
        ArgumentNullException.ThrowIfNull(protocolFactory);

        this.protocolFactory = protocolFactory;
        return this;
    }

    /// <summary>
    /// backlog 사이즈를 설정합니다.
    /// </summary>
    /// <param name="backlog">backlog 값입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithBacklog(int backlog)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(backlog);

        this.backlog = backlog;
        return this;
    }

    /// <summary>
    /// 프레임 레이트 제한을 설정합니다.
    /// 기본값은 60입니다.
    /// </summary>
    /// <param name="fps">초당 최대 Tick 횟수입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithFrameRateLimit(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);

        this.frameRateLimitFps = fps;
        return this;
    }

    /// <summary>
    /// 접속을 받아들이는 IP를 설정합니다.
    /// </summary>
    /// <param name="ip">리슨 소켓이 bind할 IP 주소입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        this.ipAddress = ip;
        return this;
    }

    /// <summary>
    /// 최대 커넥션 수 제한을 설정합니다.
    /// </summary>
    /// <param name="maxConnections">최대 커넥션 수 입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithMaxConnections(int maxConnections)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 0);

        this.maxSessionCount = maxConnections;
        return this;
    }

    /// <summary>
    /// 서버의 이름을 설정합니다.
    /// </summary>
    /// <param name="name">서버의 이름입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        this.serverName = name;
        return this;
    }

    /// <summary>
    /// 프레임 레이트 제한을 해제합니다.
    /// </summary>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithoutFrameRateLimit()
    {
        this.frameRateLimitFps = null;
        return this;
    }

    /// <summary>
    /// 접속을 받아들이는 포트를 설정합니다.
    /// </summary>
    /// <param name="port">리슨 소켓이 bind할 포트 번호입니다.</param>
    /// <returns>this를 반환합니다.</returns>
    public MonoLoopServerBuilder WithPort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, ushort.MinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        this.port = port;
        return this;
    }
}
