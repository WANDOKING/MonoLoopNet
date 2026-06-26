# MonoLoop.Server

단일 스레드에서 네트워크 IO 처리와 게임 로직 Tick을 함께 돌리는 서버입니다.

Unreal Engine의 프레임/`DeltaTime` 모델을 참고하여, 매 프레임 네트워크를 처리하고
`OnTick(deltaTime)`을 호출하는 구조입니다.

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

## 설계 결정 (Design Decisions)

이 문서는 Tick/프레임 구현 과정에서 내린 결정과 그 근거를 정리한 것입니다.
대부분은 **"Windows에서 안정적인 TickRate를 어떻게 보장할 것인가"** 라는 한 가지 문제로 수렴합니다.

### 1. Windows에서 `timeBeginPeriod(1)`이 필요한 이유

**결론:** 서버 프로세스 시작 시 `timeBeginPeriod(1)`로 시스템 타이머 해상도를 1ms로 올린다.
([`TimerResolutionHelper`](Misc/TimerResolutionHelper.cs))

**문제:** Windows의 **기본 시스템 타이머 해상도는 ~15.6ms**다. 이 상태에서는 `Thread.Sleep`이
요청 시간을 다음 15.6ms 경계로 올림한다. 측정 결과:

| | `Sleep(1)` | `Sleep(16)` |
|---|---|---|
| 기본 (해상도 ~15.6ms) | **15.69ms** | **30.9ms** |
| `timeBeginPeriod(1)` 적용 후 | 1.94ms | 16.6ms |

60fps의 프레임 예산은 16.67ms인데, 기본 상태에서는 `Sleep(16)`이 31ms가 되어 **프레임이
절반 속도(~30fps)로 떨어진다.** 이것을 1ms로 올려야 의도한 프레임 간격을 맞출 수 있다.

**자주 하는 오해 — ".NET이 기본으로 1ms로 올려주지 않나?":** 아니다.
`NtQueryTimerResolution`이 1ms로 보이더라도 그것은 **시스템 전역 값**으로, 보통 다른 프로세스
(브라우저 등)가 올려둔 것이다. Windows 10 2004부터 타이머 해상도는 **per-process**로 적용되므로,
**우리 프로세스가 직접 `timeBeginPeriod`를 호출하지 않으면 우리 Sleep은 여전히 ~15.6ms로 동작한다.**
런타임의 우연한 상태에 기대지 않기 위해 직접 호출한다.

**QPC와는 별개의 축이다:** `Stopwatch`(QPC)는 시간을 **재는** 정밀도이고, `timeBeginPeriod`는
`Sleep`이 **깨우는** 정밀도다. 둘은 독립적이다. QPC로 목표 시각을 아무리 정밀하게 계산해도
그 시각까지 `Sleep`으로 기다리는 한 `timeBeginPeriod`는 여전히 필요하다.
(이것을 없애려면 Sleep 없이 순수 busy-spin을 해야 하는데, 코어를 100% 점유하므로 채택하지 않았다.)

**구현 메모:**
- `static` 생성자에서 **프로세스당 1회만** 호출한다. `MonoLoopServer`가 여러 개여도 1회만 적용된다.
- `timeEndPeriod`(복원)는 호출하지 않는다. 프로세스 종료 시 OS가 자동 회수하며, 서버는
  실행 내내 1ms를 유지하길 원하기 때문이다.
- Windows 외 플랫폼(리눅스 등)은 고해상도 타이머가 기본이라(`Sleep(16)`≈16.1ms) **no-op**이다.
  이 타이머 문제는 사실상 Windows 한정 이슈다.

### 2. `Select(timeout = 0)` — 항상 non-blocking

**결론:** `Socket.Select`의 timeout을 항상 `0`(즉시 반환)으로 둔다.

**근거:** 프레임 페이싱(대기)의 책임을 **한 곳(루프 말미)으로 일원화**하기 위해서다.
만약 Select가 IO 이벤트를 기다리며 블로킹하면, "언제 다음 프레임이 시작되는가"가
**소켓 트래픽에 좌우되어** 프레임 간격이 들쭉날쭉해진다. Select(0)은 "지금 처리할 IO가 있는가"만
즉시 확인하고 넘어가며, 프레임 간격은 오직 페이싱 로직이 결정한다.

이 서버는 Tick 기반이라 입력을 어차피 프레임당 1회 처리한다. 따라서 IO를 일찍 깨워 처리할 이득이
없고, 수신 지연이 최대 1프레임(60fps → 16.7ms)인 것은 오히려 **일관성** 측면에서 바람직하다.
**"IO 지연보다 TickRate 안정성이 더 중요하다"** 는 판단이다.

