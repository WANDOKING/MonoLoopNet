# MonoLoop.Server

**단일 스레드에서 네트워크 IO와 게임 로직 Tick을 함께 돌리는, 쉽고 단순한 .NET 서버 라이브러리입니다.**

Unreal Engine의 프레임 / `DeltaTime` 모델을 참고하여, 매 프레임마다 네트워크를 처리하고
`OnTick(deltaTime)`을 호출하는 구조로 동작합니다. 멀티스레드 동기화(락, 동시성 컬렉션 등)에
대한 고민 없이, 게임 루프처럼 직관적으로 서버 로직을 작성할 수 있습니다.

```
MonoLoopServer 한 프레임:
  1. Accept           (새 접속 수락, non-blocking)
  2. Select(0)        (소켓 상태 즉시 확인, non-blocking)
  3. Process Write    (송신)
  4. Process Read     (수신 → OnMessageReceived)
  5. Process Disconnect
  6. OnTick(deltaTime)
  7. Frame pacing     (다음 프레임 시각까지 대기)
```

---

## 특징

- **단일 스레드 모델** — 모든 콜백(`OnConnected`, `OnMessageReceived`, `OnTick` 등)이 동일한
  서버 스레드에서 순차적으로 호출됩니다. 공유 상태에 대한 락이 필요 없습니다.
- **고정 프레임 레이트(TickRate)** — 기본 60fps. 절대 시각 누적 페이싱과 `Sleep` + `SpinWait`
  하이브리드 대기로, Windows에서도 안정적인 프레임 간격을 보장합니다.
- **빌더 기반 구성** — `MonoLoopServerBuilder`로 포트, IP, 핸들러, 프로토콜 등을 체이닝으로 설정합니다.
- **프로토콜 추상화** — `INetworkProtocol`을 구현하여 메시지 캡슐화/디캡슐화(길이 프리픽스, 구분자 등)를
  자유롭게 정의할 수 있습니다. 페이로드는 JSON, protobuf, 순수 바이트 배열 등 무엇이든 가능합니다.
- **로깅 통합** — `Microsoft.Extensions.Logging.ILogger`를 그대로 사용합니다.
- **관측 가능성** — `CurrentFps`, `SessionCount` 등 런타임 상태를 public 프로퍼티로 노출합니다.

---

## 설치

```bash
dotnet add package MonoLoop.Server
```

> .NET 10 (`net10.0`) 이상이 필요합니다.

---

## 빠른 시작

```csharp
using MonoLoop.Server.Network;

var builder = new MonoLoopServerBuilder()
    .WithPort(9642);

builder.AddOnConnectedHandler(session =>
{
    Console.WriteLine($"세션 접속: Id={session.Id}");
});

builder.AddOnDisconnectedHandler(session =>
{
    Console.WriteLine($"세션 종료: Id={session.Id}");
});

builder.AddOnMessageReceivedHandler((session, message) =>
{
    Console.WriteLine($"메시지 수신: SessionId={session.Id}, Bytes={message.Length}");

    // 받은 메시지를 그대로 돌려보내는 에코 예제
    session.Send(message);
});

builder.AddOnTickHandler(deltaTime =>
{
    // 매 프레임 호출됩니다. 게임 로직을 여기서 처리하세요.
    // deltaTime: 이전 프레임 시작부터 이번 프레임 시작까지의 경과 시간
});

MonoLoopServer server = builder.Build();

// 서버가 종료될 때까지 현재 스레드를 차단합니다.
server.Run();
```

---

## 사용법

### 1. 서버 구성 (`MonoLoopServerBuilder`)

빌더의 메서드는 모두 자기 자신을 반환하므로 체이닝이 가능합니다. `Build()`는 빌더당 한 번만 호출할 수 있습니다.

