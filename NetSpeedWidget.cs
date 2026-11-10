// NetSpeed Widget - a small always-on-top download/upload speed meter for Windows 10/11.
//
// Single source file, written in C# 5 so it compiles with the csc.exe that ships with
// .NET Framework 4.x on every Windows 10/11 PC (see build.bat). No installs needed.
//
// How the numbers are measured
//   * Windows keeps 64-bit byte counters for every network interface (InOctets / OutOctets).
//     We read them with GetIfTable2 (iphlpapi.dll) - the same source Task Manager and
//     Performance Monitor use. 64-bit counters never wrap in practice.
//   * Speed = (bytes now - bytes last time) / (exact time between the two reads).
//     The time comes from Stopwatch (QueryPerformanceCounter), not from the UI timer,
//     so timer jitter does not skew the result.
//   * In Auto mode we add up every connected *physical* adapter and skip loopback,
//     NDIS filter layers (WFP / QoS duplicates) and virtual adapters (Hyper-V, WSL,
//     Docker, VPN tunnels), whose traffic also passes through the physical adapter and
//     would otherwise be counted twice.
//
// This file is plain ASCII on purpose: old csc reads files without a BOM in the
// system code page, so non-ASCII characters are written as \u escapes.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedWidget
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\NetSpeedWidget.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("NetSpeed Widget is already running. Look for its icon in the notification area.",
                        WidgetForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { NativeMethods.SetProcessDPIAware(); } catch (Exception) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    // ------------------------------------------------------------------ Win32

    internal static class NativeMethods
    {
        [DllImport("iphlpapi.dll")]
        internal static extern int GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll")]
        internal static extern void FreeMibTable(IntPtr memory);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll")]
        internal static extern int SHQueryUserNotificationState(out int state);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    // ------------------------------------------------------------------ Interface counters

    internal sealed class AdapterSample
    {
        public ulong Luid;
        public string Alias;
        public string Description;
        public uint IfType;
        public bool Hardware;
        public bool Filter;
        public bool Up;
        public ulong TxLinkSpeed;   // bits per second
        public ulong RxLinkSpeed;   // bits per second
        public ulong InOctets;      // total bytes received
        public ulong OutOctets;     // total bytes sent
    }

    internal static class InterfaceTable
    {
        // MIB_IF_TABLE2 / MIB_IF_ROW2 layout (identical on x86 and x64).
        // sizeof(MIB_IF_ROW2) = 1352; the row array starts at offset 8 (8-byte aligned).
        private const int TableHeader = 8;
        private const int RowSize = 1352;
        private const int OffLuid = 0;
        private const int OffAlias = 28;            // WCHAR[257]
        private const int OffDescription = 542;     // WCHAR[257]
        private const int OffType = 1128;           // IFTYPE
        private const int OffFlags = 1152;          // InterfaceAndOperStatusFlags (bit 0 Hardware, bit 1 Filter)
        private const int OffOperStatus = 1156;     // IF_OPER_STATUS (1 = up)
        private const int OffTxLinkSpeed = 1192;
        private const int OffRxLinkSpeed = 1200;
        private const int OffInOctets = 1208;
        private const int OffOutOctets = 1280;

        public const uint IfTypeLoopback = 24;

        public static List<AdapterSample> Read()
        {
            IntPtr table;
            int err = NativeMethods.GetIfTable2(out table);
            if (err != 0) throw new Win32Exception(err);

            List<AdapterSample> list = new List<AdapterSample>();
            try
            {
                long count = (uint)Marshal.ReadInt32(table);
                long baseAddr = table.ToInt64() + TableHeader;
                for (long i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(baseAddr + i * RowSize);
                    byte flags = Marshal.ReadByte(row, OffFlags);

                    AdapterSample a = new AdapterSample();
                    a.Luid = (ulong)Marshal.ReadInt64(row, OffLuid);
                    a.Alias = Marshal.PtrToStringUni(new IntPtr(row.ToInt64() + OffAlias)) ?? "";
                    a.Description = Marshal.PtrToStringUni(new IntPtr(row.ToInt64() + OffDescription)) ?? "";
                    a.IfType = (uint)Marshal.ReadInt32(row, OffType);
                    a.Hardware = (flags & 0x01) != 0;
                    a.Filter = (flags & 0x02) != 0;
                    a.Up = Marshal.ReadInt32(row, OffOperStatus) == 1;
                    a.TxLinkSpeed = (ulong)Marshal.ReadInt64(row, OffTxLinkSpeed);
                    a.RxLinkSpeed = (ulong)Marshal.ReadInt64(row, OffRxLinkSpeed);
                    a.InOctets = (ulong)Marshal.ReadInt64(row, OffInOctets);
                    a.OutOctets = (ulong)Marshal.ReadInt64(row, OffOutOctets);
                    list.Add(a);
                }
            }
            finally
            {
                NativeMethods.FreeMibTable(table);
            }
            return list;
        }
    }

    internal sealed class SpeedMeter
    {
        private Dictionary<ulong, ulong[]> previous = new Dictionary<ulong, ulong[]>();
        private long previousTimestamp;
        private bool hasPrevious;

        public double DownBytesPerSec;
        public double UpBytesPerSec;
        public ulong TotalDown;
        public ulong TotalUp;
        public DateTime Since = DateTime.Now;
        public List<AdapterSample> Counted = new List<AdapterSample>();

        // selectedLuid == 0 means Auto (all connected physical adapters).
        public static bool Include(AdapterSample a, ulong selectedLuid)
        {
            if (a.Filter || a.IfType == InterfaceTable.IfTypeLoopback) return false;
            if (selectedLuid != 0) return a.Luid == selectedLuid;
            return a.Hardware && a.Up;
        }

        public void Sample(ulong selectedLuid)
        {
            List<AdapterSample> all = InterfaceTable.Read();
            long now = Stopwatch.GetTimestamp();

            Dictionary<ulong, ulong[]> current = new Dictionary<ulong, ulong[]>();
            List<AdapterSample> counted = new List<AdapterSample>();
            foreach (AdapterSample a in all)
            {
                if (!Include(a, selectedLuid) || current.ContainsKey(a.Luid)) continue;
                current[a.Luid] = new ulong[] { a.InOctets, a.OutOctets };
                counted.Add(a);
            }

            if (hasPrevious)
            {
                ulong dIn = 0, dOut = 0;
                foreach (KeyValuePair<ulong, ulong[]> kv in current)
                {
                    ulong[] p;
                    // Only adapters present in both reads are counted; a counter that went
                    // backwards (adapter reset / reconnected) contributes nothing this round.
                    if (!previous.TryGetValue(kv.Key, out p)) continue;
                    if (kv.Value[0] >= p[0]) dIn += kv.Value[0] - p[0];
                    if (kv.Value[1] >= p[1]) dOut += kv.Value[1] - p[1];
                }
                double seconds = (now - previousTimestamp) / (double)Stopwatch.Frequency;
                if (seconds > 0.01)
                {
                    DownBytesPerSec = dIn / seconds;
                    UpBytesPerSec = dOut / seconds;
                }
                TotalDown += dIn;
                TotalUp += dOut;
            }

            previous = current;
            previousTimestamp = now;
            hasPrevious = true;
            Counted = counted;
        }

        public void Reset()
        {
            previous = new Dictionary<ulong, ulong[]>();
            hasPrevious = false;
            DownBytesPerSec = 0;
            UpBytesPerSec = 0;
            TotalDown = 0;
            TotalUp = 0;
            Since = DateTime.Now;
        }
    }

    // ------------------------------------------------------------------ Formatting

    internal static class Fmt
    {
        private static readonly string[] ByteRateUnits = { "B/s", "KB/s", "MB/s", "GB/s" };
        private static readonly string[] BitRateUnits = { "bps", "Kbps", "Mbps", "Gbps" };
        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

        // Bytes mode uses 1024 steps (KB/s, MB/s like download managers);
        // bits mode uses 1000 steps (Kbps, Mbps like Task Manager and ISPs).
        public static string Rate(double bytesPerSec, bool bits)
        {
            double v = bits ? bytesPerSec * 8.0 : bytesPerSec;
            double step = bits ? 1000.0 : 1024.0;
            string[] units = bits ? BitRateUnits : ByteRateUnits;
            int i = 0;
            while (v >= 999.5 && i < units.Length - 1) { v /= step; i++; }
            return Number(v, i == 0) + " " + units[i];
        }

        public static string Size(ulong bytes)
        {
            double v = bytes;
            int i = 0;
            while (v >= 1024.0 && i < SizeUnits.Length - 1) { v /= 1024.0; i++; }
            return (i == 0 ? bytes.ToString(CultureInfo.CurrentCulture) : v.ToString("0.00", CultureInfo.CurrentCulture)) + " " + SizeUnits[i];
        }

        public static string LinkSpeed(ulong bitsPerSec)
        {
            double v = bitsPerSec;
            int i = 0;
            while (v >= 1000.0 && i < BitRateUnits.Length - 1) { v /= 1000.0; i++; }
            return v.ToString("0.#", CultureInfo.CurrentCulture) + " " + BitRateUnits[i];
        }

        private static string Number(double v, bool whole)
        {
            CultureInfo c = CultureInfo.CurrentCulture;
            if (whole) return Math.Round(v).ToString("0", c);
            if (v < 9.995) return v.ToString("0.00", c);
            if (v < 99.95) return v.ToString("0.0", c);
            return v.ToString("0", c);
        }
    }

    // ------------------------------------------------------------------ Settings

    internal sealed class Settings
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public ulong AdapterLuid;          // 0 = Auto
        public bool Bits;
        public int IntervalMs = 1000;
        public bool TwoLines = true;
        public bool DarkTheme = true;
        public float FontSize = 9f;
        public int OpacityPercent = 100;
        public bool AlwaysOnTop = true;
        public bool HideInFullscreen = true;
        public bool LockPosition;
        public bool WidgetVisible = true;

        private static string FilePath
        {
            get
            {
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NetSpeedWidget"), "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "X": s.X = ParseInt(v, s.X); break;
                            case "Y": s.Y = ParseInt(v, s.Y); break;
                            case "Adapter": s.AdapterLuid = ParseULong(v, 0); break;
                            case "Bits": s.Bits = v == "1"; break;
                            case "IntervalMs": s.IntervalMs = ParseInt(v, s.IntervalMs); break;
                            case "TwoLines": s.TwoLines = v == "1"; break;
                            case "DarkTheme": s.DarkTheme = v == "1"; break;
                            case "FontSize": s.FontSize = ParseFloat(v, s.FontSize); break;
                            case "Opacity": s.OpacityPercent = ParseInt(v, s.OpacityPercent); break;
                            case "AlwaysOnTop": s.AlwaysOnTop = v == "1"; break;
                            case "HideInFullscreen": s.HideInFullscreen = v == "1"; break;
                            case "LockPosition": s.LockPosition = v == "1"; break;
                            case "WidgetVisible": s.WidgetVisible = v == "1"; break;
                        }
                    }
                }
            }
            catch (Exception) { }

            if (s.IntervalMs != 500 && s.IntervalMs != 1000 && s.IntervalMs != 2000) s.IntervalMs = 1000;
            if (s.OpacityPercent < 30 || s.OpacityPercent > 100) s.OpacityPercent = 100;
            if (s.FontSize < 7f || s.FontSize > 16f) s.FontSize = 9f;
            return s;
        }

        public void Save()
        {
            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("X=" + X.ToString(c));
                sb.AppendLine("Y=" + Y.ToString(c));
                sb.AppendLine("Adapter=" + AdapterLuid.ToString(c));
                sb.AppendLine("Bits=" + B(Bits));
                sb.AppendLine("IntervalMs=" + IntervalMs.ToString(c));
                sb.AppendLine("TwoLines=" + B(TwoLines));
                sb.AppendLine("DarkTheme=" + B(DarkTheme));
                sb.AppendLine("FontSize=" + FontSize.ToString(c));
                sb.AppendLine("Opacity=" + OpacityPercent.ToString(c));
                sb.AppendLine("AlwaysOnTop=" + B(AlwaysOnTop));
                sb.AppendLine("HideInFullscreen=" + B(HideInFullscreen));
                sb.AppendLine("LockPosition=" + B(LockPosition));
                sb.AppendLine("WidgetVisible=" + B(WidgetVisible));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception) { }
        }

        private static string B(bool b) { return b ? "1" : "0"; }

        private static int ParseInt(string v, int fallback)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : fallback;
        }

        private static ulong ParseULong(string v, ulong fallback)
        {
            ulong r;
            return ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : fallback;
        }

        private static float ParseFloat(string v, float fallback)
        {
            float r;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : fallback;
        }
    }

    // ------------------------------------------------------------------ The widget

    internal sealed class WidgetForm : Form
    {
        public const string AppName = "NetSpeed Widget";
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "NetSpeedWidget";
        private const string DownArrow = "\u2193";
        private const string UpArrow = "\u2191";

        private readonly Settings settings;
        private readonly SpeedMeter meter = new SpeedMeter();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly float dpiScale = 1f;

        private Font textFont;
        private Icon trayIcon;
        private IntPtr trayIconHandle = IntPtr.Zero;
        private bool menuOpen;
        private string lastError;

        // Layout, recomputed whenever font / units / layout change.
        private int padX, padY, gap, lineH, arrowW, valueW, columnGap;

        public WidgetForm()
        {
            settings = Settings.Load();

            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch (Exception) { }

            Text = AppName;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            menu.Opening += Menu_Opening;
            menu.Opened += delegate { menuOpen = true; };
            menu.Closed += delegate { menuOpen = false; };
            ContextMenuStrip = menu;

            CreateTrayIcon();
            Icon = trayIcon;
            tray.Text = AppName;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += Tray_MouseClick;
            tray.Visible = true;

            ApplyAppearance();
            PlaceInitially();
            BuildMenu();

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            SampleNow();
            timer.Interval = settings.IntervalMs;
            timer.Tick += Timer_Tick;
            timer.Start();
        }

        // ---- window behaviour

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: not in Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE: never steals focus
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int small = 3;  // DWMWCP_ROUNDSMALL - rounded corners on Windows 11, ignored on 10
                NativeMethods.DwmSetWindowAttribute(Handle, 33, ref small, sizeof(int));
            }
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateVisibility();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !settings.LockPosition)
            {
                // Let Windows drag the window as if its title bar was grabbed.
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
                SavePosition();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePosition();
            timer.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                timer.Dispose();
                tray.Dispose();
                menu.Dispose();
                if (textFont != null) textFont.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
            }
            if (trayIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(trayIconHandle);
                trayIconHandle = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }

        private void KeepOnTop()
        {
            // The Windows 11 taskbar raises itself above other top-most windows when clicked,
            // so re-assert our place every tick (no move, no resize, no activation).
            NativeMethods.SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        // ---- sampling

        private void Timer_Tick(object sender, EventArgs e)
        {
            SampleNow();
            UpdateVisibility();
            if (Visible)
            {
                Invalidate();
                if (settings.AlwaysOnTop && !menuOpen) KeepOnTop();
            }
        }

        private void SampleNow()
        {
            try
            {
                meter.Sample(settings.AdapterLuid);
                lastError = null;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            UpdateTrayText();
        }

        private void UpdateVisibility()
        {
            bool fullscreen = false;
            if (settings.HideInFullscreen)
            {
                try
                {
                    int state;
                    // 2 = busy (full-screen app), 3 = full-screen Direct3D game, 4 = presentation mode
                    if (NativeMethods.SHQueryUserNotificationState(out state) == 0)
                        fullscreen = state == 2 || state == 3 || state == 4;
                }
                catch (Exception) { }
            }

            bool show = settings.WidgetVisible && !fullscreen;
            if (show && !Visible) Show();
            else if (!show && Visible) Hide();
        }

        // ---- drawing

        private Color Background { get { return settings.DarkTheme ? Color.FromArgb(28, 28, 30) : Color.FromArgb(243, 243, 243); } }
        private Color Foreground { get { return settings.DarkTheme ? Color.FromArgb(240, 240, 240) : Color.FromArgb(25, 25, 25); } }
        private Color DownColor { get { return settings.DarkTheme ? Color.FromArgb(80, 210, 120) : Color.FromArgb(0, 135, 60); } }
        private Color UpColor { get { return settings.DarkTheme ? Color.FromArgb(255, 165, 70) : Color.FromArgb(200, 90, 0); } }

        private int Px(float v) { return (int)Math.Round(v * dpiScale); }

        private void ApplyAppearance()
        {
            Font old = textFont;
            textFont = new Font("Segoe UI", settings.FontSize, FontStyle.Regular, GraphicsUnit.Point);
            if (old != null) old.Dispose();

            TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size arrow = TextRenderer.MeasureText(DownArrow, textFont, Size.Empty, f);
            // Widest value ever printed: "888.8" plus the widest unit (digits are equal width in Segoe UI).
            string widest = "888" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "8 " +
                            (settings.Bits ? "Mbps" : "MB/s");
            Size value = TextRenderer.MeasureText(widest, textFont, Size.Empty, f);

            padX = Px(6);
            padY = Px(2);
            gap = Px(4);
            columnGap = Px(10);
            arrowW = arrow.Width;
            valueW = value.Width + Px(2);
            lineH = Math.Max(arrow.Height, value.Height);

            int itemW = arrowW + gap + valueW;
            ClientSize = settings.TwoLines
                ? new Size(padX * 2 + itemW, padY * 2 + lineH * 2)
                : new Size(padX * 2 + itemW * 2 + columnGap, padY * 2 + lineH);

            BackColor = Background;
            Opacity = settings.OpacityPercent / 100.0;
            TopMost = settings.AlwaysOnTop;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Background;
            g.Clear(bg);

            string down = lastError != null ? "--" : Fmt.Rate(meter.DownBytesPerSec, settings.Bits);
            string up = lastError != null ? "--" : Fmt.Rate(meter.UpBytesPerSec, settings.Bits);

            if (settings.TwoLines)
            {
                DrawItem(g, DownArrow, down, DownColor, bg, padX, padY);
                DrawItem(g, UpArrow, up, UpColor, bg, padX, padY + lineH);
            }
            else
            {
                DrawItem(g, DownArrow, down, DownColor, bg, padX, padY);
                DrawItem(g, UpArrow, up, UpColor, bg, padX + arrowW + gap + valueW + columnGap, padY);
            }
        }

        private void DrawItem(Graphics g, string arrow, string value, Color arrowColor, Color bg, int x, int y)
        {
            TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, arrow, textFont, new Rectangle(x, y, arrowW, lineH), arrowColor, bg, f | TextFormatFlags.Left);
            TextRenderer.DrawText(g, value, textFont, new Rectangle(x + arrowW + gap, y, valueW, lineH), Foreground, bg, f | TextFormatFlags.Right);
        }

        // ---- placement

        private void PlaceInitially()
        {
            Rectangle saved = new Rectangle(settings.X, settings.Y, Width, Height);
            Location = settings.X != int.MinValue && IsOnScreen(saved) ? saved.Location : DefaultLocation();
        }

        private static bool IsOnScreen(Rectangle r)
        {
            Point centre = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            foreach (Screen s in Screen.AllScreens)
                if (s.Bounds.Contains(centre)) return true;
            return false;
        }

        // On the taskbar just left of the clock / tray icons when possible,
        // otherwise just above the bottom-right corner of the taskbar.
        private Point DefaultLocation()
        {
            Screen screen = Screen.PrimaryScreen;
            try
            {
                IntPtr bar = NativeMethods.FindWindow("Shell_TrayWnd", null);
                IntPtr notify = bar == IntPtr.Zero ? IntPtr.Zero : NativeMethods.FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
                NativeMethods.RECT r;
                if (notify != IntPtr.Zero && NativeMethods.GetWindowRect(notify, out r))
                {
                    Rectangle trayRect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    bool onBottomTaskbar = screen.Bounds.Contains(trayRect) && trayRect.Top >= screen.WorkingArea.Bottom
                                           && trayRect.Width > 40 && trayRect.Height >= Height;
                    if (onBottomTaskbar)
                        return new Point(trayRect.Left - Width - Px(8), trayRect.Top + (trayRect.Height - Height) / 2);
                }
            }
            catch (Exception) { }

            Rectangle wa = screen.WorkingArea;
            return new Point(wa.Right - Width - Px(8), wa.Bottom - Height - Px(8));
        }

        private void SavePosition()
        {
            settings.X = Left;
            settings.Y = Top;
            settings.Save();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!IsOnScreen(Bounds)) { Location = DefaultLocation(); SavePosition(); }
        }

        // ---- tray icon

        private void CreateTrayIcon()
        {
            Size sz = SystemInformation.SmallIconSize;
            using (Bitmap bmp = new Bitmap(sz.Width, sz.Height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float w = sz.Width / 2f, h = sz.Height;
                using (SolidBrush down = new SolidBrush(Color.FromArgb(46, 204, 113)))
                using (SolidBrush up = new SolidBrush(Color.FromArgb(255, 152, 0)))
                {
                    g.FillPolygon(down, ArrowShape(0, 0, w, h, true));
                    g.FillPolygon(up, ArrowShape(w, 0, w, h, false));
                }
                trayIconHandle = bmp.GetHicon();
                trayIcon = Icon.FromHandle(trayIconHandle);
            }
            tray.Icon = trayIcon;
        }

        private static PointF[] ArrowShape(float x, float y, float w, float h, bool pointingDown)
        {
            float shaftL = x + w * 0.32f, shaftR = x + w * 0.68f;
            PointF[] p =
            {
                new PointF(shaftL, y + h * 0.06f), new PointF(shaftR, y + h * 0.06f),
                new PointF(shaftR, y + h * 0.50f), new PointF(x + w * 0.96f, y + h * 0.50f),
                new PointF(x + w * 0.50f, y + h * 0.94f), new PointF(x + w * 0.04f, y + h * 0.50f),
                new PointF(shaftL, y + h * 0.50f)
            };
            if (!pointingDown)
                for (int i = 0; i < p.Length; i++) p[i] = new PointF(p[i].X, y + h - (p[i].Y - y));
            return p;
        }

        private void Tray_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            settings.WidgetVisible = !settings.WidgetVisible;
            settings.Save();
            UpdateVisibility();
        }

        // ---- menu

        private void Menu_Opening(object sender, CancelEventArgs e)
        {
            BuildMenu();
            e.Cancel = false;
        }

        private static ToolStripMenuItem Item(string text, bool isChecked, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Checked = isChecked;
            mi.Click += onClick;
            return mi;
        }

        private void BuildMenu()
        {
            List<ToolStripItem> old = new List<ToolStripItem>();
            foreach (ToolStripItem it in menu.Items) old.Add(it);
            menu.Items.Clear();
            foreach (ToolStripItem it in old) it.Dispose();

            // Network adapter
            ToolStripMenuItem adapters = new ToolStripMenuItem("Network adapter");
            adapters.DropDownItems.Add(Item("Auto - all connected physical adapters", settings.AdapterLuid == 0,
                delegate { SetAdapter(0); }));
            adapters.DropDownItems.Add(new ToolStripSeparator());
            List<AdapterSample> all;
            try { all = InterfaceTable.Read(); } catch (Exception) { all = new List<AdapterSample>(); }
            HashSet<ulong> seen = new HashSet<ulong>();
            foreach (AdapterSample a in all)
            {
                if (a.Filter || a.IfType == InterfaceTable.IfTypeLoopback || !seen.Add(a.Luid)) continue;
                if (!a.Up && a.Luid != settings.AdapterLuid) continue;
                ulong luid = a.Luid;
                string label = a.Alias + "  (" + a.Description + ")";
                if (!a.Hardware) label += "  [virtual]";
                if (!a.Up) label += "  [disconnected]";
                adapters.DropDownItems.Add(Item(label, settings.AdapterLuid == luid, delegate { SetAdapter(luid); }));
            }
            menu.Items.Add(adapters);

            // Units
            ToolStripMenuItem units = new ToolStripMenuItem("Units");
            units.DropDownItems.Add(Item("Bytes  (KB/s, MB/s)", !settings.Bits, delegate { settings.Bits = false; Changed(true); }));
            units.DropDownItems.Add(Item("Bits  (Kbps, Mbps)", settings.Bits, delegate { settings.Bits = true; Changed(true); }));
            menu.Items.Add(units);

            // Update interval
            ToolStripMenuItem interval = new ToolStripMenuItem("Update every");
            foreach (int ms in new int[] { 500, 1000, 2000 })
            {
                int value = ms;
                string label = (ms / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " s";
                interval.DropDownItems.Add(Item(label, settings.IntervalMs == value, delegate
                {
                    settings.IntervalMs = value;
                    timer.Interval = value;
                    Changed(false);
                }));
            }
            menu.Items.Add(interval);

            // Appearance
            ToolStripMenuItem look = new ToolStripMenuItem("Appearance");
            look.DropDownItems.Add(Item("Two lines", settings.TwoLines, delegate { settings.TwoLines = true; Changed(true); }));
            look.DropDownItems.Add(Item("One line", !settings.TwoLines, delegate { settings.TwoLines = false; Changed(true); }));
            look.DropDownItems.Add(new ToolStripSeparator());
            look.DropDownItems.Add(Item("Dark", settings.DarkTheme, delegate { settings.DarkTheme = true; Changed(true); }));
            look.DropDownItems.Add(Item("Light", !settings.DarkTheme, delegate { settings.DarkTheme = false; Changed(true); }));
            look.DropDownItems.Add(new ToolStripSeparator());
            string[] sizeNames = { "Small text", "Normal text", "Large text", "Extra large text" };
            float[] sizes = { 8f, 9f, 10.5f, 12f };
            for (int i = 0; i < sizes.Length; i++)
            {
                float size = sizes[i];
                look.DropDownItems.Add(Item(sizeNames[i], Math.Abs(settings.FontSize - size) < 0.01f,
                    delegate { settings.FontSize = size; Changed(true); }));
            }
            look.DropDownItems.Add(new ToolStripSeparator());
            foreach (int pct in new int[] { 100, 90, 80, 70, 60 })
            {
                int value = pct;
                look.DropDownItems.Add(Item("Opacity " + value.ToString(CultureInfo.CurrentCulture) + "%",
                    settings.OpacityPercent == value,
                    delegate { settings.OpacityPercent = value; Changed(true); }));
            }
            menu.Items.Add(look);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Always on top", settings.AlwaysOnTop,
                delegate { settings.AlwaysOnTop = !settings.AlwaysOnTop; Changed(true); }));
            menu.Items.Add(Item("Hide while a full-screen app is running", settings.HideInFullscreen,
                delegate { settings.HideInFullscreen = !settings.HideInFullscreen; Changed(false); UpdateVisibility(); }));
            menu.Items.Add(Item("Lock position", settings.LockPosition,
                delegate { settings.LockPosition = !settings.LockPosition; Changed(false); }));
            menu.Items.Add(Item("Start with Windows", IsStartupEnabled(), delegate { ToggleStartup(); }));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Show widget", settings.WidgetVisible, delegate
            {
                settings.WidgetVisible = !settings.WidgetVisible;
                Changed(false);
                UpdateVisibility();
            }));
            menu.Items.Add(Item("Move to default position", false, delegate { Location = DefaultLocation(); SavePosition(); }));
            menu.Items.Add(Item("Details and totals...", false, delegate { ShowDetails(); }));
            menu.Items.Add(Item("Reset totals", false, delegate { meter.Reset(); SampleNow(); Invalidate(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, delegate { Close(); }));
        }

        // Settings changed from the menu. Never resets the meter, so the next reading stays exact.
        private void Changed(bool relayout)
        {
            settings.Save();
            if (relayout)
            {
                int right = Right, top = Top;
                ApplyAppearance();
                // Keep the right edge in place so the widget grows to the left (away from the clock).
                Location = new Point(right - Width, top);
                SavePosition();
            }
            UpdateTrayText();
            Invalidate();
        }

        private void UpdateTrayText()
        {
            string text = lastError != null
                ? AppName + " - error reading counters"
                : DownArrow + " " + Fmt.Rate(meter.DownBytesPerSec, settings.Bits) + "    " +
                  UpArrow + " " + Fmt.Rate(meter.UpBytesPerSec, settings.Bits);
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void SetAdapter(ulong luid)
        {
            settings.AdapterLuid = luid;
            settings.Save();
            meter.Reset();
            SampleNow();   // primes the new baseline; the first real reading arrives on the next tick
            Invalidate();
        }

        // ---- start with Windows (per-user Run key, no admin rights needed)

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string v = key.GetValue(RunValueName) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        private static void ToggleStartup()
        {
            try
            {
                bool enable = !IsStartupEnabled();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the startup setting:\n" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- details

        private void ShowDetails()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Measuring: " + (settings.AdapterLuid == 0
                ? "Auto (all connected physical adapters)"
                : "the adapter you selected"));
            sb.AppendLine();
            sb.AppendLine("Adapters being counted:");
            if (meter.Counted.Count == 0) sb.AppendLine("   (none - no connected adapter found)");
            foreach (AdapterSample a in meter.Counted)
            {
                string line = "   - " + a.Alias + ": " + a.Description;
                if (a.RxLinkSpeed > 0 && a.RxLinkSpeed != ulong.MaxValue)
                    line += "  (link " + Fmt.LinkSpeed(a.RxLinkSpeed) + ")";
                sb.AppendLine(line);
            }
            sb.AppendLine();
            sb.AppendLine("Totals since " + meter.Since.ToString("g", CultureInfo.CurrentCulture) + ":");
            sb.AppendLine("   Downloaded: " + Fmt.Size(meter.TotalDown));
            sb.AppendLine("   Uploaded:   " + Fmt.Size(meter.TotalUp));
            sb.AppendLine();
            sb.AppendLine("Speeds are read from Windows' own 64-bit adapter byte counters and timed with the " +
                          "high-resolution clock - the same counters Task Manager uses. They include packet " +
                          "headers, so they run a few percent above speed-test sites, which count only payload.");
            if (lastError != null)
            {
                sb.AppendLine();
                sb.AppendLine("Last error: " + lastError);
            }
            MessageBox.Show(sb.ToString(), AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
