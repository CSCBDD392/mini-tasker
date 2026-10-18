using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MiniTasker
{
    /// <summary>离线自检：不开界面，直接调 ProcessApi 看数据对不对。</summary>
    internal static class SelfTest
    {
        internal static void Run()
        {
            var sb = new System.Text.StringBuilder();
            Action<string> w = (s) => { sb.AppendLine(s); Console.WriteLine(s); };

            ProcessApi.DetectElevation();
            w("完整性级别 = 0x" + ProcessApi.IntegrityLevel.ToString("X4") + "  管理员 = " + ProcessApi.IsElevated);

            var prev = new Dictionary<int, ulong>();
            ProcessApi.ReadSystemTotals();
            var first = ProcessApi.Snapshot(ref prev, 1.0);
            w("第 1 次: 进程数=" + first.Count + " 线程=" + ProcessApi.ThreadCount);

            System.Threading.Thread.Sleep(1200);
            ProcessApi.ReadSystemTotals();
            var second = ProcessApi.Snapshot(ref prev, 1.2);
            w("第 2 次: 进程数=" + second.Count);

            second.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
            w("\nCPU Top 10:");
            for (int i = 0; i < Math.Min(10, second.Count); i++)
            {
                var p = second[i];
                w(string.Format("  {0,-26} pid={1,-6} cpu={2,6:0.0}%  mem={3,8:0.0} MB  th={4,4} hung={5}",
                    Trunc(p.Name, 26), p.Pid, p.CpuPercent, p.MbDead, p.Threads, p.Hung));
            }

            int acc = 0, hung = 0;
            foreach (var p in second) { if (p.Accessible) acc++; if (p.Hung) hung++; }
            w(string.Format("\n可访问={0}  无响应={1}", acc, hung));
            w("系统 CPU=" + ProcessApi.CpuPercent.ToString("0.0") + "%  内存=" + ProcessApi.MemoryLoad + "%");

            try
            {
                string outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-result.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
