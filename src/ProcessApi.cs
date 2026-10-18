using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniTasker
{
    /// <summary>
    /// 一个进程的快照。全部来自 Win32 API，不碰 WMI —— WMI 服务本身就会卡，
    /// 而「任务管理器打不开」往往正是出在那时候。
    /// </summary>
    internal sealed class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name = "";
        public uint Threads;
        public int Priority;
        public ulong WorkingSet;      // 工作集（实际占用的物理内存）
        public ulong PrivateBytes;    // 私有内存
        public ulong CpuTicks;        // 累计 CPU 时间（100ns）
        public double CpuPercent;     // 两次采样之间的 CPU 占用
        public bool Hung;             // 主窗口无响
        public bool Elevated;         // 以管理员运行
        public bool Accessible;       // 能否打开句柄（判断是否为系统/受保护进程）
        public string WindowTitle = "";
        public string Path = "";
        public DateTime StartTime = DateTime.MinValue;

        public double MbDead { get { return WorkingSet / 1024.0 / 1024.0; } }
    }

    internal static class ProcessApi
    {
        // ---------------------------------------------------------------- 导入
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess,
            out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess,
            ref PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, int cb);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ---------------------------------------------------------------- 结构
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;
        private const int TokenIntegrityLevel = 25;

        // ---------------------------------------------------------------- 自身权限
        /// <summary>当前进程是否以管理员（高完整性）运行。非管理员时只能看到同级别的进程。</summary>
        public static bool IsElevated { get; private set; }

        /// <summary>当前进程的完整性级别数值（0x2000=High, 0x1000=Medium）。</summary>
        public static uint IntegrityLevel { get; private set; }

        public static void DetectElevation()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return;
            try
            {
                uint needed;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
                if (needed > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        uint ret;
                        if (GetTokenInformation(token, TokenIntegrityLevel, buf, needed, out ret))
                        {
                            IntPtr sid = Marshal.ReadIntPtr(buf);
                            IntPtr sub = GetSidSubAuthority(sid, 0);
                            IntegrityLevel = (uint)Marshal.ReadInt32(sub);
                            IsElevated = IntegrityLevel >= 0x2000;
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { CloseHandle(token); }
        }

        /// <summary>读一个进程令牌的提权状态。注意 TokenElevation 是 4 字节 uint，用 out 参数重载即可。</summary>
        private static bool ReadElevation(IntPtr token)
        {
            IntPtr buf = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                uint ret;
                if (!GetTokenInformation(token, TokenElevation, buf, sizeof(uint), out ret)) return false;
                return Marshal.ReadInt32(buf) != 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------------------------------------------------------------- 系统整体
        public static int MemoryLoad;
        public static ulong TotalPhys;
        public static ulong AvailPhys;
        public static int ProcessCount;
        public static int ThreadCount;

        private static long _prevIdle, _prevKernel, _prevUser;
        public static double CpuPercent { get; private set; }

        public static void ReadSystemTotals()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref m))
            {
                MemoryLoad = (int)m.dwMemoryLoad;
                TotalPhys = m.ullTotalPhys;
                AvailPhys = m.ullAvailPhys;
            }

            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                if (_prevKernel != 0)
                {
                    long dIdle = idle - _prevIdle;
                    long dKernel = kernel - _prevKernel;
                    long dUser = user - _prevUser;
                    // kernel 时间里包含 idle，所以总时间 = kernel + user，忙时间要再减掉 idle
                    long total = dKernel + dUser;
                    if (total > 0)
                    {
                        double busy = (double)(total - dIdle) / total * 100.0;
                        if (busy < 0) busy = 0;
                        if (busy > 100) busy = 100;
                        // 轻微平滑，避免数字乱跳
                        CpuPercent = CpuPercent * 0.5 + busy * 0.5;
                    }
                }
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            }
        }

        // ---------------------------------------------------------------- 窗口信息
        private sealed class WindowInfo
        {
            public uint Pid;
            public bool Hung;
            public string Title;
        }

        private static Dictionary<uint, WindowInfo> CollectWindows()
        {
            var map = new Dictionary<uint, WindowInfo>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;

                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return true;

                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.Length == 0) return true;

                WindowInfo info;
                bool hung = IsHungAppWindow(h);

                if (!map.TryGetValue(pid, out info))
                {
                    info = new WindowInfo { Pid = pid, Hung = hung, Title = sb.ToString() };
                    map[pid] = info;
                }
                else
                {
                    if (hung) info.Hung = true;
                    // 保留最长（通常最有信息量）的标题
                    if (sb.Length > info.Title.Length) info.Title = sb.ToString();
                }
                return true;
            }, IntPtr.Zero);
            return map;
        }

        // ---------------------------------------------------------------- 枚举
        /// <summary>
        /// 抓一遍所有进程。累计 CPU 时间会在两次调用之间换算成百分比，
        /// 所以第一次调用时 CpuPercent 全是 0（和任务管理器一样）。
        ///
        /// 注意：必须分两步走。
        /// 1) 先用 Toolhelp32 把所有 (pid, 名字, 线程数, 父 pid) 一次性读完并关掉快照；
        /// 2) 再逐个 OpenProcess 补内存/CPU/路径。
        /// 以前把这两步写在一个 do..while 里，采集过程调用的其它 API 会干扰枚举结构体，
        /// 导致枚举到十几个进程就提前结束。
        /// </summary>
        public static List<ProcInfo> Snapshot(ref Dictionary<int, ulong> prevCpu, double intervalSeconds)
        {
            var list = new List<ProcInfo>();
            var windows = CollectWindows();
            var nextCpu = new Dictionary<int, ulong>();
            int threads = 0;

            // ---------- 第 1 步：只枚举，不调用别的 API ----------
            var entries = new List<PROCESSENTRY32W>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;

            try
            {
                var e = new PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (Process32FirstW(snap, ref e))
                {
                    do
                    {
                        entries.Add(e);
                        e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                    } while (Process32NextW(snap, ref e));
                }
            }
            finally { CloseHandle(snap); }

            // ---------- 第 2 步：逐个补细节 ----------
            foreach (var entry in entries)
            {
                var p = new ProcInfo
                {
                    Pid = (int)entry.th32ProcessID,
                    ParentPid = (int)entry.th32ParentProcessID,
                    Name = entry.szExeFile ?? "",
                    Threads = entry.cntThreads,
                    Priority = entry.pcPriClassBase,
                };
                threads += (int)entry.cntThreads;

                if (p.Pid > 0)
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
                    if (h != IntPtr.Zero)
                    {
                        p.Accessible = true;
                        try
                        {
                            var mc = new PROCESS_MEMORY_COUNTERS_EX();
                            mc.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
                            if (GetProcessMemoryInfo(h, ref mc, (int)mc.cb))
                            {
                                p.WorkingSet = mc.WorkingSetSize.ToUInt64();
                                p.PrivateBytes = mc.PrivateUsage.ToUInt64();
                            }

                            long create, exit, kernel, user;
                            if (GetProcessTimes(h, out create, out exit, out kernel, out user))
                            {
                                p.CpuTicks = (ulong)(kernel + user);
                                try { p.StartTime = DateTime.FromFileTime(create); } catch { }
                            }

                            var path = new StringBuilder(1024);
                            uint size = (uint)path.Capacity;
                            if (QueryFullProcessImageNameW(h, 0, path, ref size))
                                p.Path = path.ToString();

                            IntPtr token;
                            if (OpenProcessToken(h, TOKEN_QUERY, out token))
                            {
                                p.Elevated = ReadElevation(token);
                                CloseHandle(token);
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                }

                WindowInfo wi;
                if (windows.TryGetValue((uint)p.Pid, out wi))
                {
                    p.Hung = wi.Hung;
                    p.WindowTitle = wi.Title;
                }

                // CPU 百分比 = (本次累计 - 上次累计) / 间隔
                ulong prev;
                if (prevCpu != null && prevCpu.TryGetValue(p.Pid, out prev) && p.CpuTicks >= prev && intervalSeconds > 0.05)
                {
                    double delta = (p.CpuTicks - prev) / 1e7;      // 秒
                    double pct = delta / intervalSeconds * 100.0;
                    double cores = Environment.ProcessorCount;
                    if (pct > cores * 100) pct = cores * 100;
                    if (pct < 0) pct = 0;
                    p.CpuPercent = pct;
                }
                nextCpu[p.Pid] = p.CpuTicks;

                list.Add(p);
            }

            if (prevCpu != null)
            {
                prevCpu.Clear();
                foreach (var kv in nextCpu) prevCpu[kv.Key] = kv.Value;
            }

            ProcessCount = list.Count;
            ThreadCount = threads;
            return list;
        }

        // ---------------------------------------------------------------- 结束进程
        /// <summary>结束进程。失败时返回 Win32 的说明，而不是静默什么都不做。</summary>
        public static bool Kill(int pid, out string error)
        {
            error = null;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    p.Kill();
                    return true;
                }
            }
            catch (ArgumentException)
            {
                error = "进程已经不存在了";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>结束整棵进程树（先子后父），用于关掉一个带一堆子进程的程序。</summary>
        public static int KillTree(List<ProcInfo> all, int pid, out string error)
        {
            error = null;
            var children = new Dictionary<int, List<int>>();
            foreach (var p in all)
            {
                if (!children.ContainsKey(p.ParentPid)) children[p.ParentPid] = new List<int>();
                children[p.ParentPid].Add(p.Pid);
            }

            var order = new List<int>();
            var stack = new Stack<int>();
            stack.Push(pid);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                order.Add(cur);
                List<int> kids;
                if (children.TryGetValue(cur, out kids))
                    foreach (var k in kids) stack.Push(k);
            }
            order.Reverse();   // 子进程先杀

            int killed = 0;
            foreach (var id in order)
            {
                string err;
                if (Kill(id, out err)) killed++;
                else if (id == pid) error = err;
            }
            return killed;
        }
    }
}
