using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SessionExec
{
    public class Program
    {
        enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,              // User logged on to WinStation
            WTSConnected,           // WinStation connected to client
            WTSConnectQuery,        // In the process of connecting to client
            WTSShadow,              // Shadowing another WinStation
            WTSDisconnected,        // WinStation logged on without client
            WTSIdle,                // Waiting for client to connect
            WTSListen,              // WinStation is listening for connection
            WTSReset,               // WinStation is being reset
            WTSDown,                // WinStation is down due to error
            WTSInit,                // WinStation in initialization
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSEnumerateSessions(
                IntPtr hServer,
                int Reserved,
                int Version,
                out IntPtr ppSessionInfo,
                out int pCount);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            ref SECURITY_ATTRIBUTES lpProcessAttributes,
            ref SECURITY_ATTRIBUTES lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(int sessionId, out IntPtr Token);

        public static IEnumerable<int> GetSessionIds()
        {
            List<int> sids = new List<int>();
            IntPtr pSessions = IntPtr.Zero;
            int dwSessionCount = 0;
            try
            {
                if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pSessions, out dwSessionCount))
                {
                    IntPtr current = pSessions;
                    for (int i = 0; i < dwSessionCount; ++i)
                    {
                        WTS_SESSION_INFO session_info = (WTS_SESSION_INFO)Marshal.PtrToStructure(current, typeof(WTS_SESSION_INFO));

                        if (session_info.State == WTS_CONNECTSTATE_CLASS.WTSActive ||
                            session_info.State == WTS_CONNECTSTATE_CLASS.WTSConnected ||
                            session_info.State == WTS_CONNECTSTATE_CLASS.WTSConnectQuery ||
                            session_info.State == WTS_CONNECTSTATE_CLASS.WTSShadow ||
                            session_info.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected ||
                            session_info.State == WTS_CONNECTSTATE_CLASS.WTSIdle)
                        {
                            if (session_info.SessionId != 0)
                            {
                                sids.Add(session_info.SessionId);
                            }
                        }
                        current += Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    }
                }
            }
            finally
            {
                if (pSessions != IntPtr.Zero)
                {
                    WTSFreeMemory(pSessions);
                }
            }

            return sids;
        }

        public static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: SessionExec.exe <SessionID|All> <Command> [/NoOutput]");
                return;
            }

            string sessionArg = args[0];
            string command = args[1];
            bool NoOutput = args.Length > 2 && args[2].Equals("/NoOutput", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (sessionArg.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    var sessionIds = GetSessionIds().Where(id => id != Process.GetCurrentProcess().SessionId);
                    foreach (var sessionId in sessionIds)
                    {
                        ExecuteCommandInSession(sessionId, command, NoOutput);
                    }
                }
                else if (int.TryParse(sessionArg, out int new_session_id))
                {
                    if (new_session_id == Process.GetCurrentProcess().SessionId)
                    {
                        Console.WriteLine("Cannot use the current session ID.");
                        return;
                    }
                    ExecuteCommandInSession(new_session_id, command, NoOutput);
                }
                else
                {
                    Console.WriteLine("Invalid Session ID");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static void ExecuteCommandInSession(int sessionId, string command, bool NoOutput)
        {
            if (NoOutput)
            {
                Console.WriteLine("Creating Process in Session {0}", sessionId);
            }

            if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            STARTUPINFO si = new STARTUPINFO();
            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            si.cb = Marshal.SizeOf(si);
            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);

            string powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string arguments = $"-Command \"{command}\"";

            uint creationFlags = NoOutput ? (uint)0x08000000 /* CREATE_NO_WINDOW */ : 0;

            if (!CreateProcessAsUser(userToken, powershellPath, arguments, ref sa, ref sa, false, creationFlags, IntPtr.Zero, null, ref si, out pi))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!NoOutput)
            {
                // Wait for the process to complete and capture the output
                using (Process process = Process.GetProcessById(pi.dwProcessId))
                {
                    process.WaitForExit();
                }
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            CloseHandle(userToken);
        }
    }
}
