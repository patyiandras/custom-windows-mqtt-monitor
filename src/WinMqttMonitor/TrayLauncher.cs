using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WinMqttMonitor
{
    /// <summary>
    /// A tálcás segéd indítása a felhasználó sessionjében, a szolgáltatásból (LocalSystem): ha be van jelentkezve
    /// valaki, de a segéd nem jelentkezik (nem indult el a Run-kulcsból, összeomlott, a telepítő leállította),
    /// a szolgáltatás a felhasználó saját (nem emelt) tokenjével elindítja. A segéd mutexe kizárja a duplikációt.
    /// </summary>
    static class TrayLauncher
    {
        public static readonly string TrayPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinMqttMonitorTray.exe");

        public static void Launch(int sessionId)
        {
            if (!File.Exists(TrayPath)) { throw new FileNotFoundException("Nincs meg a tálcás segéd", TrayPath); }
            IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!WTSQueryUserToken((uint)sessionId, out userToken)) { throw new Win32Exception(); }
                if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                {
                    throw new Win32Exception();
                }
                if (!CreateEnvironmentBlock(out env, primary, false)) { env = IntPtr.Zero; }
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = @"winsta0\default" };
                if (!CreateProcessAsUser(primary, TrayPath, $"\"{TrayPath}\"", IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT, env, Path.GetDirectoryName(TrayPath), ref si, out var pi))
                {
                    throw new Win32Exception();
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
            finally
            {
                if (env != IntPtr.Zero) { DestroyEnvironmentBlock(env); }
                if (primary != IntPtr.Zero) { CloseHandle(primary); }
                if (userToken != IntPtr.Zero) { CloseHandle(userToken); }
            }
        }

        const uint TOKEN_ALL_ACCESS = 0xF01FF, CREATE_UNICODE_ENVIRONMENT = 0x400;
        const int SecurityImpersonation = 2, TokenPrimary = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr token, string application, string commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
    }
}
