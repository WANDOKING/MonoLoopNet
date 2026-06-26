namespace MonoLoop.Server.Misc;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// Windows에서 시스템 타이머 해상도를 1ms로 상향합니다.
/// Windows 외 플랫폼은 고해상도 타이머가 기본이므로 아무 동작도 하지 않습니다.
/// </summary>
/// <remarks>
/// Windows의 기본 타이머 해상도는 ~15.6ms이며, 이 상태에서는 짧은 Sleep이 크게 올림됩니다.<br/>
/// (예: Sleep(1)이 ~15.6ms, Sleep(16)이 ~31ms로 동작)<br/>
/// timeBeginPeriod로 1ms까지 올리면 Sleep 정밀도가 ~1ms 수준으로 개선되어 안정적인 TickRate를 달성할 수 있습니다.<br/>
/// </remarks>
internal static class TimerResolutionHelper
{
    static TimerResolutionHelper()
    {
        // NOTE: timeEndPeriod는 따로 호출하지 않음
        // 프로세스 종료 시 OS가 자동으로 복원한다.

        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }
    }

    /// <summary>
    /// 타이머 해상도 상향이 적용되었음을 보장합니다.
    /// 이 메서드를 호출하면 정적 생성자가 1회 실행됩니다.
    /// </summary>
    public static void EnsureHighResolution()
    {
        // 정적 생성자 호출을 위함, 아무것도 하지 않음.
    }

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);
}