| 메서드 | 설명 | 기본값 |
|---|---|---|
| `WithPort(int)` | 리슨 포트 | `0` |
| `WithIp(IPAddress)` | bind할 IP 주소 | `IPAddress.Any` |
| `WithName(string)` | 서버 이름 (디버깅/모니터링용) | `MonoLoopServer-{port}` |
| `WithBacklog(int)` | listen backlog 크기 | `0` |
| `WithMaxConnections(int)` | 최대 동시 세션 수 | `int.MaxValue` |
| `WithFrameRateLimit(int fps)` | 초당 최대 Tick 횟수 | `60` |
| `WithoutFrameRateLimit()` | 프레임 레이트 제한 해제 (busy loop) | — |
| `ConfigureLogger(Func<ILogger>)` | 로거 설정 | `NullLogger` |
| `ConfigureProtocol(Func<INetworkProtocol>)` | 네트워크 프로토콜 설정 | `NullNetworkProtocol` |
| `AddOnConnectedHandler(...)` | 세션 접속 핸들러 등록 | — |
| `AddOnDisconnectedHandler(...)` | 세션 종료 핸들러 등록 | — |
| `AddOnMessageReceivedHandler(...)` | 메시지 수신 핸들러 등록 | — |
| `AddOnTickHandler(Action<TimeSpan>)` | 매 프레임 Tick 핸들러 등록 | — |

각 핸들러는 여러 번 등록할 수 있으며, 등록한 순서대로 호출됩니다.

### 2. 세션 다루기 (`ServerSession`)

핸들러로 전달되는 `ServerSession` 객체로 개별 클라이언트와 통신합니다.

```csharp
session.Id;             // 세션 식별자 (ulong)
session.IsConnected;    // 연결 여부
session.RemoteEndPoint; // 원격 엔드포인트
session.Send(byte[]);   // 데이터 송신 (전송 큐에 적재, 즉시 전송 보장 X)
session.Disconnect();   // 세션 연결 종료
```

> `Send`는 데이터를 송신 큐에 넣고 다음 프레임에서 실제로 전송합니다. 반환값은 큐잉 성공 여부이며,
> 네트워크로 실제 전달되었음을 보장하지는 않습니다.

### 3. 프로토콜 정의 (`INetworkProtocol`)

TCP는 스트림이므로 메시지 경계가 보존되지 않습니다. `INetworkProtocol`을 구현하여 메시지를
어떻게 구분할지(예: 길이 프리픽스) 정의하세요.

```csharp
using MonoLoop.Server.Protocol;

public sealed class LengthPrefixedProtocol : INetworkProtocol
{
    // 송신: [4바이트 길이][페이로드] 형태로 감쌉니다.
    public byte[] EncapsulatePayload(byte[] payload)
    {
        byte[] result = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(result, payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    // 수신: 완전한 메시지 한 개를 분리해 냅니다.
    public bool TryDecapsulatePayload(
        ReadOnlySpan<byte> receivedData,
        out byte[]? decapsulatedPayload,
        out int processedBytesCount)
    {
        decapsulatedPayload = null;
        processedBytesCount = 0;

        if (receivedData.Length < 4)
        {
            return false; // 길이 헤더가 아직 다 도착하지 않음
        }

        int payloadLength = BitConverter.ToInt32(receivedData);
        if (receivedData.Length < 4 + payloadLength)
        {
            return false; // 페이로드가 아직 다 도착하지 않음
        }

        decapsulatedPayload = receivedData.Slice(4, payloadLength).ToArray();
        processedBytesCount = 4 + payloadLength;
        return true;
    }
}
```

```csharp
builder.ConfigureProtocol(() => new LengthPrefixedProtocol());
```

> 프로토콜을 설정하지 않으면 수신한 바이트를 그대로 페이로드로 전달하는 기본 프로토콜이 사용됩니다.
> 프로토콜 구현에 오류가 있으면 서버가 오작동할 수 있으니 주의해서 구현하세요.

### 4. 서버 실행 / 종료

```csharp
server.Run();       // 서버 스레드를 시작하고, 종료될 때까지 호출 스레드를 차단합니다.
server.Shutdown();  // 다른 스레드에서 호출하여 서버를 안전하게 종료합니다.
```

---

## 동작 원리 및 설계

프레임 페이싱, Windows 타이머 해상도(`timeBeginPeriod`), `DeltaTime` 모델 등 내부 구현에 대한
상세한 설계 결정과 근거는 [docs/design-decisions.md](docs/design-decisions.md)를 참고하세요.

---

## 라이선스

[MIT](LICENSE)