### 3. 절대 시각 누적 페이싱 (Frame Pacing)

**결론:** "지금부터 16.7ms 뒤"(상대)가 아니라 **"다음 목표 타임스탬프"(절대)를 누적**하여 대기한다.

```csharp
nextFrameTargetTimestamp += targetFrameTicks;   // = now + interval 이 아님
WaitUntil(nextFrameTargetTimestamp);
```

**근거:** 상대 방식은 매 프레임의 측정·Sleep 오차가 **누적되며 drift**한다.
절대 방식은 목표 시각이 고정 격자(`T0, T0+Δ, T0+2Δ …`) 위에 놓여, 한 프레임이 늦게 깨어나도
다음 목표가 그대로이므로 **오차가 자동 보정되고 평균 FPS가 목표로 수렴**한다.

**과부하 처리:** 프레임 작업 자체가 예산을 초과해 목표 시각이 이미 과거가 되면, 밀린 시간을
따라잡으려 프레임이 연속 폭주(spiral of death)할 수 있다. 이때는 누적 지연을 **버리고**
스케줄을 현재 시각으로 재정렬한다("따라잡기 포기, 지금부터 다시 정상 간격").

### 4. `Sleep` + `SpinWait` 하이브리드 대기 ([`WaitUntil`](Server/MonoLoopServer.cs))

**결론:** 목표 직전 ~2ms를 남기고 그 전까지는 `Thread.Sleep`, 마지막 2ms는 `SpinWait`.

**근거:** `timeBeginPeriod(1)`을 적용해도 `Sleep`에는 ±1ms 잔여 지터가 남는다.
목표 직전 구간만 busy-spin으로 마무리하면 **CPU를 거의 태우지 않으면서** 지터를 흡수해
타임스탬프 단위의 정밀도를 얻는다.

### 5. 시간 계산은 QPC `long` 타임스탬프로

**결론:** 내부 페이싱은 `Stopwatch.GetTimestamp()`(raw `long`)로 다루고,
외부(`OnTick`)로 넘기는 `deltaTime`만 `Stopwatch.GetElapsedTime`으로 `TimeSpan` 변환한다.

**근거:**
- `TimeSpan`은 의미상 **기간(duration)**이지 **시각(instant)**이 아니다. 타임라인 위의 한 점을
  표현하기엔 `long` 타임스탬프가 더 적합하다.
- `stopwatch.Elapsed`는 호출마다 `TimeSpan`을 만든다. 스핀 루프에서 반복 호출되므로
  변환·할당이 없는 `GetTimestamp()`가 더 가볍고, QPC 원본 해상도도 유지된다.

**단위 주의 (함정):** `GetTimestamp()`의 틱은 **`Stopwatch.Frequency` 단위**이지
`TimeSpan.Ticks`(100ns)가 **아니다.** 프레임 간격도 같은 단위로 계산해야 한다:

```csharp
long targetFrameTicks = Stopwatch.Frequency / frameRateLimitFps.Value;  // 100ns 아님!
```

### 6. `DeltaTime` 모델 (Unreal Engine 참고)

- `deltaTime`은 **프레임 시작 시점에 1회 확정**한다. 한 프레임 안에서 호출되는 모든 `OnTick`
  핸들러는 **동일한 `deltaTime`** 을 받는다.
- 값은 "이전 프레임 시작 → 이번 프레임 시작" 사이의 실제 경과 시간이다.
- 첫 프레임의 `deltaTime`은 정확히 0은 아니지만(루프 진입 직전 샘플과의 미세 간격), 무시 가능한
  수준이라 별도 처리하지 않는다.

### 7. 관측 가능성 (`CurrentFps`)

`MonoLoopServer.CurrentFps` public 프로퍼티로 실제 측정 FPS를 노출한다. Tick이 느려지면
외부에서 즉시 관측할 수 있다. (`deltaTime`이 0이면 계산 불가이므로 갱신을 건너뛴다.)

---

## 측정 환경별 `Sleep` 정밀도 요약

| 환경 | `Sleep(1)` | `Sleep(16)` | 비고 |
|---|---|---|---|
| Windows 기본 | 15.69ms | 30.9ms | 타이머 ~15.6ms |
| Windows + `timeBeginPeriod(1)` | 1.94ms | 16.6ms | 본 구현 적용 상태 |
| Linux 기본 | 1.08ms | 16.10ms | 추가 설정 불필요 |

> 60fps 프레임 제한 적용 시 실측 `deltaTime`은 16.665~16.669ms, `CurrentFps` 60.00으로 안정.
