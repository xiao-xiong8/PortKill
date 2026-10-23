using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PortKiller.Native;

/// <summary>
/// 令牌特权提升：启用 SeDebugPrivilege。
///
/// 为什么必须做这一步 —— 即使以管理员身份运行，Windows 管理员令牌里的
/// SeDebugPrivilege 默认也是「存在但禁用」状态。未启用时 OpenProcess 对
/// 服务托管进程（nssm / winsw 包装的 nginx 等）、SYSTEM 账户进程、
/// 高完整性级别进程一律返回 ACCESS_DENIED (5)，这正是「nginx 终止不了」
/// 的第一大根因。启用后管理员可打开除 PPL（受保护进程光）外的几乎所有进程。
///
/// 实现走 unsafe 指针直写 TOKEN_PRIVILEGES（栈上零分配），Native AOT 友好，
/// 不依赖任何反射或托管安全 API。
/// </summary>
public static unsafe class PrivilegeHelper
{
    private const string SE_DEBUG_NAME = "SeDebugPrivilege";

    // 0 = 未尝试 · 1 = 已成功 · -1 = 已失败（不再重复尝试）
    private static int _seDebugState;

    /// <summary>SeDebugPrivilege 是否已在本进程令牌中启用。</summary>
    public static bool IsSeDebugEnabled => Volatile.Read(ref _seDebugState) == 1;

    /// <summary>
    /// 在当前进程令牌中启用 SeDebugPrivilege。幂等：成功后不再重复调用，
    /// 失败（非管理员 / 令牌中无此特权）也只尝试一次。
    /// </summary>
    public static bool EnableSeDebugPrivilege()
    {
        int state = Volatile.Read(ref _seDebugState);
        if (state != 0) return state == 1;

        bool ok = TryEnablePrivilege(SE_DEBUG_NAME);
        Interlocked.Exchange(ref _seDebugState, ok ? 1 : -1);
        return ok;
    }

    private static bool TryEnablePrivilege(string privilegeName)
    {
        if (!OperatingSystem.IsWindows()) return false;

        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out hToken))
            {
                return false;
            }

            if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, out LUID luid))
            {
                return false;
            }

            // 栈上直写单特权 TOKEN_PRIVILEGES，避免变长结构的托管封送
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
            };

            if (!NativeMethods.AdjustTokenPresent(hToken, false, &tp, (uint)sizeof(TOKEN_PRIVILEGES), null, null))
            {
                return false;
            }

            // AdjustTokenPresent 成功但特权「未被分配」时也返回 TRUE 且
            // GetLastError = ERROR_NOT_ALL_ASSIGNED (1300)，必须复核
            return Marshal.GetLastWin32Error() != 1300;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hToken != IntPtr.Zero) NativeMethods.CloseHandle(hToken);
        }
    }
}
