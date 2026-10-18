using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace MiniTasker
{
    internal static class Program
    {
        /// <summary>
        /// 单实例锁。要在 MainForm 里持有它，因为「以管理员重启」必须先释放锁再拉起新进程，
        /// 否则新进程起来时旧进程还没退出，会被锁挡掉然后自己退出——表现就是「点了没反应」。
        /// </summary>
        internal static System.Threading.Mutex InstanceMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            // --kill <pid> [--tree]：以管理员重启后，先把旧的非管理员实例杀掉
            int killPid = -1;
            bool killTree = false;
            bool elevatedFlag = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--kill" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out killPid);
                    i++;
                }
                else if (args[i] == "--tree") killTree = true;
                else if (args[i] == "--elevated") elevatedFlag = true;
            }

            bool created;
            InstanceMutex = new System.Threading.Mutex(true, "MiniTasker.SingleInstance", out created);

            if (!created)
            {
                // 已经有实例在跑。如果是提升后启动的，先把旧实例收掉再接管。
                if (elevatedFlag)
                {
                    // 给旧实例一点时间自己退
                    for (int i = 0; i < 20; i++)
                    {
                        System.Threading.Thread.Sleep(150);
                        bool again;
                        var m = new System.Threading.Mutex(true, "MiniTasker.SingleInstance", out again);
                        if (again)
                        {
                            InstanceMutex = m;
                            created = true;
                            break;
                        }
                        m.Dispose();
                    }
                }

                if (!created)
                {
                    MessageBox.Show("极简任务管理器已经在托盘里了，双击托盘图标即可打开。",
                        "极简任务管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            // 新实例接管前，把旧的非管理员进程结束掉
            if (killPid > 0 && killPid != Process.GetCurrentProcess().Id)
            {
                try
                {
                    // 先等旧实例自己退（它可能是被我们 Close 的），等不到再强杀
                    for (int i = 0; i < 10; i++)
                    {
                        bool alive;
                        try { using (var q = Process.GetProcessById(killPid)) { alive = !q.HasExited; } }
                        catch { alive = false; }
                        if (!alive) break;
                        System.Threading.Thread.Sleep(100);
                    }

                    string err;
                    if (killTree) ProcessApi.KillTreeSnapshot(killPid, out err);
                    else ProcessApi.Kill(killPid, out err);
                }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, "极简任务管理器 出错",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            var form = new MainForm();
            form.HoldMutex = InstanceMutex;
            Application.Run(form);

            try { if (InstanceMutex != null) InstanceMutex.ReleaseMutex(); } catch { }
        }
    }
}
