using System;
using System.Windows.Forms;

namespace MiniTasker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "MiniTasker.SingleInstance", out created))
            {
                if (!created)
                {
                    // 已经有一个在跑：把那个叫出来
                    MessageBox.Show("极简任务管理器已经在托盘里了，双击托盘图标即可打开。",
                        "极简任务管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool hiddenStart = false;
                foreach (var a in args)
                {
                    if (a == "--tray" || a == "-tray") hiddenStart = true;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                {
                    MessageBox.Show(e.Exception.Message, "极简任务管理器 出错",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                var form = new MainForm();
                if (hiddenStart)
                {
                    form.WindowState = FormWindowState.Minimized;
                }
                Application.Run(form);
            }
        }
    }
}
