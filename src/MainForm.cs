using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace MiniTasker
{
    /// <summary>
    /// 极简任务管理器。界面全部自绘，不依赖第三方组件，也不碰 WMI。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---------------------------------------------------------------- 配色
        private static readonly Color BgTop = Color.FromArgb(255, 24, 26, 32);
        private static readonly Color BgBottom = Color.FromArgb(255, 14, 16, 20);
        private static readonly Color CardBg = Color.FromArgb(255, 30, 33, 40);
        private static readonly Color Line = Color.FromArgb(255, 42, 46, 55);
        private static readonly Color TextMain = Color.FromArgb(255, 238, 242, 248);
        private static readonly Color TextDim = Color.FromArgb(255, 150, 158, 172);
        private static readonly Color TextFaint = Color.FromArgb(255, 100, 108, 122);
        private static readonly Color Accent = Color.FromArgb(255, 255, 77, 77);
        private static readonly Color AccentDim = Color.FromArgb(255, 120, 44, 48);
        private static readonly Color Warn = Color.FromArgb(255, 234, 179, 8);
        private static readonly Color Blue = Color.FromArgb(255, 90, 160, 255);
        private static readonly Color RowHover = Color.FromArgb(255, 44, 48, 58);
        private static readonly Color RowSelect = Color.FromArgb(255, 58, 64, 78);

        // ---------------------------------------------------------------- 列
        private enum Col { Name, Pid, Cpu, Memory, Threads, Status }
        private readonly Col[] _columns = { Col.Name, Col.Pid, Col.Cpu, Col.Memory, Col.Threads, Col.Status };

        // ---------------------------------------------------------------- 状态
        private List<ProcInfo> _all = new List<ProcInfo>();
        private List<ProcInfo> _view = new List<ProcInfo>();
        private Dictionary<int, ulong> _prevCpu = new Dictionary<int, ulong>();
        private DateTime _lastSample = DateTime.Now;
        private ProcInfo _selected;
        private ProcInfo _hover;
        private Col _sortCol = Col.Cpu;
        private bool _sortDesc = true;
        private string _filter = "";
        private int _scroll;
        private readonly int _rowH = 26;
        private readonly int _headerH = 30;
        private readonly int _topH = 96;

        private double _cpuPct;
        private int _memLoad;
        private ulong _totalPhys, _availPhys;
        private int _procCount, _threadCount;
        private int _hungCount;

        private readonly Timer _timer = new Timer();
        private readonly TextBox _filterBox;
        private readonly HelpTip _tip = new HelpTip();
        private readonly TipForm _tipForm = new TipForm();
        private NotifyIcon _tray;
        private bool _dragWindow;
        private Point _dragOffset;
        private bool _reallyClosing;
        private volatile bool _sampling;
        private bool _hotkeyOk;
        private string _hotkeyLabel;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_SHOW = 0xA11;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F10 = 0x79;

        /// <summary>
        /// 全局热键。托盘图标在某些系统上会被折叠进「隐藏的图标」区，
        /// 一旦窗口收起来又没有托盘可点，人就会以为程序没了，所以留这个兜底。
        ///
        /// 首选 Ctrl+Alt+F10：Ctrl+Alt+M 之类常被输入法/其它工具占用
        /// （实测本机 RegisterHotKey 返回 1409「热键已被占用」）。
        /// 被占用时自动往后退，逐个试，全都失败才退化成「只能点托盘」。
        /// </summary>
        private static readonly uint[] HotkeyVks = { VK_F10, 0x77 /* F8 */, 0x76 /* F7 */ };

        /// <summary>已注册热键的窗口句柄。句柄变了（重建）才重新注册，避免重复注册把前一次顶掉。</summary>
        private IntPtr _hotkeyHwnd = IntPtr.Zero;

        private void RegisterShowHotkey()
        {
            if (Handle == IntPtr.Zero) return;
            if (_hotkeyHwnd == Handle && _hotkeyOk) return;   // 已经在这个句柄上注册过

            if (_hotkeyHwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(_hotkeyHwnd, HOTKEY_SHOW); } catch { }
                _hotkeyHwnd = IntPtr.Zero;
            }

            for (int i = 0; i < HotkeyVks.Length; i++)
            {
                if (RegisterHotKey(Handle, HOTKEY_SHOW, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, HotkeyVks[i]))
                {
                    _hotkeyOk = true;
                    _hotkeyLabel = "Ctrl+Alt+" + VkName(HotkeyVks[i]);
                    _hotkeyHwnd = Handle;
                    LogHotkey("热键注册成功: " + _hotkeyLabel + "  hwnd=" + Handle);
                    return;
                }
                LogHotkey("热键注册失败: " + VkName(HotkeyVks[i]) + " err=" +
                          System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            _hotkeyOk = false;
            _hotkeyLabel = null;
        }

        private static string VkName(uint vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);   // F1..F24
            return "F10";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_SHOW)
            {
                RestoreFromTray();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private static void LogHotkey(string msg)
        {
            // 默认不写日志；排查时把下面的环境变量设成 1 即可
            try
            {
                if (Environment.GetEnvironmentVariable("MINITASKER_DEBUG") != "1") return;
                string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.log");
                System.IO.File.AppendAllText(p,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        public MainForm()
        {
            Text = "极简任务管理器";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(700, 420);
            Size = new Size(920, 640);
            BackColor = BgTop;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            var wa = Screen.PrimaryScreen.WorkingArea;

            // 默认位置：水平居中、垂直居中偏上。
            // 不能直接用屏幕正中——桌面歌词条固定在「顶部居中、宽 760」，居中放会压住它。
            // 上边界压到 y=180 以上（歌词条最高约 148 + 顶边距），并保证底部不出屏。
            int posY = wa.Y + (wa.Height - Height) / 2;
            if (posY < wa.Y + 180) posY = wa.Y + 180;
            if (posY + Height > wa.Bottom - 10) posY = wa.Bottom - Height - 10;
            if (posY < wa.Y) posY = wa.Y;

            // 右侧留出 170px 给「收起胶囊 / 别的常驻小窗」，避免主窗口压住它们的落点
            int maxW = wa.Width - 170;
            if (Width > maxW) Width = maxW;

            int posX = wa.X + (wa.Width - Width) / 2;
            Location = new Point(posX, posY);

            // 让窗口不出现在任务栏：用 WS_EX_TOOLWINDOW，而不是 ShowInTaskbar。
            // ShowInTaskbar 一改就会重建句柄，全局热键随之失效（踩过）。
            ShowInTaskbar = false;

            _filterBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = CardBg,
                ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 10f),
                Visible = false,
            };
            _filterBox.TextChanged += (s, e) => { _filter = _filterBox.Text; ApplyFilter(); Invalidate(); };
            _filterBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) { CloseFilter(); e.SuppressKeyPress = true; }
            };
            Controls.Add(_filterBox);

            _timer.Interval = 1100;
            _timer.Tick += (s, e) => Sample();

            BuildTray();

            Load += (s, e) =>
            {
                ApplyWindowChrome();
                ProcessApi.DetectElevation();
                Sample();
                _timer.Start();
            };

            // 热键必须在句柄创建后就注册（Load 时 Handle 一定存在，但用这个事件更稳）
            HandleCreated += (s, e) => RegisterShowHotkey();

            FormClosing += (s, e) =>
            {
                if (!_reallyClosing) { e.Cancel = true; HideToTray(); }
            };

            KeyDown += OnKey;
            MouseWheel += (s, e) => Scroll(-e.Delta / 120 * 3);
        }

        // ---------------------------------------------------------------- 圆角窗口
        [System.Runtime.InteropServices.DllImport("Gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight, int nBottom, int w, int h);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyWindowChrome();
            ClampScroll();
        }

        private void ApplyWindowChrome()
        {
            if (WindowState == FormWindowState.Minimized || !IsHandleCreated) return;
            try
            {
                IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 16, 16);
                SetWindowRgn(Handle, rgn, true);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 托盘
        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add("显示主窗口", null, (a, b) => RestoreFromTray());
                menu.Items.Add(new ToolStripSeparator());
                var stat = menu.Items.Add(string.Format("CPU {0:0.0}%  ·  内存 {1}%  ·  进程 {2}", _cpuPct, _memLoad, _procCount));
                stat.Enabled = false;
                if (_hungCount > 0)
                {
                    var it = menu.Items.Add(string.Format("⚠ {0} 个窗口无响应", _hungCount));
                    it.ForeColor = Color.FromArgb(255, 180, 120, 0);
                    it.Enabled = false;
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (a, b) => RealExit());
            };

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "极简任务管理器",
                ContextMenuStrip = menu,
            };
            // 关键：先把菜单挂上再设 Visible，某些系统上顺序反了图标会注册失败
            _tray.Visible = true;
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            _tray.DoubleClick += (s, e) => RestoreFromTray();

            // 自检：确认托盘图标真的注册上了，没上去就写进状态栏提示
            TrayRegistered = _tray.Visible;
        }

        internal bool TrayRegistered { get; private set; }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(255, 24, 26, 32)))
                        g.FillEllipse(b, 0, 0, 31, 31);
                    using (var ring = new Pen(Color.FromArgb(255, 226, 48, 48), 3f))
                        g.DrawEllipse(ring, 3, 3, 25, 25);
                    using (var hand = new Pen(Color.FromArgb(255, 238, 242, 248), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(hand, 16, 16, 16, 8);
                        g.DrawLine(hand, 16, 16, 21, 19);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        /// <summary>
        /// 收进托盘。
        ///
        /// 注意：某些 Windows 版本的系统托盘并不显示 NotifyIcon 注册的图标
        /// （实测本机 Shell_TrayWnd 下没有 ToolbarWindow32，UIA 也读不到任何第三方图标）。
        /// 所以收起之后不能只剩「托盘」这一条路——右侧会留一个小胶囊，点它就能展开。
        /// </summary>
        private void HideToTray()
        {
            Hide();
            ShowMiniPill();
            if (_tray != null)
            {
                try
                {
                    string tip = _hotkeyOk
                        ? "已收起。点右边的「显示」小条，或按 " + _hotkeyLabel + " 可以再打开。"
                        : "已收起。点屏幕右侧的「显示」小条可以再打开。";
                    _tray.ShowBalloonTip(3000, "已收起", tip, ToolTipIcon.Info);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- 收起后的胶囊
        private Rectangle _pillRect;
        private bool _pillVisible;

        private void ShowMiniPill()
        {
            if (_pillForm == null) _pillForm = new PillForm(this);
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = 132, h = 30;

            // 放在屏幕右侧、垂直居中的位置（不是右上角）：
            // 桌面歌词那个程序的收起胶囊固定在右上角，两个都收起来时用同一块地方会互相盖住。
            // 若这个位置被别的窗口占着，就贴着右边缘往下找一个空位。
            int x = wa.Right - w - 14;
            int y = wa.Top + Math.Max(60, (wa.Height - h) / 2);
            var chosen = new Rectangle(x, y, w, h);

            if (!IsSpotFree(chosen))
            {
                bool found = false;
                for (int yy = wa.Top + 10; yy <= wa.Bottom - h - 10; yy += h + 8)
                {
                    var cand = new Rectangle(x, yy, w, h);
                    if (IsSpotFree(cand)) { chosen = cand; found = true; break; }
                }
                if (!found) chosen = new Rectangle(x, y, w, h);   // 全被占就回默认位置
            }

            _pillRect = chosen;
            _pillForm.ShowAt(_pillRect);
            _pillVisible = true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// 这个矩形是否「空」：取四角 + 中心共 5 个采样点，任何一点底下是别的程序窗口就算被占。
        /// 只看中心点不够——中心可能落在两个窗口之间的缝里，但边缘仍然被压住。
        /// </summary>
        private bool IsSpotFree(Rectangle r)
        {
            var pts = new[]
            {
                new POINT { X = r.Left + 4,  Y = r.Top + 4 },
                new POINT { X = r.Right - 4, Y = r.Top + 4 },
                new POINT { X = r.Left + 4,  Y = r.Bottom - 4 },
                new POINT { X = r.Right - 4, Y = r.Bottom - 4 },
                new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 },
            };

            foreach (var pt in pts)
            {
                IntPtr hw = WindowFromPoint(pt);
                if (hw == IntPtr.Zero) continue;
                IntPtr root = GetAncestor(hw, 2);   // GA_ROOT
                if (root == IntPtr.Zero || root == Handle) continue;

                var cls = new System.Text.StringBuilder(128);
                GetClassName(root, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd")
                    continue;   // 桌面/任务栏算背景

                return false;
            }
            return true;
        }

        private void HideMiniPill()
        {
            if (_pillForm != null) _pillForm.HidePill();
            _pillVisible = false;
        }

        internal void PillClicked()
        {
            HideMiniPill();
            RestoreFromTray();
        }

        private PillForm _pillForm;

        private void RestoreFromTray()
        {
            HideMiniPill();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            TopMost = true;
            TopMost = false;
            ApplyWindowChrome();
            Invalidate();
        }

        private void RealExit()
        {
            _reallyClosing = true;
            _timer.Stop();
            try { UnregisterHotKey(Handle, HOTKEY_SHOW); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            if (_pillForm != null) { _pillForm.Close(); _pillForm.Dispose(); _pillForm = null; }
            _tipForm.Dispose();
            Close();
        }

        // ---------------------------------------------------------------- 采样
        private void Sample()
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                double interval = (DateTime.Now - _lastSample).TotalSeconds;
                _lastSample = DateTime.Now;

                ProcessApi.ReadSystemTotals();
                _cpuPct = ProcessApi.CpuPercent;
                _memLoad = ProcessApi.MemoryLoad;
                _totalPhys = ProcessApi.TotalPhys;
                _availPhys = ProcessApi.AvailPhys;

                int selPid = _selected != null ? _selected.Pid : -1;
                var fresh = ProcessApi.Snapshot(ref _prevCpu, interval);
                _procCount = ProcessApi.ProcessCount;
                _threadCount = ProcessApi.ThreadCount;

                _all = fresh;
                ApplyFilter();
                _selected = selPid > 0 ? _all.FirstOrDefault(p => p.Pid == selPid) : null;
                _hungCount = _all.Count(p => p.Hung);

                if (_tray != null)
                {
                    string tip = string.Format("CPU {0:0.#}% · 内存 {1}% · 进程 {2}", _cpuPct, _memLoad, _procCount);
                    if (_hungCount > 0) tip += " · " + _hungCount + " 个无响应";
                    if (tip.Length > 62) tip = tip.Substring(0, 62);
                    try { _tray.Text = tip; } catch { }
                }
            }
            catch { /* 采样失败不影响界面继续用 */ }
            finally { _sampling = false; }

            Invalidate();
        }

        private void ApplyFilter()
        {
            IEnumerable<ProcInfo> q = _all;
            if (!string.IsNullOrWhiteSpace(_filter))
            {
                string f = _filter.Trim();
                q = q.Where(p =>
                    p.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Pid.ToString() == f
                    || (p.WindowTitle ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            switch (_sortCol)
            {
                case Col.Name:
                    q = _sortDesc ? q.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
                                  : q.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
                    break;
                case Col.Pid: q = _sortDesc ? q.OrderByDescending(p => p.Pid) : q.OrderBy(p => p.Pid); break;
                case Col.Cpu: q = _sortDesc ? q.OrderByDescending(p => p.CpuPercent) : q.OrderBy(p => p.CpuPercent); break;
                case Col.Memory: q = _sortDesc ? q.OrderByDescending(p => p.WorkingSet) : q.OrderBy(p => p.WorkingSet); break;
                case Col.Threads: q = _sortDesc ? q.OrderByDescending(p => p.Threads) : q.OrderBy(p => p.Threads); break;
                case Col.Status: q = _sortDesc ? q.OrderByDescending(p => p.Hung) : q.OrderBy(p => p.Hung); break;
            }
            _view = q.ToList();
            ClampScroll();
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, _view.Count - VisibleRows());
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        private void Scroll(int lines)
        {
            if (_all.Count == 0) return;
            _scroll += lines;
            ClampScroll();
            Invalidate();
        }

        // ---------------------------------------------------------------- 布局
        private Rectangle ListRect
        {
            get { return new Rectangle(0, _topH + _headerH, ClientSize.Width, Math.Max(0, ClientSize.Height - _topH - _headerH - 22)); }
        }

        private int VisibleRows() { return Math.Max(1, ListRect.Height / _rowH); }

        private int[] ColWidths()
        {
            int w = ClientSize.Width;
            const int pad = 14, pidW = 74, cpuW = 76, memW = 96, thW = 64, stW = 100;
            int nameW = Math.Max(120, w - pad * 2 - pidW - cpuW - memW - thW - stW);
            return new[] { nameW, pidW, cpuW, memW, thW, stW };
        }

        private int ColX(int index)
        {
            var widths = ColWidths();
            int x = 14;
            for (int i = 0; i < index; i++) x += widths[i];
            return x;
        }

        private Rectangle FilterRect()
        {
            // 控件独占最右侧一列，不和任何文字重叠
            int w = Math.Min(250, Math.Max(150, ClientSize.Width / 4));
            return new Rectangle(ClientSize.Width - 110 - w - 10, _topH - 25, w, 20);
        }

        private Rectangle KillRect() { return new Rectangle(ClientSize.Width - 104, _topH - 26, 92, 22); }
        private Rectangle MinRect() { return new Rectangle(ClientSize.Width - 84, 12, 26, 24); }
        private Rectangle CloseRect() { return new Rectangle(ClientSize.Width - 50, 12, 26, 24); }

        // ---------------------------------------------------------------- 绘制
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _tip.Clear();

            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, Math.Max(2, ClientSize.Height)), BgTop, BgBottom, 90f))
                g.FillRectangle(brush, ClientRectangle);

            DrawHeader(g);
            DrawSection(g);
            DrawTable(g);
            DrawStatusBar(g);
        }

        private void DrawHeader(Graphics g)
        {
            using (var fTitle = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold))
            using (var fSub = new Font("Microsoft YaHei UI", 8.8f))
            {
                using (var b = new SolidBrush(TextMain)) g.DrawString("极简任务管理器", fTitle, b, 16, 14);
                using (var b = new SolidBrush(TextFaint)) g.DrawString("不依赖 WMI · 纯 Win32 采样 · 关闭窗口收进托盘", fSub, b, 176, 21);
            }

            DrawTitleButton(g, MinRect(), "—", "最小化到托盘（程序继续在后台）");
            DrawTitleButton(g, CloseRect(), "×", "收进托盘（不是退出，可在托盘菜单里退出）");

            int y = 44;
            var gCpu = new Rectangle(16, y, 150, 44);
            var gMem = new Rectangle(174, y, 150, 44);
            DrawGauge(g, gCpu, "CPU", _cpuPct, Accent);
            DrawGauge(g, gMem, "内存", _memLoad, Blue);

            // 左侧：两行统计 + 无响应提示
            using (var f = new Font("Microsoft YaHei UI", 9f))
            using (var b = new SolidBrush(TextDim))
            {
                double usedGb = (_totalPhys - _availPhys) / 1024.0 / 1024.0 / 1024.0;
                double totalGb = _totalPhys / 1024.0 / 1024.0 / 1024.0;
                int statX = gMem.Right + 16;
                g.DrawString(string.Format("内存 {0:0.0} / {1:0.0} GB", usedGb, totalGb), f, b, statX, y + 2);
                g.DrawString(string.Format("进程 {0}   线程 {1}", _procCount, _threadCount), f, b, statX, y + 24);
            }

            int cursorX = gMem.Right + 16 + 150;

            if (!ProcessApi.IsElevated)
            {
                var btn = ElevateRect();
                bool canRunAs = ProcessApi.RunAsWillElevate;
                bool hover = canRunAs && btn.Contains(PointToClient(Cursor.Position));

                using (var b = new SolidBrush(hover ? Color.FromArgb(255, 70, 60, 30) : Color.FromArgb(255, 42, 38, 28)))
                using (var path = Rounded(btn, 7)) g.FillPath(b, path);

                using (var f = new Font("Microsoft YaHei UI", 8.6f))
                using (var tb = new SolidBrush(Warn))
                {
                    string text = canRunAs ? "仅显示部分进程 · 以管理员重启" : "仅显示部分进程 · 本机无法提权";
                    g.DrawString(text, f, tb, btn.X + 9, btn.Y + 4);
                }

                _tip.Register(btn, canRunAs
                    ? "非管理员权限下只能看到与自己同级别的进程。点这里以管理员身份重新启动。"
                    : "本机登录的是内置 Administrator 账户且未启用 FilterAdministratorToken，UAC 对它不生效，"
                      + "「以管理员运行」只会重开进程、权限不变。要看到全部进程，需要用另一个管理员账户登录。");
            }

            if (_hungCount > 0)
            {
                using (var f = new Font("Microsoft YaHei UI", 9f))
                using (var wb = new SolidBrush(Warn))
                {
                    string warn = string.Format("⚠ {0} 个窗口无响应", _hungCount);
                    float wx = ProcessApi.IsElevated ? cursorX : ElevateRect().Right + 14;
                    g.DrawString(warn, f, wb, wx, y + 14);
                    _tip.Register(new Rectangle((int)wx, y + 10, 180, 20), "这些程序的界面已经卡死，在「状态」列标黄");
                }
            }

            using (var p = new Pen(Line))
                g.DrawLine(p, 0, _topH - 1, ClientSize.Width, _topH - 1);
        }

        private Rectangle ElevateRect()
        {
            return new Rectangle(ProcessApi.IsElevated ? 0 : 490, 48, 186, 22);
        }

        private void DrawTitleButton(Graphics g, Rectangle r, string glyph, string tip)
        {
            bool hover = r.Contains(PointToClient(Cursor.Position));
            if (hover)
            {
                using (var b = new SolidBrush(RowHover))
                using (var path = Rounded(r, 7)) g.FillPath(b, path);
            }
            using (var f = new Font("Microsoft YaHei UI", 11f))
            using (var tb = new SolidBrush(hover ? TextMain : TextDim))
            {
                var s = g.MeasureString(glyph, f);
                g.DrawString(glyph, f, tb, r.X + (r.Width - s.Width) / 2, r.Y + (r.Height - s.Height) / 2);
            }
            _tip.Register(r, tip);
        }

        private void DrawGauge(Graphics g, Rectangle r, string label, double value, Color color)
        {
            using (var bg = new SolidBrush(CardBg))
            using (var path = Rounded(r, 10)) g.FillPath(bg, path);

            double pct = Math.Max(0, Math.Min(100, value));
            var track = new Rectangle(r.X + 10, r.Bottom - 13, r.Width - 20, 5);
            using (var tb = new SolidBrush(Color.FromArgb(255, 46, 50, 60)))
                FillRounded(g, track, 3, tb);

            int fillW = (int)(track.Width * pct / 100.0);
            if (fillW > 2)
            {
                Color c = pct >= 85 ? Accent : (pct >= 65 ? Warn : color);
                using (var fb = new SolidBrush(c))
                    FillRounded(g, new Rectangle(track.X, track.Y, fillW, track.Height), 3, fb);
            }

            using (var f = new Font("Microsoft YaHei UI", 8.5f))
            using (var fb2 = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold))
            {
                using (var b = new SolidBrush(TextFaint)) g.DrawString(label, f, b, r.X + 10, r.Y + 4);
                using (var b = new SolidBrush(TextMain)) g.DrawString(string.Format("{0:0.#}%", pct), fb2, b, r.X + 10, r.Y + 17);
            }
        }

        private void DrawSection(Graphics g)
        {
            int y = _topH;
            using (var b = new SolidBrush(TextDim))
            using (var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                g.DrawString("进程", f, b, 16, y + 8);

            string count = _view.Count == _all.Count
                ? string.Format("{0} 个", _all.Count)
                : string.Format("{0} / {1} 个", _view.Count, _all.Count);
            using (var b = new SolidBrush(TextFaint))
            using (var f = new Font("Microsoft YaHei UI", 8.8f))
                g.DrawString(count, f, b, 58, y + 10);

            var box = FilterRect();
            using (var bg = new SolidBrush(CardBg))
            using (var path = Rounded(box, 8)) g.FillPath(bg, path);

            if (_filterBox != null && _filterBox.Visible)
            {
                _filterBox.Bounds = new Rectangle(box.X + 10, box.Y + 4, box.Width - 20, box.Height - 8);
            }
            else if (_filterBox != null)
            {
                using (var b = new SolidBrush(TextFaint))
                using (var f = new Font("Microsoft YaHei UI", 9f))
                    g.DrawString("搜索进程名 / PID   (Ctrl+F)", f, b, box.X + 10, box.Y + 4);
            }

            DrawButton(g, KillRect(), "结束任务", _selected != null, AccentDim);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, bool enabled, Color color)
        {
            bool hover = enabled && r.Contains(PointToClient(Cursor.Position));
            using (var b = new SolidBrush(enabled ? (hover ? color : CardBg) : CardBg))
            using (var path = Rounded(r, 7)) g.FillPath(b, path);

            using (var f = new Font("Microsoft YaHei UI", 9f))
            using (var tb = new SolidBrush(enabled ? TextMain : Color.FromArgb(255, 78, 84, 96)))
            {
                var s = g.MeasureString(text, f);
                g.DrawString(text, f, tb, r.X + (r.Width - s.Width) / 2, r.Y + (r.Height - s.Height) / 2);
            }
            if (enabled) _tip.Register(r, "结束选中的进程（Del）");
        }

        private void DrawTable(Graphics g)
        {
            var widths = ColWidths();
            var list = ListRect;

            using (var b = new SolidBrush(TextFaint))
            using (var f = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold))
            {
                for (int i = 0; i < _columns.Length; i++)
                {
                    var col = _columns[i];
                    string label = ColLabel(col);
                    if (col == _sortCol) label += _sortDesc ? " ↓" : " ↑";
                    int x = ColX(i);
                    if (IsRightAligned(col))
                        x += widths[i] - (int)g.MeasureString(label, f).Width - 10;
                    g.DrawString(label, f, b, x, _topH + 8);
                    _tip.Register(new Rectangle(ColX(i), _topH, widths[i], _headerH), "按 " + ColLabel(col) + " 排序");
                }
            }

            int rows = VisibleRows();
            for (int i = 0; i < rows; i++)
            {
                int idx = _scroll + i;
                if (idx >= _view.Count) break;
                var p = _view[idx];
                var rowRect = new Rectangle(0, list.Y + i * _rowH, list.Width, _rowH);

                if (p == _selected)
                    using (var b = new SolidBrush(RowSelect)) g.FillRectangle(b, rowRect);

                if (p.Hung)
                    using (var b = new SolidBrush(Color.FromArgb(255, 150, 20, 20)))
                        g.FillRectangle(b, new Rectangle(rowRect.X, rowRect.Y + 3, 3, rowRect.Height - 6));

                DrawRow(g, rowRect, p, widths);
            }

            if (_view.Count > rows)
            {
                int trackH = list.Height;
                int thumbH = Math.Max(28, (int)((double)rows / _view.Count * trackH));
                int maxScroll = Math.Max(1, _view.Count - rows);
                int thumbY = list.Y + (int)((double)_scroll / maxScroll * (trackH - thumbH));
                using (var b = new SolidBrush(Color.FromArgb(255, 60, 66, 78)))
                    FillRounded(g, new Rectangle(ClientSize.Width - 7, thumbY, 4, thumbH), 2, b);
            }

            // 筛选为空时的提示
            if (_view.Count == 0 && _all.Count > 0)
            {
                using (var b = new SolidBrush(TextFaint))
                using (var f = new Font("Microsoft YaHei UI", 10f))
                {
                    string t = "没有匹配的进程";
                    var s = g.MeasureString(t, f);
                    g.DrawString(t, f, b, (ClientSize.Width - s.Width) / 2, list.Y + 40);
                }
            }
        }

        private void DrawRow(Graphics g, Rectangle r, ProcInfo p, int[] widths)
        {
            int textY = r.Y + (r.Height - 15) / 2;
            using (var f = new Font("Microsoft YaHei UI", 9.2f))
            {
                using (var b = new SolidBrush(p.Hung ? Warn : TextMain))
                    g.DrawString(p.Name, f, b, ColX(0), textY);

                using (var b = new SolidBrush(TextDim))
                    g.DrawString(p.Pid.ToString(), f, b, ColX(1), textY);

                string cpu = p.CpuPercent >= 0.05 ? p.CpuPercent.ToString("0.0") + "%" : "—";
                using (var b = new SolidBrush(p.CpuPercent >= 40 ? Accent : (p.CpuPercent >= 10 ? Warn : TextDim)))
                {
                    int x = ColX(2) + widths[2] - (int)g.MeasureString(cpu, f).Width - 10;
                    g.DrawString(cpu, f, b, x, textY);
                }

                string mem = p.WorkingSet > 0 ? p.MbDead.ToString("0.0") + " MB" : "—";
                using (var b = new SolidBrush(TextDim))
                {
                    int x = ColX(3) + widths[3] - (int)g.MeasureString(mem, f).Width - 10;
                    g.DrawString(mem, f, b, x, textY);
                }

                string th = p.Threads.ToString();
                using (var b = new SolidBrush(TextDim))
                {
                    int x = ColX(4) + widths[4] - (int)g.MeasureString(th, f).Width - 10;
                    g.DrawString(th, f, b, x, textY);
                }
            }

            int sx = ColX(5);
            int sy = r.Y + (r.Height - 16) / 2;
            if (p.Hung) Chip(g, sx, sy, "无响应", Warn);
            else if (!p.Accessible) Chip(g, sx, sy, "系统", TextFaint);
            else if (p.Elevated) Chip(g, sx, sy, "管理员", Blue);
        }

        private void Chip(Graphics g, int x, int y, string text, Color color)
        {
            using (var f = new Font("Microsoft YaHei UI", 8f))
            {
                var s = g.MeasureString(text, f);
                var r = new Rectangle(x, y, (int)s.Width + 12, 16);
                using (var b = new SolidBrush(Color.FromArgb(42, color)))
                    FillRounded(g, r, 6, b);
                using (var tb = new SolidBrush(color))
                    g.DrawString(text, f, tb, r.X + 6, r.Y);
            }
        }

        private void DrawStatusBar(Graphics g)
        {
            int y = ClientSize.Height - 22;
            using (var p = new Pen(Line))
                g.DrawLine(p, 0, y, ClientSize.Width, y);

            using (var f = new Font("Microsoft YaHei UI", 8.4f))
            using (var b = new SolidBrush(TextFaint))
            {
                string left = _selected != null
                    ? string.Format("{0}  (PID {1})   {2}", _selected.Name, _selected.Pid,
                        string.IsNullOrEmpty(_selected.Path) ? "路径不可读" : _selected.Path)
                    : "选中一行看详情 · 双击打开进程目录 · Del 结束任务 · Ctrl+F 搜索 · 关闭窗口=收进托盘";
                left = ClipText(g, left, f, ClientSize.Width - 250);
                g.DrawString(left, f, b, 16, y + 3);

                string right = _hotkeyOk ? _hotkeyLabel + " 呼出" : "收起后点屏幕右侧「显示」条";
                var s = g.MeasureString(right, f);
                g.DrawString(right, f, b, ClientSize.Width - s.Width - 16, y + 3);
            }
        }

        private string ClipText(Graphics g, string text, Font f, int maxWidth)
        {
            if (maxWidth < 40) return "";
            if (g.MeasureString(text, f).Width <= maxWidth) return text;
            while (text.Length > 4 && g.MeasureString(text + "…", f).Width > maxWidth)
                text = text.Substring(0, text.Length - 2);
            return text + "…";
        }

        private static string ColLabel(Col c)
        {
            switch (c)
            {
                case Col.Name: return "名称";
                case Col.Pid: return "PID";
                case Col.Cpu: return "CPU";
                case Col.Memory: return "内存";
                case Col.Threads: return "线程";
                case Col.Status: return "状态";
            }
            return "";
        }

        private static bool IsRightAligned(Col c)
        {
            return c == Col.Pid || c == Col.Cpu || c == Col.Memory || c == Col.Threads;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRounded(Graphics g, Rectangle r, int radius, Brush brush)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = Rounded(r, radius))
                g.FillPath(brush, path);
        }

        // ---------------------------------------------------------------- 鼠标 / 键盘
        private ProcInfo RowAt(Point pt)
        {
            var list = ListRect;
            if (pt.Y < list.Y || pt.Y > list.Bottom || pt.X < 0 || pt.X > list.Width) return null;
            int i = (pt.Y - list.Y) / _rowH;
            int idx = _scroll + i;
            if (idx < 0 || idx >= _view.Count) return null;
            return _view[idx];
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = RowAt(e.Location);
            if (p != _hover) { _hover = p; Invalidate(); }

            bool overControl = MinRect().Contains(e.Location) || CloseRect().Contains(e.Location)
                               || KillRect().Contains(e.Location) || FilterRect().Contains(e.Location)
                               || (!ProcessApi.IsElevated && ElevateRect().Contains(e.Location));
            bool overHeader = e.Y >= _topH && e.Y < _topH + _headerH;
            Cursor = (p != null || overControl || overHeader) ? Cursors.Hand : Cursors.Default;

            // 悬停提示
            var tipText = _tip.HitTest(e.Location);
            _tipForm.UpdateTip(tipText, Cursor.Position);

            if (_dragWindow)
            {
                Location = new Point(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _tipForm.UpdateTip(null, Point.Empty);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (MinRect().Contains(e.Location)) { HideToTray(); return; }
            if (CloseRect().Contains(e.Location)) { HideToTray(); return; }

            if (!ProcessApi.IsElevated && ElevateRect().Contains(e.Location))
            {
                if (ProcessApi.RunAsWillElevate)
                {
                    RestartAsAdmin();
                }
                else
                {
                    MessageBox.Show(this,
                        "本机无法通过「以管理员身份运行」提权。\n\n"
                        + "原因：当前登录的是内置 Administrator 账户，且系统没有启用\n"
                        + "FilterAdministratorToken（UAC 对该账户不生效）。这种情况下\n"
                        + "「以管理员运行」只会把程序重新启动一遍，权限级别不变。\n\n"
                        + "如果想看到全部进程，有两种办法：\n"
                        + "  1. 启用 FilterAdministratorToken 后重新登录（需要改注册表并重启）\n"
                        + "  2. 用另一个「管理员」组的普通账户登录\n\n"
                        + "注意：这两种做法都会改变系统的 UAC 行为，请确认后再操作。",
                        "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            if (KillRect().Contains(e.Location)) { KillSelected(); return; }

            Col col;
            if (ClickedHeader(e.Location, out col))
            {
                if (_sortCol == col) _sortDesc = !_sortDesc;
                else { _sortCol = col; _sortDesc = col != Col.Name; }
                ApplyFilter();
                Invalidate();
                return;
            }

            var p = RowAt(e.Location);
            if (p != null)
            {
                _selected = p;
                Invalidate();
                return;
            }

            // 拖动窗口：顶部空白区
            if (e.Y < 40)
            {
                _dragWindow = true;
                _dragOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragWindow) { _dragWindow = false; Capture = false; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var p = RowAt(e.Location);
            if (p != null && !string.IsNullOrEmpty(p.Path))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p.Path + "\""); }
                catch { }
            }
            base.OnMouseDoubleClick(e);
        }

        private bool ClickedHeader(Point pt, out Col col)
        {
            col = Col.Name;
            if (pt.Y < _topH || pt.Y >= _topH + _headerH) return false;
            var widths = ColWidths();
            for (int i = 0; i < _columns.Length; i++)
            {
                int x = ColX(i);
                if (pt.X >= x && pt.X < x + widths[i]) { col = _columns[i]; return true; }
            }
            return false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            OnKey(this, e);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F5:
                    Sample();
                    e.Handled = true;
                    break;
                case Keys.Delete:
                    KillSelected();
                    e.Handled = true;
                    break;
                case Keys.F:
                    if (e.Control) { OpenFilter(); e.Handled = true; e.SuppressKeyPress = true; }
                    break;
                case Keys.Escape:
                    if (_filterBox.Visible) { CloseFilter(); e.Handled = true; }
                    break;
                case Keys.Up:
                case Keys.Down:
                    {
                        int idx = _selected != null ? _view.IndexOf(_selected) : -1;
                        idx += e.KeyCode == Keys.Down ? 1 : -1;
                        if (idx >= 0 && idx < _view.Count)
                        {
                            _selected = _view[idx];
                            if (idx < _scroll) _scroll = idx;
                            if (idx >= _scroll + VisibleRows()) _scroll = idx - VisibleRows() + 1;
                            Invalidate();
                        }
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void OpenFilter()
        {
            if (_filterBox.Visible) return;
            _filterBox.Visible = true;
            _filterBox.Bounds = new Rectangle(FilterRect().X + 10, FilterRect().Y + 4, FilterRect().Width - 20, FilterRect().Height - 8);
            _filterBox.Focus();
        }

        private void CloseFilter()
        {
            _filterBox.Visible = false;
            _filterBox.Text = "";
            _filter = "";
            ApplyFilter();
            Invalidate();
            Focus();
        }

        // ---------------------------------------------------------------- 操作
        private void KillSelected()
        {
            if (_selected == null) return;
            var p = _selected;

            if (p.Pid <= 4)
            {
                MessageBox.Show(this, "系统关键进程不能结束。", "极简任务管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string subject = p.Hung ? p.Name + "（无响应）" : p.Name;
            var r = MessageBox.Show(this,
                string.Format("结束 {0}  (PID {1})？\n\n未保存的数据会丢失。", subject, p.Pid),
                "确认结束进程", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            string error;
            if (!ProcessApi.Kill(p.Pid, out error))
            {
                var r2 = MessageBox.Show(this,
                    "结束失败：" + error + "\n\n可能是权限不足（系统进程 / 其他用户）。\n"
                    + "以管理员身份重新启动，启动后会自动再试一次结束这个进程。要现在重启吗？",
                    "结束进程失败", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (r2 == DialogResult.Yes) RestartAsAdmin(p.Pid, false);
                return;
            }

            _selected = null;
            Sample();
        }

        /// <summary>
        /// 以管理员身份重新启动。
        ///
        /// 这里有两个坑，都会表现成「点了没反应」：
        /// 1. 单实例互斥锁——新进程起来时旧进程还没退出，会被挡住然后自己退出，
        ///    结果旧进程关了、新进程也没了。所以必须先退出再启动。
        /// 2. 用户点了 UAC 的「否」会抛 Win32Exception(1223)，旧代码用空的 catch 吞掉，
        ///    界面上什么都看不到。现在把结果明确告诉用户。
        /// </summary>
        private void RestartAsAdmin(int pidToKill = -1, bool killTree = false)
        {
            string exe = Application.ExecutablePath;
            string args = (pidToKill > 0 ? "--kill " + pidToKill + (killTree ? " --tree" : "") + " --elevated" : "--elevated");

            // 先释放单实例锁
            ReleaseSingleInstance();
            _reallyClosing = true;
            _timer.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            Hide();

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = 用户在 UAC 提示上点了「否」
                string why = ex.NativeErrorCode == 1223
                    ? "你取消了 UAC 提示，没有以管理员身份重启。"
                    : "无法以管理员身份启动：" + ex.Message;
                ShowBanner(why);
                // 取消提权就回到原来的窗口，别让程序直接消失了
                if (_tray == null) BuildTray();
                Show();
                _timer.Start();
                return;
            }
            catch (Exception ex)
            {
                ShowBanner("重启失败：" + ex.Message);
                if (_tray == null) BuildTray();
                Show();
                _timer.Start();
                return;
            }

            Close();
        }

        /// <summary>程序内的一条临时提示（画在标题下面），几秒后自动消失。</summary>
        private string _banner;
        private DateTime _bannerUntil = DateTime.MinValue;

        private void ShowBanner(string text)
        {
            _banner = text;
            _bannerUntil = DateTime.Now.AddSeconds(6);
            Invalidate();
        }

        private void ReleaseSingleInstance()
        {
            try
            {
                if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch { }
        }

        private System.Threading.Mutex _mutex;

        /// <summary>由 Program 传入的单实例锁；「以管理员重启」时要先释放它。</summary>
        internal System.Threading.Mutex HoldMutex
        {
            get { return _mutex; }
            set { _mutex = value; }
        }

        // ---------------------------------------------------------------- 提示气泡
        private sealed class HelpTip
        {
            private readonly List<KeyValuePair<Rectangle, string>> _regions = new List<KeyValuePair<Rectangle, string>>();

            public void Clear() { _regions.Clear(); }

            public void Register(Rectangle r, string text)
            {
                _regions.Add(new KeyValuePair<Rectangle, string>(r, text));
            }

            public string HitTest(Point p)
            {
                foreach (var kv in _regions)
                    if (kv.Key.Contains(p)) return kv.Value;
                return null;
            }
        }
    }

    /// <summary>
    /// 收起后留在屏幕上的小胶囊。点它就展开主窗口。
    ///
    /// 为什么需要它：不是所有 Windows 版本都会显示 NotifyIcon 注册的托盘图标
    /// （本机实测 Shell_TrayWnd 下连 ToolbarWindow32 都没有，UIA 也读不到第三方图标）。
    /// 如果没有这个胶囊，窗口一收起来用户就再也找不回来，只能结束进程重开。
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    internal sealed class PillForm : Form
    {
        private readonly MainForm _owner;
        private bool _hover;

        public PillForm(MainForm owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 24, 26, 32);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        [System.Runtime.InteropServices.DllImport("Gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        public void ShowAt(Rectangle rect)
        {
            Bounds = rect;
            if (!Visible) Show();
            try
            {
                IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 15, 15);
                SetWindowRgn(Handle, rgn, true);
            }
            catch { }
            TopMost = true;
            Invalidate();
        }

        public void HidePill()
        {
            Hide();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _owner.PillClicked();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(_hover ? Color.FromArgb(255, 38, 42, 52) : Color.FromArgb(255, 26, 29, 36)))
                g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(_hover ? 120 : 60, 255, 255, 255)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            using (var f = new Font("Microsoft YaHei UI", 9.5f))
            using (var b = new SolidBrush(Color.FromArgb(255, 220, 226, 236)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("▣  显示任务管理器", f, b, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    /// <summary>鼠标悬停的小提示条。比起 ToolTip 组件，这个不闪、不会挡住点击。</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    internal sealed class TipForm : Form
    {
        private string _text = "";

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 40, 44, 54);
            Size = new Size(10, 10);
            Visible = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void UpdateTip(string text, Point screen)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (Visible) Visible = false;
                return;
            }
            if (_text == text && Visible) return;
            _text = text;

            using (var g = CreateGraphics())
            {
                var size = g.MeasureString(text, Font);
                Size = new Size((int)size.Width + 16, 24);
            }
            Location = new Point(screen.X + 14, screen.Y + 18);
            if (!Visible) Visible = true;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(255, 42, 46, 56));
            using (var b = new SolidBrush(Color.FromArgb(255, 225, 230, 238)))
                e.Graphics.DrawString(_text, Font, b, 8, 4);
        }
    }
}
