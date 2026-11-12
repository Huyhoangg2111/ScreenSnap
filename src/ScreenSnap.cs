// ScreenSnap - Phần mềm chụp màn hình chọn vùng cho Windows
// Biên dịch bằng build.ps1 (dùng csc.exe có sẵn của .NET Framework 4.x)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ScreenSnap")]
[assembly: AssemblyDescription("Chụp màn hình chọn vùng, vẽ & chú thích")]
[assembly: AssemblyProduct("ScreenSnap")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ScreenSnap
{
    // ======================================================================
    // Win32
    // ======================================================================
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);
    }

    // ======================================================================
    // Program
    // ======================================================================
    internal static class Program
    {
        public static bool FromStartup;
        public static bool Relaunched;
        public static bool CaptureNow;
        public const string CaptureEventName = "Local\\ScreenSnap_CaptureEvent_v1";
        static Mutex mutex;
        static bool owned;

        [STAThread]
        static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--make-icon" && i + 1 < args.Length) { Util.WriteIco(args[i + 1]); return; }
                if (args[i] == "--startup") FromStartup = true;
                if (args[i] == "--relaunch") Relaunched = true;
                if (args[i] == "--capture") CaptureNow = true;
            }

            EnableDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Lỗi: " + e.Exception.Message, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Error);

            try
            {
                mutex = new Mutex(false, "Local\\ScreenSnap_SingleInstance_v1");
                try { owned = mutex.WaitOne(Relaunched ? 10000 : 0, false); }
                catch (AbandonedMutexException) { owned = true; }
            }
            catch (UnauthorizedAccessException) { owned = false; }

            if (!owned)
            {
                if (CaptureNow)
                {
                    // Báo cho bản đang chạy mở màn hình chụp
                    try { Native.AllowSetForegroundWindow(-1); } catch { }
                    try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting(CaptureEventName)) ev.Set(); } catch { }
                    return;
                }
                if (!FromStartup)
                    MessageBox.Show("ScreenSnap đang chạy rồi.\nBiểu tượng nằm ở khay hệ thống (góc phải thanh Taskbar).",
                        "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Settings cfg = Settings.Load();
                if (cfg.RunAsAdmin && !Util.IsAdmin() && !Relaunched)
                {
                    if (Util.RelaunchElevated(FromStartup ? "--startup" : null)) return;
                }
                Application.Run(new TrayApp(cfg));
            }
            finally { Release(); }
        }

        public static void Release()
        {
            if (owned) { try { mutex.ReleaseMutex(); } catch { } owned = false; }
        }

        static void EnableDpi()
        {
            try { if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { if (Native.SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { Native.SetProcessDPIAware(); } catch { }
        }
    }

    // ======================================================================
    // Cài đặt (lưu tại %APPDATA%\ScreenSnap\settings.ini)
    // ======================================================================
    internal class Settings
    {
        public Keys HotkeyRegion = Keys.PrintScreen;
        public Keys HotkeyFull = Keys.Control | Keys.PrintScreen;
        public string SaveFolder = DefaultFolder();
        public string Format = "png";
        public bool AutoSaveOnCopy = false;
        public bool RunAtStartup = false;
        public bool RunAsAdmin = false;
        public bool Notify = true;
        public bool Magnifier = true;
        public int ColorArgb = Color.FromArgb(255, 59, 48).ToArgb();
        public int SizeLevel = 2;
        public bool FirstRun;

        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenSnap");
        }
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenSnap"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.FirstRun = true; return s; }
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "HotkeyRegion": s.HotkeyRegion = (Keys)Int(v, (int)s.HotkeyRegion); break;
                        case "HotkeyFull": s.HotkeyFull = (Keys)Int(v, (int)s.HotkeyFull); break;
                        case "SaveFolder": if (v.Length > 0) s.SaveFolder = v; break;
                        case "Format": s.Format = (v == "jpg") ? "jpg" : "png"; break;
                        case "AutoSaveOnCopy": s.AutoSaveOnCopy = v == "1"; break;
                        case "RunAtStartup": s.RunAtStartup = v == "1"; break;
                        case "RunAsAdmin": s.RunAsAdmin = v == "1"; break;
                        case "Notify": s.Notify = v == "1"; break;
                        case "Magnifier": s.Magnifier = v == "1"; break;
                        case "Color": s.ColorArgb = Int(v, s.ColorArgb); break;
                        case "SizeLevel": s.SizeLevel = Math.Max(1, Math.Min(3, Int(v, 2))); break;
                    }
                }
            }
            catch { }
            return s;
        }

        static int Int(string v, int def) { int r; return int.TryParse(v, out r) ? r : def; }
        static string B(bool b) { return b ? "1" : "0"; }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("HotkeyRegion=" + (int)HotkeyRegion);
                sb.AppendLine("HotkeyFull=" + (int)HotkeyFull);
                sb.AppendLine("SaveFolder=" + SaveFolder);
                sb.AppendLine("Format=" + Format);
                sb.AppendLine("AutoSaveOnCopy=" + B(AutoSaveOnCopy));
                sb.AppendLine("RunAtStartup=" + B(RunAtStartup));
                sb.AppendLine("RunAsAdmin=" + B(RunAsAdmin));
                sb.AppendLine("Notify=" + B(Notify));
                sb.AppendLine("Magnifier=" + B(Magnifier));
                sb.AppendLine("Color=" + ColorArgb);
                sb.AppendLine("SizeLevel=" + SizeLevel);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                FirstRun = false;
            }
            catch { }
        }
    }

    // ======================================================================
    // Tiện ích
    // ======================================================================
    internal static class Util
    {
        const string TaskName = "ScreenSnap_AutoStart";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ScreenSnap";

        public static string ExePath { get { return Application.ExecutablePath; } }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static bool RelaunchElevated(string extraArgs)
        {
            try
            {
                string a = "--relaunch" + (string.IsNullOrEmpty(extraArgs) ? "" : " " + extraArgs);
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, a);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        public static int RunHidden(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // ---- Khởi động cùng Windows ----
        // Quyền thường: dùng khóa Run trong registry (HKCU).
        // Quyền Admin: dùng Task Scheduler với "Run with highest privileges" để không bị hỏi UAC mỗi lần bật máy.
        public static string ApplyStartup(Settings s)
        {
            bool admin = IsAdmin();
            try
            {
                if (s.RunAtStartup && s.RunAsAdmin && admin)
                {
                    if (CreateTask()) SetRunKey(false);
                    else { SetRunKey(true); return "Không tạo được tác vụ khởi động quyền Admin, đã dùng chế độ khởi động thường."; }
                }
                else if (s.RunAtStartup)
                {
                    SetRunKey(true);
                    if (admin) DeleteTask();
                }
                else
                {
                    SetRunKey(false);
                    if (admin) DeleteTask();
                }
            }
            catch (Exception ex) { return ex.Message; }
            return null;
        }

        static void SetRunKey(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) return;
                if (on) k.SetValue(RunValue, "\"" + ExePath + "\" --startup");
                else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, false);
            }
        }

        static void DeleteTask()
        {
            if (RunHidden("schtasks.exe", "/Query /TN " + TaskName) == 0)
                RunHidden("schtasks.exe", "/Delete /TN " + TaskName + " /F");
        }

        static bool CreateTask()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>ScreenSnap - tự khởi động với quyền Administrator</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
"<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
"<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
"<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
"<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(ExePath) + "</Command><Arguments>--startup</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "ScreenSnap_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunHidden("schtasks.exe", "/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            return code == 0;
        }

        // ---- Ảnh ----
        public static Bitmap CaptureScreen(Rectangle r, PixelFormat pf)
        {
            Bitmap raw = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(raw))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = Native.GetDC(IntPtr.Zero);
                try { Native.BitBlt(dst, 0, 0, r.Width, r.Height, src, r.Left, r.Top, 0x00CC0020 | 0x40000000); } // SRCCOPY | CAPTUREBLT
                finally { Native.ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
            }
            if (pf == PixelFormat.Format24bppRgb) return raw;
            Bitmap c = raw.Clone(new Rectangle(0, 0, r.Width, r.Height), pf);
            raw.Dispose();
            return c;
        }

        public static string SaveToFolder(Bitmap bmp, Settings s)
        {
            string dir = string.IsNullOrEmpty(s.SaveFolder) ? Settings.DefaultFolder() : s.SaveFolder;
            Directory.CreateDirectory(dir);
            string ext = s.Format == "jpg" ? ".jpg" : ".png";
            string name = "ScreenSnap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, name + ext);
            int n = 2;
            while (File.Exists(path)) { path = Path.Combine(dir, name + "_" + n + ext); n++; }
            SaveImage(bmp, path);
            return path;
        }

        public static void SaveImage(Bitmap bmp, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
            {
                ImageCodecInfo codec = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                    if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                    bmp.Save(path, codec, ep);
                }
            }
            else if (ext == ".bmp") bmp.Save(path, ImageFormat.Bmp);
            else bmp.Save(path, ImageFormat.Png);
        }

        public static void CopyImage(Bitmap bmp)
        {
            DataObject d = new DataObject();
            d.SetData(DataFormats.Bitmap, true, bmp);
            MemoryStream ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            d.SetData("PNG", false, ms);
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetDataObject(d, true); return; }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            throw new Exception("Clipboard đang bị phần mềm khác chiếm, hãy thử lại.");
        }

        // ---- Phím tắt ----
        public static string HotkeyToString(Keys k)
        {
            if (k == Keys.None) return "(không dùng)";
            List<string> parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(KeyName(k & Keys.KeyCode));
            return string.Join(" + ", parts.ToArray());
        }

        public static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + ((int)key - (int)Keys.NumPad0);
            switch (key)
            {
                case Keys.PrintScreen: return "PrintScreen";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Return: return "Enter";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                default: return key.ToString();
            }
        }

        // ---- Đồ họa ----
        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, float rad, Color c)
        {
            using (GraphicsPath p = RoundRect(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawArrow(Graphics g, PointF a, PointF b, Color c, float w)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float ux = dx / len, uy = dy / len, px = -uy, py = ux;
            float head = Math.Max(w * 3.6f + 6f, 14f);
            if (head > len) head = len;
            float hw = head * 0.5f;
            float sw = w * 0.5f;
            float tail = Math.Max(0.6f, w * 0.15f);
            PointF bc = new PointF(b.X - ux * head, b.Y - uy * head);
            PointF j = new PointF(b.X - ux * head * 0.78f, b.Y - uy * head * 0.78f);
            PointF[] pts = {
                new PointF(a.X + px * tail, a.Y + py * tail),
                new PointF(j.X + px * sw, j.Y + py * sw),
                new PointF(bc.X + px * hw, bc.Y + py * hw),
                b,
                new PointF(bc.X - px * hw, bc.Y - py * hw),
                new PointF(j.X - px * sw, j.Y - py * sw),
                new PointF(a.X - px * tail, a.Y - py * tail)
            };
            using (SolidBrush br = new SolidBrush(c)) g.FillPolygon(br, pts);
        }

        public static Bitmap DrawLogo(int size)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                RectangleF rc = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
                using (GraphicsPath p = RoundRect(rc, size * 0.22f))
                using (LinearGradientBrush br = new LinearGradientBrush(rc, Color.FromArgb(20, 170, 255), Color.FromArgb(0, 90, 220), 45f))
                    g.FillPath(br, p);
                float m = size * 0.2f, L = size * 0.24f, w = Math.Max(1.6f, size * 0.09f), e = size - m;
                using (Pen pen = new Pen(Color.White, w))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new PointF[] { new PointF(m, m + L), new PointF(m, m), new PointF(m + L, m) });
                    g.DrawLines(pen, new PointF[] { new PointF(e, e - L), new PointF(e, e), new PointF(e - L, e) });
                }
                float cr = size * 0.13f;
                g.FillEllipse(Brushes.White, size / 2f - cr, size / 2f - cr, cr * 2, cr * 2);
            }
            return b;
        }

        public static Icon MakeIcon(int size)
        {
            using (Bitmap b = DrawLogo(size)) return Icon.FromHandle(b.GetHicon());
        }

        // Ghi file .ico nhiều kích thước (PNG nhúng) - dùng khi build để gắn icon cho exe
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in sizes)
                using (Bitmap b = DrawLogo(sz))
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // ======================================================================
    // Cửa sổ ẩn nhận phím tắt toàn cục
    // ======================================================================
    internal class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public bool Register(int id, Keys k)
        {
            uint mod = 0x4000; // MOD_NOREPEAT
            if ((k & Keys.Alt) != 0) mod |= 1;
            if ((k & Keys.Control) != 0) mod |= 2;
            if ((k & Keys.Shift) != 0) mod |= 4;
            return Native.RegisterHotKey(Handle, id, mod, (uint)(k & Keys.KeyCode));
        }

        public void Unregister(int id) { Native.UnregisterHotKey(Handle, id); }
        public void Dispose() { DestroyHandle(); }
    }

    // ======================================================================
    // Ứng dụng chạy ở khay hệ thống
    // ======================================================================
    internal class TrayApp : ApplicationContext
    {
        public Settings Cfg;
        public Icon FormIcon;
        NotifyIcon tray;
        HotkeyWindow hk;
        CaptureForm capture;
        SettingsForm settingsForm;
        string lastPath;
        ToolStripMenuItem miRegion, miFull;

        public const string Guide =
            "• Kéo chuột để chọn vùng. Rê chuột lên một cửa sổ rồi click để chọn nguyên cửa sổ đó.\n" +
            "• Ctrl + A: chọn toàn bộ màn hình hiện tại.\n" +
            "• Kéo các ô vuông ở viền để chỉnh kích thước, kéo bên trong để di chuyển vùng chọn.\n" +
            "• Phím mũi tên: dịch vùng chọn 1px  |  Shift + mũi tên: đổi kích thước 1px.\n\n" +
            "CÔNG CỤ VẼ (bấm lại lần nữa để tắt):\n" +
            "   R  Hình chữ nhật     E  Hình elip        A  Mũi tên\n" +
            "   L  Đường thẳng        P  Bút vẽ tự do     H  Bút dạ quang\n" +
            "   T  Viết chữ              N  Đánh số 1,2,3   M  Làm mờ (che thông tin)\n" +
            "   Giữ Shift khi vẽ: hình vuông / tròn / đường thẳng góc 45°.\n\n" +
            "LƯU / SAO CHÉP:\n" +
            "• Enter, Ctrl + C hoặc double-click: sao chép vào clipboard rồi đóng.\n" +
            "• Ctrl + S: lưu nhanh vào thư mục (đồng thời sao chép).\n" +
            "• Ctrl + Shift + S: lưu thành file (chọn nơi lưu).\n" +
            "• Ctrl + Z / Ctrl + Y: hoàn tác / làm lại.\n" +
            "• Chuột phải: chọn lại vùng khác.   Esc: thoát.";

        public TrayApp(Settings cfg)
        {
            Cfg = cfg;
            FormIcon = Util.MakeIcon(32);

            ContextMenuStrip m = new ContextMenuStrip();
            miRegion = new ToolStripMenuItem("Chụp vùng chọn", null, (s, e) => DelayedCapture());
            miRegion.Font = new Font(miRegion.Font, FontStyle.Bold);
            miFull = new ToolStripMenuItem("Chụp toàn màn hình (lưu ngay)", null, (s, e) => DelayedFull());
            m.Items.Add(miRegion);
            m.Items.Add(miFull);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Mở thư mục ảnh", null, (s, e) => OpenFolder()));
            m.Items.Add(new ToolStripMenuItem("Cài đặt...", null, (s, e) => ShowSettings()));
            m.Items.Add(new ToolStripMenuItem("Hướng dẫn sử dụng", null, (s, e) => ShowGuide()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Thoát", null, (s, e) => ExitApp()));

            tray = new NotifyIcon();
            tray.Icon = Util.MakeIcon(SystemInformation.SmallIconSize.Width);
            tray.Text = "ScreenSnap - chụp màn hình";
            tray.ContextMenuStrip = m;
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) DelayedCapture(); };
            tray.BalloonTipClicked += (s, e) => OpenLast();

            hk = new HotkeyWindow();
            hk.Pressed += id =>
            {
                if (id == 1) StartCapture();
                else if (id == 2) FullCapture();
            };
            string fail = RegisterHotkeys();
            if (Cfg.FirstRun && fail != null)
            {
                // Phím mặc định bị phần mềm khác chiếm -> thử các tổ hợp dự phòng
                UnregisterHotkeys();
                Keys[] alts = { Keys.Control | Keys.Shift | Keys.X, Keys.Control | Keys.Alt | Keys.X, Keys.Control | Keys.Shift | Keys.Q };
                foreach (Keys k in alts)
                {
                    if (hk.Register(9, k)) { hk.Unregister(9); Cfg.HotkeyRegion = k; break; }
                }
                if (Cfg.HotkeyFull != Keys.None && !hk.Register(9, Cfg.HotkeyFull)) Cfg.HotkeyFull = Keys.None;
                else hk.Unregister(9);
                fail = RegisterHotkeys();
            }
            Util.ApplyStartup(Cfg);
            ListenCaptureEvent();

            if (Program.CaptureNow && !Cfg.FirstRun) { DelayedCapture(); return; }
            if (Cfg.FirstRun)
            {
                Cfg.Save();
                ShowSettings();
            }
            else if (fail != null) WarnHotkey(fail);
            else if (!Program.FromStartup && !Program.Relaunched)
                Balloon("ScreenSnap đang chạy", "Nhấn " + Util.HotkeyToString(Cfg.HotkeyRegion) + " để chụp màn hình.", ToolTipIcon.None);
        }

        // "ScreenSnap.exe --capture" khi app đang chạy -> mở màn hình chụp (dùng cho shortcut / ghim taskbar)
        void ListenCaptureEvent()
        {
            Control invoker = new Control();
            invoker.CreateControl();
            IntPtr force = invoker.Handle;
            EventWaitHandle ev;
            try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.CaptureEventName); }
            catch { return; }
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    try { invoker.BeginInvoke(new Action(StartCapture)); } catch { return; }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public string RegisterHotkeys()
        {
            UnregisterHotkeys();
            List<string> fails = new List<string>();
            if (Cfg.HotkeyRegion != Keys.None && !hk.Register(1, Cfg.HotkeyRegion)) fails.Add(Util.HotkeyToString(Cfg.HotkeyRegion));
            if (Cfg.HotkeyFull != Keys.None && !hk.Register(2, Cfg.HotkeyFull)) fails.Add(Util.HotkeyToString(Cfg.HotkeyFull));
            miRegion.ShortcutKeyDisplayString = Cfg.HotkeyRegion == Keys.None ? "" : Util.HotkeyToString(Cfg.HotkeyRegion).Replace(" ", "");
            miFull.ShortcutKeyDisplayString = Cfg.HotkeyFull == Keys.None ? "" : Util.HotkeyToString(Cfg.HotkeyFull).Replace(" ", "");
            return fails.Count == 0 ? null : string.Join(", ", fails.ToArray());
        }

        public void UnregisterHotkeys() { hk.Unregister(1); hk.Unregister(2); }

        public void WarnHotkey(string combos)
        {
            Balloon("Phím tắt bị trùng", "Phím tắt " + combos + " đang bị phần mềm khác sử dụng. Hãy đổi phím khác trong Cài đặt.", ToolTipIcon.Warning);
        }

        void Delayed(Action a)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 250;
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }
        void DelayedCapture() { Delayed(StartCapture); }
        void DelayedFull() { Delayed(FullCapture); }

        public void StartCapture()
        {
            if (capture != null && !capture.IsDisposed) { capture.Activate(); return; }
            try
            {
                capture = new CaptureForm(this);
                capture.FormClosed += (s, e) => { capture = null; };
                capture.Show();
            }
            catch (Exception ex)
            {
                capture = null;
                Balloon("Không chụp được", ex.Message, ToolTipIcon.Error);
            }
        }

        public void FullCapture()
        {
            if (capture != null) return;
            try
            {
                using (Bitmap b = Util.CaptureScreen(SystemInformation.VirtualScreen, PixelFormat.Format24bppRgb))
                {
                    string path = Util.SaveToFolder(b, Cfg);
                    Util.CopyImage(b);
                    Notify("Đã chụp toàn màn hình", "Đã lưu & sao chép: " + Path.GetFileName(path) + "\nBấm vào đây để mở.", path);
                }
            }
            catch (Exception ex) { Balloon("Lỗi khi chụp", ex.Message, ToolTipIcon.Error); }
        }

        public void Notify(string title, string text, string path)
        {
            if (path != null) lastPath = path;
            if (Cfg.Notify) Balloon(title, text, ToolTipIcon.None);
        }

        public void Balloon(string title, string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(3000, title, text, icon);
        }

        void OpenLast()
        {
            try
            {
                if (lastPath != null && File.Exists(lastPath)) Process.Start("explorer.exe", "/select,\"" + lastPath + "\"");
            }
            catch { }
        }

        public void OpenFolder()
        {
            try
            {
                string dir = string.IsNullOrEmpty(Cfg.SaveFolder) ? Settings.DefaultFolder() : Cfg.SaveFolder;
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "ScreenSnap"); }
        }

        void ShowGuide()
        {
            MessageBox.Show(Guide, "ScreenSnap - Hướng dẫn", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            UnregisterHotkeys(); // để ô nhập phím tắt bắt được tổ hợp phím
            settingsForm = new SettingsForm(this);
            settingsForm.FormClosed += (s, e) =>
            {
                settingsForm = null;
                string fail = RegisterHotkeys();
                if (fail != null) WarnHotkey(fail);
            };
            settingsForm.Show();
            settingsForm.Activate();
        }

        public void RestartElevated()
        {
            if (Util.RelaunchElevated(null)) ExitApp();
            else MessageBox.Show("Không thể chạy với quyền Administrator (yêu cầu đã bị từ chối).", "ScreenSnap",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public void ExitApp()
        {
            try { UnregisterHotkeys(); hk.Dispose(); } catch { }
            if (capture != null) capture.Close();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    // ======================================================================
    // Các hình vẽ chú thích
    // ======================================================================
    internal enum Tool { None, Rect, Ellipse, Arrow, Line, Pen, Marker, Text, Number, Mosaic }

    internal class RenderCtx
    {
        public Func<int, Bitmap> Mosaic;
    }

    internal abstract class Shape
    {
        public abstract void Draw(Graphics g, RenderCtx c);
        public virtual void Update(Point p, bool shift) { }
        public virtual bool IsValid() { return true; }
    }

    internal class DragShape : Shape
    {
        public Tool Kind;
        public Point A, B;
        public Color Color;
        public float W;
        public int Block;

        public DragShape(Tool kind, Point p, Color c, float w, int block)
        {
            Kind = kind; A = p; B = p; Color = c; W = w; Block = block;
        }

        public Rectangle Rect
        {
            get { return Rectangle.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y)); }
        }

        public override void Update(Point p, bool shift)
        {
            if (shift)
            {
                int dx = p.X - A.X, dy = p.Y - A.Y;
                if (Kind == Tool.Line || Kind == Tool.Arrow)
                {
                    double ang = Math.Atan2(dy, dx), step = Math.PI / 4;
                    ang = Math.Round(ang / step) * step;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    p = new Point(A.X + (int)Math.Round(len * Math.Cos(ang)), A.Y + (int)Math.Round(len * Math.Sin(ang)));
                }
                else
                {
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    p = new Point(A.X + (dx < 0 ? -d : d), A.Y + (dy < 0 ? -d : d));
                }
            }
            B = p;
        }

        public override bool IsValid() { return Math.Abs(A.X - B.X) > 2 || Math.Abs(A.Y - B.Y) > 2; }

        public override void Draw(Graphics g, RenderCtx c)
        {
            Rectangle r = Rect;
            switch (Kind)
            {
                case Tool.Rect:
                    using (Pen p = new Pen(Color, W)) { p.LineJoin = LineJoin.Miter; g.DrawRectangle(p, r); }
                    break;
                case Tool.Ellipse:
                    using (Pen p = new Pen(Color, W)) g.DrawEllipse(p, r);
                    break;
                case Tool.Line:
                    using (Pen p = new Pen(Color, W)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, A, B); }
                    break;
                case Tool.Arrow:
                    Util.DrawArrow(g, A, B, Color, W * 1.5f);
                    break;
                case Tool.Mosaic:
                    if (r.Width > 0 && r.Height > 0)
                    {
                        Bitmap m = c.Mosaic(Block);
                        InterpolationMode im = g.InterpolationMode;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(m, r, r, GraphicsUnit.Pixel);
                        g.InterpolationMode = im;
                    }
                    break;
            }
        }
    }

    internal class PenShape : Shape
    {
        public List<Point> Pts = new List<Point>();
        public Color Color;
        public float W;

        public PenShape(Point p, Color c, float w) { Pts.Add(p); Color = c; W = w; }

        public override void Update(Point p, bool shift)
        {
            if (shift)
            {
                Point first = Pts[0];
                Pts.Clear(); Pts.Add(first); Pts.Add(p);
                return;
            }
            Point last = Pts[Pts.Count - 1];
            if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 2) Pts.Add(p);
        }

        public override void Draw(Graphics g, RenderCtx c)
        {
            if (Pts.Count == 1)
            {
                using (SolidBrush b = new SolidBrush(Color))
                    g.FillEllipse(b, Pts[0].X - W / 2, Pts[0].Y - W / 2, W, W);
                return;
            }
            using (Pen p = new Pen(Color, W))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, Pts.ToArray());
            }
        }
    }

    internal class TextShape : Shape
    {
        public string Text;
        public Font Font;
        public Color Color;
        public Point P;

        public TextShape(string text, Font f, Color c, Point p) { Text = text; Font = f; Color = c; P = p; }

        public override void Draw(Graphics g, RenderCtx c)
        {
            TextRenderingHint h = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color))
                g.DrawString(Text, Font, b, P.X + 1, P.Y + 1, StringFormat.GenericTypographic);
            g.TextRenderingHint = h;
        }
    }

    internal class NumberShape : Shape
    {
        public Point C;
        public int N;
        public Color Color;
        public float R;

        public NumberShape(Point c, int n, Color col, float r) { C = c; N = n; Color = col; R = r; }

        public override void Draw(Graphics g, RenderCtx c)
        {
            RectangleF rc = new RectangleF(C.X - R, C.Y - R, R * 2, R * 2);
            using (SolidBrush b = new SolidBrush(Color)) g.FillEllipse(b, rc);
            using (Pen p = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1.5f, R * 0.12f))) g.DrawEllipse(p, rc);
            Color fg = Color.GetBrightness() > 0.8f ? Color.Black : Color.White;
            TextRenderingHint h = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (Font f = new Font("Segoe UI", R * (N >= 10 ? 0.95f : 1.15f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                rc.Offset(0, R * 0.04f);
                g.DrawString(N.ToString(), f, b, rc, sf);
            }
            g.TextRenderingHint = h;
        }
    }

    // ======================================================================
    // Màn hình chụp: chọn vùng + thanh công cụ vẽ
    // ======================================================================
    internal class CaptureForm : Form
    {
        enum Mode { Idle, Creating, Adjusting, Drawing, Selected }

        class Btn { public string Id; public Rectangle R; }

        static readonly string[] Row1 = { "rect", "ellipse", "arrow", "line", "pen", "marker", "text", "number", "mosaic", "|", "undo", "redo", "|", "saveas", "save", "copy", "close" };
        static readonly Color[] Palette = {
            Color.FromArgb(255, 59, 48), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 214, 10), Color.FromArgb(52, 199, 89),
            Color.FromArgb(0, 122, 255), Color.FromArgb(175, 82, 222), Color.FromArgb(25, 25, 25), Color.White };
        static readonly Color Accent = Color.FromArgb(0, 150, 255);

        TrayApp app;
        Settings cfg;
        Bitmap shot, dim;
        Rectangle vs;
        float s = 1f;
        List<Rectangle> winRects = new List<Rectangle>();
        Rectangle hoverWin = Rectangle.Empty;

        Mode mode = Mode.Idle;
        Rectangle sel;
        bool hasSel;
        Point downPt, mouse;
        bool moved;
        int handle = -1;
        Rectangle selStart;

        Tool tool = Tool.None;
        List<Shape> shapes = new List<Shape>();
        List<Shape> redo = new List<Shape>();
        Shape cur;
        Color color;
        int sizeLevel;
        TextBox tb;
        Dictionary<int, Bitmap> mosaicCache = new Dictionary<int, Bitmap>();

        List<Btn> btns = new List<Btn>();
        Rectangle tbRect, tbRect2;
        string hoverBtn;
        Font uiFont, iconFont, letterFont, smallBold;

        public CaptureForm(TrayApp app)
        {
            this.app = app;
            cfg = app.Cfg;
            color = Color.FromArgb(cfg.ColorArgb);
            sizeLevel = Math.Max(1, Math.Min(3, cfg.SizeLevel));
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = Math.Max(1f, g.DpiX / 96f);

            vs = SystemInformation.VirtualScreen;
            shot = Util.CaptureScreen(vs, PixelFormat.Format32bppPArgb);
            dim = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dim))
            {
                g.DrawImageUnscaled(shot, 0, 0);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(115, 0, 0, 0))) g.FillRectangle(b, 0, 0, vs.Width, vs.Height);
            }
            CollectWindows();

            uiFont = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            iconFont = new Font("Segoe MDL2 Assets", 16f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            letterFont = new Font("Segoe UI", 17f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            smallBold = new Font("Segoe UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = false;
            Text = "ScreenSnap";
            Bounds = vs;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Bounds = vs;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle);
            Focus();
            mouse = PointToClient(Cursor.Position);
            hoverWin = FindWindowAt(mouse);
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            cfg.ColorArgb = color.ToArgb();
            cfg.SizeLevel = sizeLevel;
            cfg.Save();
            if (shot != null) shot.Dispose();
            if (dim != null) dim.Dispose();
            foreach (Bitmap b in mosaicCache.Values) b.Dispose();
            mosaicCache.Clear();
            uiFont.Dispose(); iconFont.Dispose(); letterFont.Dispose(); smallBold.Dispose();
            base.OnFormClosed(e);
        }

        // ---------- Phát hiện cửa sổ dưới con trỏ ----------
        void CollectWindows()
        {
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                int ex = Native.GetWindowLong(h, -20);
                if ((ex & 0x20) != 0) return true; // WS_EX_TRANSPARENT
                int cloaked = 0;
                try { if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; } catch { }
                Native.RECT rc;
                bool ok;
                try { ok = Native.DwmGetWindowAttribute(h, 9, out rc, Marshal.SizeOf(typeof(Native.RECT))) == 0; }
                catch { ok = false; rc = new Native.RECT(); }
                if (!ok && !Native.GetWindowRect(h, out rc)) return true;
                Rectangle r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                r.Offset(-vs.X, -vs.Y);
                r.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
                if (r.Width < 10 || r.Height < 10) return true;
                winRects.Add(r);
                return true;
            }, IntPtr.Zero);
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle r = sc.Bounds;
                r.Offset(-vs.X, -vs.Y);
                winRects.Add(r);
            }
        }

        Rectangle FindWindowAt(Point p)
        {
            foreach (Rectangle r in winRects) if (r.Contains(p)) return r;
            return Rectangle.Empty;
        }

        Rectangle ScreenRectAt(Point p)
        {
            Rectangle r = Screen.FromPoint(new Point(p.X + vs.X, p.Y + vs.Y)).Bounds;
            r.Offset(-vs.X, -vs.Y);
            return r;
        }

        // ---------- Kích thước nét theo mức ----------
        float Stroke() { return new float[] { 2f, 4f, 6f }[sizeLevel - 1] * s; }
        float MarkerW() { return new float[] { 12f, 18f, 26f }[sizeLevel - 1] * s; }
        float TextPx() { return new float[] { 18f, 26f, 38f }[sizeLevel - 1] * s; }
        float NumR() { return new float[] { 11f, 15f, 20f }[sizeLevel - 1] * s; }
        int Block() { return Math.Max(5, (int)(new float[] { 7f, 11f, 16f }[sizeLevel - 1] * s)); }

        Bitmap GetMosaic(int b)
        {
            Bitmap m;
            if (mosaicCache.TryGetValue(b, out m)) return m;
            int sw = Math.Max(1, (shot.Width + b - 1) / b), sh = Math.Max(1, (shot.Height + b - 1) / b);
            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(shot, new Rectangle(0, 0, sw, sh), 0, 0, sw * b, sh * b, GraphicsUnit.Pixel, ia);
                }
                m = new Bitmap(sw * b, sh * b, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(m))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, new Rectangle(0, 0, sw * b, sh * b), 0, 0, sw, sh, GraphicsUnit.Pixel);
                }
            }
            mosaicCache[b] = m;
            return m;
        }

        RenderCtx Ctx()
        {
            RenderCtx c = new RenderCtx();
            c.Mosaic = GetMosaic;
            return c;
        }

        // ---------- Vẽ màn hình ----------
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);

            Rectangle bright = hasSel ? sel : (mode == Mode.Idle ? hoverWin : Rectangle.Empty);
            if (bright.Width > 0 && bright.Height > 0)
            {
                Rectangle r = Rectangle.Intersect(bright, clip);
                if (r.Width > 0 && r.Height > 0) g.DrawImage(shot, r, r, GraphicsUnit.Pixel);
            }
            g.CompositingMode = CompositingMode.SourceOver;

            if (hasSel)
            {
                g.SetClip(sel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RenderCtx c = Ctx();
                foreach (Shape sh in shapes) sh.Draw(g, c);
                if (cur != null) cur.Draw(g, c);
                g.ResetClip();
                g.SmoothingMode = SmoothingMode.None;

                using (Pen p = new Pen(Accent, Math.Max(1f, (float)Math.Round(s))))
                    g.DrawRectangle(p, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
                if (mode == Mode.Selected && tool == Tool.None) DrawHandles(g);
                DrawSizeLabel(g);
                if (tb != null)
                {
                    Rectangle fr = tb.Bounds;
                    fr.Inflate((int)(3 * s), (int)(3 * s));
                    using (Pen p = new Pen(Accent, 1f)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, fr); }
                }
                if (mode == Mode.Selected || mode == Mode.Drawing) DrawToolbar(g);
            }
            else if (mode == Mode.Idle && hoverWin.Width > 0)
            {
                using (Pen p = new Pen(Accent, Math.Max(2f, 2f * s)))
                    g.DrawRectangle(p, hoverWin.X + 1, hoverWin.Y + 1, hoverWin.Width - 2, hoverWin.Height - 2);
            }

            if (cfg.Magnifier && (mode == Mode.Idle || mode == Mode.Creating)) DrawMagnifier(g);
        }

        Rectangle[] HandleRects()
        {
            int hs = (int)(8 * s);
            int x1 = sel.Left, x2 = sel.Left + sel.Width / 2, x3 = sel.Right, y1 = sel.Top, y2 = sel.Top + sel.Height / 2, y3 = sel.Bottom;
            Point[] pts = { new Point(x1, y1), new Point(x2, y1), new Point(x3, y1), new Point(x3, y2), new Point(x3, y3), new Point(x2, y3), new Point(x1, y3), new Point(x1, y2) };
            Rectangle[] r = new Rectangle[8];
            for (int i = 0; i < 8; i++) r[i] = new Rectangle(pts[i].X - hs / 2, pts[i].Y - hs / 2, hs, hs);
            return r;
        }

        void DrawHandles(Graphics g)
        {
            using (Pen p = new Pen(Accent, 1f))
                foreach (Rectangle r in HandleRects())
                {
                    g.FillRectangle(Brushes.White, r);
                    g.DrawRectangle(p, r);
                }
        }

        void DrawSizeLabel(Graphics g)
        {
            string t = sel.Width + " × " + sel.Height;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            SizeF sz = g.MeasureString(t, uiFont);
            int pw = (int)(sz.Width + 10 * s), ph = (int)(sz.Height + 4 * s);
            Rectangle scr = ScreenRectAt(sel.Location);
            int x = sel.X, y = sel.Y - ph - (int)(5 * s);
            if (y < scr.Top) y = sel.Y + (int)(5 * s);
            if (y < scr.Top) y = scr.Top;
            Rectangle r = new Rectangle(x, y, pw, ph);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Util.FillRound(g, r, 4 * s, Color.FromArgb(200, 20, 20, 20));
            g.SmoothingMode = SmoothingMode.None;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(t, uiFont, Brushes.White, r, sf);
            }
        }

        void DrawMagnifier(Graphics g)
        {
            const int src = 17;
            int z = Math.Max(6, (int)(7 * s));
            int box = src * z;
            int infoH = (int)(42 * s);
            Point p = mouse;
            Rectangle scr = ScreenRectAt(p);
            int x = p.X + (int)(22 * s), y = p.Y + (int)(26 * s);
            if (x + box > scr.Right) x = p.X - (int)(22 * s) - box;
            if (y + box + infoH > scr.Bottom) y = p.Y - (int)(26 * s) - box - infoH;
            Rectangle dst = new Rectangle(x, y, box, box);
            Rectangle srcR = new Rectangle(p.X - src / 2, p.Y - src / 2, src, src);

            g.FillRectangle(Brushes.Black, dst);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, dst, srcR, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;

            int cx = x + (src / 2) * z, cy = y + (src / 2) * z;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 0, 160, 255)))
            {
                g.FillRectangle(b, x, cy, box, z);
                g.FillRectangle(b, cx, y, z, box);
            }
            g.DrawRectangle(Pens.White, cx, cy, z - 1, z - 1);
            g.DrawRectangle(Pens.White, dst);

            Rectangle info = new Rectangle(x, y + box, box + 1, infoH);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(215, 20, 20, 20))) g.FillRectangle(b, info);
            Color px = Color.Black;
            if (p.X >= 0 && p.Y >= 0 && p.X < shot.Width && p.Y < shot.Height) px = shot.GetPixel(p.X, p.Y);
            string l1 = (mode == Mode.Creating && hasSel) ? (sel.Width + " × " + sel.Height) : ("X: " + (p.X + vs.X) + "   Y: " + (p.Y + vs.Y));
            string l2 = "RGB(" + px.R + ", " + px.G + ", " + px.B + ")";
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int pad = (int)(6 * s);
            g.DrawString(l1, uiFont, Brushes.White, x + pad, y + box + (int)(3 * s));
            int sw = (int)(12 * s);
            int ly = y + box + (int)(22 * s);
            using (SolidBrush b = new SolidBrush(px)) g.FillRectangle(b, x + pad, ly + (int)(2 * s), sw, sw);
            g.DrawRectangle(Pens.White, x + pad, ly + (int)(2 * s), sw, sw);
            g.DrawString(l2, uiFont, Brushes.White, x + pad + sw + (int)(5 * s), ly);
        }

        // ---------- Thanh công cụ ----------
        int RowWidth(string[] ids, int B, int gap, int sepW, int pad)
        {
            int w = pad * 2;
            for (int i = 0; i < ids.Length; i++) w += (ids[i] == "|" ? sepW : B) + (i > 0 ? gap : 0);
            return w;
        }

        void AddRow(string[] ids, int x, int y, int B, int gap, int sepW, int pad)
        {
            int cx = x + pad;
            foreach (string id in ids)
            {
                Btn b = new Btn();
                b.Id = id;
                int w = id == "|" ? sepW : B;
                b.R = new Rectangle(cx, y + pad, w, B);
                btns.Add(b);
                cx += w + gap;
            }
        }

        void LayoutToolbar()
        {
            btns.Clear();
            int B = (int)(32 * s), gap = (int)(2 * s), sepW = (int)(9 * s), pad = (int)(5 * s);
            int h = B + pad * 2;
            int w1 = RowWidth(Row1, B, gap, sepW, pad);
            string[] row2 = null;
            if (tool != Tool.None && tool != Tool.Mosaic)
            {
                row2 = new string[4 + Palette.Length];
                row2[0] = "size1"; row2[1] = "size2"; row2[2] = "size3"; row2[3] = "|";
                for (int i = 0; i < Palette.Length; i++) row2[4 + i] = "c" + i;
            }
            else if (tool == Tool.Mosaic) row2 = new string[] { "size1", "size2", "size3" };
            int w2 = row2 != null ? RowWidth(row2, B, gap, sepW, pad) : 0;
            int rowGap = (int)(4 * s), vgap = (int)(8 * s);
            int total = h + (row2 != null ? h + rowGap : 0);

            Rectangle scr = ScreenRectAt(new Point(Math.Max(0, sel.Right - 1), Math.Max(0, sel.Bottom - 1)));
            int x = sel.Right - w1;
            int y = sel.Bottom + vgap;
            if (y + total > scr.Bottom)
            {
                y = sel.Top - vgap - total;
                if (y < scr.Top) y = Math.Max(scr.Top + 2, sel.Bottom - vgap - total);
            }
            x = Math.Max(scr.Left + 2, Math.Min(x, scr.Right - w1 - 2));
            tbRect = new Rectangle(x, y, w1, h);
            AddRow(Row1, x, y, B, gap, sepW, pad);
            if (row2 != null)
            {
                int x2 = Math.Max(scr.Left + 2, Math.Min(x, scr.Right - w2 - 2));
                tbRect2 = new Rectangle(x2, y + h + rowGap, w2, h);
                AddRow(row2, x2, tbRect2.Y, B, gap, sepW, pad);
            }
            else tbRect2 = Rectangle.Empty;
        }

        Btn HitButton(Point p)
        {
            foreach (Btn b in btns) if (b.Id != "|" && b.R.Contains(p)) return b;
            return null;
        }

        static Tool ToolOf(string id)
        {
            switch (id)
            {
                case "rect": return Tool.Rect;
                case "ellipse": return Tool.Ellipse;
                case "arrow": return Tool.Arrow;
                case "line": return Tool.Line;
                case "pen": return Tool.Pen;
                case "marker": return Tool.Marker;
                case "text": return Tool.Text;
                case "number": return Tool.Number;
                case "mosaic": return Tool.Mosaic;
            }
            return Tool.None;
        }

        static string Tip(string id)
        {
            switch (id)
            {
                case "rect": return "Hình chữ nhật (R)";
                case "ellipse": return "Hình elip / tròn (E)";
                case "arrow": return "Mũi tên (A)";
                case "line": return "Đường thẳng (L)";
                case "pen": return "Bút vẽ tự do (P)";
                case "marker": return "Bút dạ quang (H)";
                case "text": return "Viết chữ chú thích (T)";
                case "number": return "Đánh số thứ tự (N)";
                case "mosaic": return "Làm mờ / che thông tin (M)";
                case "undo": return "Hoàn tác (Ctrl+Z)";
                case "redo": return "Làm lại (Ctrl+Y)";
                case "saveas": return "Lưu thành file... (Ctrl+Shift+S)";
                case "save": return "Lưu nhanh vào thư mục (Ctrl+S)";
                case "copy": return "Sao chép vào clipboard (Enter)";
                case "close": return "Hủy (Esc)";
                case "size1": return "Cỡ nhỏ";
                case "size2": return "Cỡ vừa";
                case "size3": return "Cỡ lớn";
            }
            if (id.StartsWith("c")) return "Chọn màu";
            return null;
        }

        void DrawToolbar(Graphics g)
        {
            LayoutToolbar();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawPanel(g, tbRect);
            if (!tbRect2.IsEmpty) DrawPanel(g, tbRect2);

            foreach (Btn b in btns)
            {
                if (b.Id == "|")
                {
                    using (Pen p = new Pen(Color.FromArgb(215, 215, 215), 1f))
                        g.DrawLine(p, b.R.X + b.R.Width / 2, b.R.Y + 6 * s, b.R.X + b.R.Width / 2, b.R.Bottom - 6 * s);
                    continue;
                }
                bool active = false;
                Tool t = ToolOf(b.Id);
                if (t != Tool.None && t == tool) active = true;
                if (b.Id == "size" + sizeLevel) active = true;
                bool disabled = (b.Id == "undo" && shapes.Count == 0) || (b.Id == "redo" && redo.Count == 0);
                if (active) Util.FillRound(g, b.R, 5 * s, Color.FromArgb(222, 238, 255));
                else if (b.Id == hoverBtn && !disabled) Util.FillRound(g, b.R, 5 * s, Color.FromArgb(236, 236, 236));
                DrawIcon(g, b, active, disabled);
            }
            if (hoverBtn != null) DrawTooltip(g);
            g.SmoothingMode = SmoothingMode.None;
        }

        void DrawPanel(Graphics g, Rectangle r)
        {
            Rectangle sh = r;
            sh.Offset(0, (int)(2 * s));
            sh.Inflate(1, 1);
            Util.FillRound(g, sh, 8 * s, Color.FromArgb(60, 0, 0, 0));
            Util.FillRound(g, r, 7 * s, Color.FromArgb(250, 250, 250));
        }

        void DrawIcon(Graphics g, Btn b, bool active, bool disabled)
        {
            Color fg = disabled ? Color.FromArgb(185, 185, 185) : (active ? Color.FromArgb(0, 110, 230) : Color.FromArgb(45, 45, 45));
            if (!disabled && b.Id == "copy") fg = Color.FromArgb(20, 150, 70);
            if (!disabled && b.Id == "close") fg = Color.FromArgb(220, 50, 50);
            int inset = (int)(8 * s);
            Rectangle ir = Rectangle.Inflate(b.R, -inset, -inset);
            float L = ir.Left, T = ir.Top, R = ir.Right, Bm = ir.Bottom;
            using (Pen pen = new Pen(fg, 1.8f * s))
            using (SolidBrush br = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat())
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                switch (b.Id)
                {
                    case "rect": g.DrawRectangle(pen, L, T + 2 * s, R - L, Bm - T - 4 * s); break;
                    case "ellipse": g.DrawEllipse(pen, L, T + 1 * s, R - L, Bm - T - 2 * s); break;
                    case "line": g.DrawLine(pen, L + 1 * s, Bm - 1 * s, R - 1 * s, T + 1 * s); break;
                    case "arrow": Util.DrawArrow(g, new PointF(L + 1 * s, Bm - 1 * s), new PointF(R, T), fg, 2.2f * s); break;
                    case "pen":
                        g.DrawBezier(pen, L, Bm - 3 * s, L + (R - L) * 0.35f, T - 4 * s, L + (R - L) * 0.6f, Bm + 4 * s, R, T + 3 * s);
                        break;
                    case "marker":
                        using (Pen hp = new Pen(Color.FromArgb(disabled ? 80 : 170, 255, 200, 0), 7f * s))
                        { hp.StartCap = LineCap.Round; hp.EndCap = LineCap.Round; g.DrawLine(hp, L + 2 * s, Bm - 3 * s, R - 2 * s, T + 3 * s); }
                        g.DrawLine(pen, L + 2 * s, Bm - 3 * s, R - 2 * s, T + 3 * s);
                        break;
                    case "text": g.DrawString("T", letterFont, br, b.R, sf); break;
                    case "number":
                        g.DrawEllipse(pen, L, T, R - L, Bm - T);
                        g.DrawString("1", smallBold, br, new RectangleF(L, T + 0.5f * s, R - L, Bm - T), sf);
                        break;
                    case "mosaic":
                        {
                            float cw = (R - L) / 3f;
                            for (int i = 0; i < 3; i++)
                                for (int j = 0; j < 3; j++)
                                    using (SolidBrush cb = new SolidBrush(((i + j) % 2 == 0) ? fg : Color.FromArgb(120, fg)))
                                        g.FillRectangle(cb, L + i * cw, T + j * cw, cw - 0.5f, cw - 0.5f);
                        }
                        break;
                    case "undo": g.DrawString("\uE7A7", iconFont, br, b.R, sf); break;
                    case "redo": g.DrawString("\uE7A6", iconFont, br, b.R, sf); break;
                    case "saveas": g.DrawString("\uE792", iconFont, br, b.R, sf); break;
                    case "save": g.DrawString("\uE74E", iconFont, br, b.R, sf); break;
                    case "copy": g.DrawString("\uE8FB", iconFont, br, b.R, sf); break;
                    case "close": g.DrawString("\uE711", iconFont, br, b.R, sf); break;
                    case "size1":
                    case "size2":
                    case "size3":
                        {
                            float d = new float[] { 5f, 9f, 14f }[b.Id[4] - '1'] * s;
                            float cx = b.R.X + b.R.Width / 2f, cy = b.R.Y + b.R.Height / 2f;
                            g.FillEllipse(br, cx - d / 2, cy - d / 2, d, d);
                        }
                        break;
                    default:
                        if (b.Id.StartsWith("c"))
                        {
                            int idx = int.Parse(b.Id.Substring(1));
                            Color pc = Palette[idx];
                            Rectangle cr = Rectangle.Inflate(b.R, -(int)(7 * s), -(int)(7 * s));
                            if (pc.ToArgb() == color.ToArgb())
                            {
                                Rectangle ring = Rectangle.Inflate(b.R, -(int)(3 * s), -(int)(3 * s));
                                using (Pen rp = new Pen(Color.FromArgb(0, 120, 230), 2f * s))
                                using (GraphicsPath gp = Util.RoundRect(ring, 5 * s)) g.DrawPath(rp, gp);
                            }
                            Util.FillRound(g, cr, 4 * s, pc);
                            using (Pen bp = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
                            using (GraphicsPath gp = Util.RoundRect(cr, 4 * s)) g.DrawPath(bp, gp);
                        }
                        break;
                }
            }
        }

        void DrawTooltip(Graphics g)
        {
            Btn hb = null;
            foreach (Btn b in btns) if (b.Id == hoverBtn) hb = b;
            if (hb == null) return;
            string t = Tip(hb.Id);
            if (t == null) return;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            SizeF sz = g.MeasureString(t, uiFont);
            int w = (int)(sz.Width + 12 * s), h = (int)(sz.Height + 6 * s);
            Rectangle rowRect = tbRect.Contains(hb.R) ? tbRect : tbRect2;
            Rectangle scr = ScreenRectAt(hb.R.Location);
            int x = hb.R.X + hb.R.Width / 2 - w / 2;
            int y = rowRect.Bottom + (int)(4 * s);
            if (rowRect == tbRect && !tbRect2.IsEmpty) y = tbRect.Top - h - (int)(4 * s);
            if (y + h > scr.Bottom) y = rowRect.Top - h - (int)(4 * s);
            if (y < scr.Top) y = rowRect.Bottom + (int)(4 * s);
            x = Math.Max(scr.Left + 2, Math.Min(x, scr.Right - w - 2));
            Rectangle r = new Rectangle(x, y, w, h);
            Util.FillRound(g, r, 4 * s, Color.FromArgb(235, 30, 30, 30));
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(t, uiFont, Brushes.White, r, sf);
            }
        }

        // ---------- Chuột ----------
        int HitHandle(Point p)
        {
            Rectangle[] hr = HandleRects();
            int pad = (int)(4 * s);
            for (int i = 0; i < 8; i++)
            {
                Rectangle r = hr[i];
                r.Inflate(pad, pad);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        static Cursor HandleCursor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                case 3: case 7: return Cursors.SizeWE;
                case 8: return Cursors.SizeAll;
            }
            return Cursors.Cross;
        }

        Rectangle ClientBounds { get { return new Rectangle(0, 0, vs.Width, vs.Height); } }

        Rectangle RectFrom(Point a, Point b)
        {
            Rectangle r = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
            r.Intersect(ClientBounds);
            return r;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            mouse = e.Location;
            if (tb != null)
            {
                bool clickedInToolbar = HitButton(e.Location) != null;
                CommitText();
                if (!clickedInToolbar) return;
            }

            if (e.Button == MouseButtons.Right)
            {
                if (mode == Mode.Drawing) return;
                if (hasSel) ResetSelection();
                else Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (hasSel && mode == Mode.Selected)
            {
                LayoutToolbar();
                Btn b = HitButton(e.Location);
                if (b != null) { OnButton(b.Id); return; }
                if (tbRect.Contains(e.Location) || tbRect2.Contains(e.Location)) return;

                if (tool == Tool.None)
                {
                    int h = HitHandle(e.Location);
                    if (h >= 0) { BeginAdjust(h, e.Location); return; }
                    if (sel.Contains(e.Location)) { BeginAdjust(8, e.Location); return; }
                    if (shapes.Count == 0) { ResetSelection(); BeginCreate(e.Location); }
                    return;
                }
                if (sel.Contains(e.Location)) BeginDraw(e.Location);
                return;
            }

            if (mode == Mode.Idle) BeginCreate(e.Location);
        }

        void BeginCreate(Point p) { mode = Mode.Creating; downPt = p; moved = false; }

        void BeginAdjust(int h, Point p) { mode = Mode.Adjusting; handle = h; downPt = p; selStart = sel; }

        void BeginDraw(Point p)
        {
            switch (tool)
            {
                case Tool.Text: StartText(p); return;
                case Tool.Number:
                    int n = 1;
                    foreach (Shape sh in shapes) if (sh is NumberShape) n++;
                    shapes.Add(new NumberShape(p, n, color, NumR()));
                    redo.Clear();
                    Invalidate();
                    return;
                case Tool.Pen: cur = new PenShape(p, color, Stroke()); break;
                case Tool.Marker: cur = new PenShape(p, Color.FromArgb(105, color), MarkerW()); break;
                default: cur = new DragShape(tool, p, color, Stroke(), Block()); break;
            }
            mode = Mode.Drawing;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            switch (mode)
            {
                case Mode.Idle:
                    hoverWin = FindWindowAt(e.Location);
                    Invalidate();
                    break;
                case Mode.Creating:
                    if (!moved && (Math.Abs(e.X - downPt.X) > 3 || Math.Abs(e.Y - downPt.Y) > 3)) moved = true;
                    if (moved) { sel = RectFrom(downPt, e.Location); hasSel = true; }
                    Invalidate();
                    break;
                case Mode.Adjusting:
                    ApplyAdjust(e.Location);
                    Invalidate();
                    break;
                case Mode.Drawing:
                    if (cur != null) cur.Update(e.Location, (ModifierKeys & Keys.Shift) != 0);
                    Invalidate();
                    break;
                case Mode.Selected:
                    UpdateHover(e.Location);
                    break;
            }
        }

        void ApplyAdjust(Point p)
        {
            int dx = p.X - downPt.X, dy = p.Y - downPt.Y;
            int l = selStart.Left, t = selStart.Top, r = selStart.Right, b = selStart.Bottom;
            switch (handle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: r += dx; t += dy; break;
                case 3: r += dx; break;
                case 4: r += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
                case 8:
                    dx = Math.Max(-selStart.Left, Math.Min(dx, vs.Width - selStart.Right));
                    dy = Math.Max(-selStart.Top, Math.Min(dy, vs.Height - selStart.Bottom));
                    l += dx; r += dx; t += dy; b += dy;
                    break;
            }
            Rectangle nr = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            nr.Intersect(ClientBounds);
            if (nr.Width < 1) nr.Width = 1;
            if (nr.Height < 1) nr.Height = 1;
            sel = nr;
        }

        void UpdateHover(Point p)
        {
            LayoutToolbar();
            Btn b = HitButton(p);
            string id = b == null ? null : b.Id;
            if (id != hoverBtn) { hoverBtn = id; Invalidate(); }
            Cursor c;
            if (b != null || tbRect.Contains(p) || tbRect2.Contains(p)) c = Cursors.Hand;
            else if (tool == Tool.None)
            {
                int h = HitHandle(p);
                if (h >= 0) c = HandleCursor(h);
                else if (sel.Contains(p)) c = Cursors.SizeAll;
                else c = shapes.Count == 0 ? Cursors.Cross : Cursors.Default;
            }
            else if (sel.Contains(p)) c = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
            else c = Cursors.Default;
            if (Cursor != c) Cursor = c;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            switch (mode)
            {
                case Mode.Creating:
                    if (!moved)
                    {
                        if (hoverWin.Width > 0) { sel = hoverWin; hasSel = true; mode = Mode.Selected; }
                        else mode = Mode.Idle;
                    }
                    else if (sel.Width < 4 || sel.Height < 4) { hasSel = false; mode = Mode.Idle; }
                    else mode = Mode.Selected;
                    if (mode == Mode.Selected) UpdateHover(e.Location);
                    Invalidate();
                    break;
                case Mode.Adjusting:
                    mode = Mode.Selected;
                    Invalidate();
                    break;
                case Mode.Drawing:
                    if (cur != null && cur.IsValid()) { shapes.Add(cur); redo.Clear(); }
                    cur = null;
                    mode = Mode.Selected;
                    Invalidate();
                    break;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && hasSel && tool == Tool.None && sel.Contains(e.Location) && HitButton(e.Location) == null)
                DoCopy();
        }

        void ResetSelection()
        {
            CancelText();
            hasSel = false;
            shapes.Clear(); redo.Clear(); cur = null;
            tool = Tool.None;
            mode = Mode.Idle;
            hoverBtn = null;
            hoverWin = FindWindowAt(mouse);
            Cursor = Cursors.Cross;
            Invalidate();
        }

        void SetTool(Tool t)
        {
            CommitText();
            tool = t;
            UpdateHover(mouse);
            Invalidate();
        }

        void OnButton(string id)
        {
            Tool t = ToolOf(id);
            if (t != Tool.None) { SetTool(tool == t ? Tool.None : t); return; }
            switch (id)
            {
                case "undo": Undo(); break;
                case "redo": Redo(); break;
                case "saveas": DoSaveAs(); break;
                case "save": DoQuickSave(); break;
                case "copy": DoCopy(); break;
                case "close": Close(); break;
                case "size1": sizeLevel = 1; Invalidate(); break;
                case "size2": sizeLevel = 2; Invalidate(); break;
                case "size3": sizeLevel = 3; Invalidate(); break;
                default:
                    if (id.StartsWith("c")) { color = Palette[int.Parse(id.Substring(1))]; Invalidate(); }
                    break;
            }
        }

        void Undo()
        {
            CommitText();
            if (shapes.Count == 0) return;
            redo.Add(shapes[shapes.Count - 1]);
            shapes.RemoveAt(shapes.Count - 1);
            Invalidate();
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            shapes.Add(redo[redo.Count - 1]);
            redo.RemoveAt(redo.Count - 1);
            Invalidate();
        }

        // ---------- Viết chữ ----------
        void StartText(Point p)
        {
            Font f = new Font("Segoe UI", TextPx(), FontStyle.Bold, GraphicsUnit.Pixel);
            tb = new TextBox();
            tb.Multiline = true;
            tb.WordWrap = false;
            tb.AcceptsReturn = true;
            tb.BorderStyle = BorderStyle.None;
            tb.Font = f;
            tb.ForeColor = color;
            tb.BackColor = color.GetBrightness() > 0.75f ? Color.FromArgb(60, 60, 60) : Color.FromArgb(255, 255, 255);
            tb.Location = p;
            tb.TextChanged += (s2, e2) => ResizeTb();
            Controls.Add(tb);
            ResizeTb();
            tb.Focus();
            Invalidate();
        }

        void ResizeTb()
        {
            if (tb == null) return;
            string t = tb.Text.Length == 0 ? "W" : tb.Text + "W";
            if (t.EndsWith("\nW")) t += "\nW";
            Size sz = TextRenderer.MeasureText(t, tb.Font);
            tb.Size = new Size(Math.Max(sz.Width, (int)(40 * s)), Math.Max(sz.Height, tb.Font.Height) + 4);
            Invalidate();
        }

        void CommitText()
        {
            if (tb == null) return;
            string t = tb.Text.TrimEnd();
            if (t.Trim().Length > 0)
            {
                shapes.Add(new TextShape(t, (Font)tb.Font.Clone(), color, tb.Location));
                redo.Clear();
            }
            RemoveTb();
        }

        void CancelText() { if (tb != null) RemoveTb(); }

        void RemoveTb()
        {
            TextBox t = tb;
            tb = null;
            Controls.Remove(t);
            t.Dispose();
            ActiveControl = null;
            Focus();
            Invalidate();
        }

        // ---------- Bàn phím ----------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (tb != null)
            {
                if (keyData == Keys.Escape) { CancelText(); return true; }
                if (keyData == Keys.Enter) { CommitText(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            Keys k = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0;
            bool shift = (keyData & Keys.Shift) != 0;

            if (k == Keys.Escape) { Close(); return true; }
            if (mode == Mode.Drawing || mode == Mode.Creating || mode == Mode.Adjusting) return true;

            if (hasSel && (k == Keys.Enter || (ctrl && k == Keys.C))) { DoCopy(); return true; }
            if (hasSel && ctrl && k == Keys.S) { if (shift) DoSaveAs(); else DoQuickSave(); return true; }
            if (ctrl && k == Keys.Z) { Undo(); return true; }
            if (ctrl && k == Keys.Y) { Redo(); return true; }
            if (ctrl && k == Keys.A)
            {
                if (shapes.Count == 0)
                {
                    sel = ScreenRectAt(mouse);
                    hasSel = true;
                    mode = Mode.Selected;
                    Invalidate();
                }
                return true;
            }

            int dx = 0, dy = 0;
            if (k == Keys.Left) dx = -1;
            else if (k == Keys.Right) dx = 1;
            else if (k == Keys.Up) dy = -1;
            else if (k == Keys.Down) dy = 1;

            if (hasSel && mode == Mode.Selected)
            {
                if (dx != 0 || dy != 0)
                {
                    if (shift) { sel.Width = Math.Max(1, sel.Width + dx); sel.Height = Math.Max(1, sel.Height + dy); }
                    else { sel.X += dx; sel.Y += dy; }
                    Rectangle r = sel; r.Intersect(ClientBounds);
                    if (r.Width > 0 && r.Height > 0) sel = r;
                    Invalidate();
                    return true;
                }
                if (!ctrl)
                {
                    Tool t = Tool.None;
                    switch (k)
                    {
                        case Keys.R: t = Tool.Rect; break;
                        case Keys.E: t = Tool.Ellipse; break;
                        case Keys.A: t = Tool.Arrow; break;
                        case Keys.L: t = Tool.Line; break;
                        case Keys.P: t = Tool.Pen; break;
                        case Keys.H: t = Tool.Marker; break;
                        case Keys.T: t = Tool.Text; break;
                        case Keys.N: t = Tool.Number; break;
                        case Keys.M: t = Tool.Mosaic; break;
                    }
                    if (t != Tool.None) { SetTool(tool == t ? Tool.None : t); return true; }
                }
            }
            else if (mode == Mode.Idle && (dx != 0 || dy != 0))
            {
                Point cp = Cursor.Position;
                Cursor.Position = new Point(cp.X + dx, cp.Y + dy);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- Xuất ảnh ----------
        Bitmap RenderResult()
        {
            Bitmap b = new Bitmap(sel.Width, sel.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(shot, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                g.TranslateTransform(-sel.X, -sel.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RenderCtx c = Ctx();
                foreach (Shape sh in shapes) sh.Draw(g, c);
            }
            return b;
        }

        void DoCopy()
        {
            if (!hasSel) return;
            CommitText();
            try
            {
                string path = null;
                using (Bitmap b = RenderResult())
                {
                    Util.CopyImage(b);
                    if (cfg.AutoSaveOnCopy) path = Util.SaveToFolder(b, cfg);
                }
                Close();
                app.Notify("Đã sao chép ảnh", path == null ? "Nhấn Ctrl + V để dán vào nơi bạn muốn." : "Đã sao chép & lưu: " + Path.GetFileName(path), path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void DoQuickSave()
        {
            if (!hasSel) return;
            CommitText();
            try
            {
                string path;
                using (Bitmap b = RenderResult())
                {
                    path = Util.SaveToFolder(b, cfg);
                    try { Util.CopyImage(b); } catch { }
                }
                Close();
                app.Notify("Đã lưu ảnh", Path.GetFileName(path) + "\n(đã sao chép vào clipboard - bấm để mở thư mục)", path);
            }
            catch (Exception ex) { MessageBox.Show(this, "Không lưu được ảnh: " + ex.Message, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void DoSaveAs()
        {
            if (!hasSel) return;
            CommitText();
            using (Bitmap b = RenderResult())
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Lưu ảnh chụp màn hình";
                d.Filter = "Ảnh PNG (*.png)|*.png|Ảnh JPG (*.jpg)|*.jpg|Ảnh BMP (*.bmp)|*.bmp";
                d.FilterIndex = cfg.Format == "jpg" ? 2 : 1;
                d.FileName = "ScreenSnap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                try { if (Directory.Exists(cfg.SaveFolder)) d.InitialDirectory = cfg.SaveFolder; } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) { Activate(); return; }
                try
                {
                    Util.SaveImage(b, d.FileName);
                    Close();
                    app.Notify("Đã lưu ảnh", Path.GetFileName(d.FileName) + "\nBấm vào đây để mở thư mục.", d.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "Không lưu được ảnh: " + ex.Message, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }

    // ======================================================================
    // Ô nhập phím tắt
    // ======================================================================
    internal class HotkeyBox : TextBox
    {
        Keys val;

        public Keys Value
        {
            get { return val; }
            set { val = value; Text = Util.HotkeyToString(val); }
        }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            Cursor = Cursors.Hand;
        }

        static string ModText(Keys mods)
        {
            string t = "";
            if ((mods & Keys.Control) != 0) t += "Ctrl + ";
            if ((mods & Keys.Alt) != 0) t += "Alt + ";
            if ((mods & Keys.Shift) != 0) t += "Shift + ";
            return t;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if (mods == Keys.None && (key == Keys.Tab || key == Keys.Escape)) return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && (key == Keys.Back || key == Keys.Delete)) { Value = Keys.None; return true; }
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                Text = ModText(mods) + "...";
                return true;
            }
            bool plain = (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) || key == Keys.Space || key == Keys.Enter;
            if (mods == Keys.None && plain)
            {
                Text = "Cần kèm Ctrl / Alt / Shift";
                return true;
            }
            Value = key | mods;
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PrintScreen) Value = Keys.PrintScreen | e.Modifiers;
            else if (Text.EndsWith("...") && (ModifierKeys == Keys.None)) Value = val;
            base.OnKeyUp(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            Value = val;
            base.OnLeave(e);
        }
    }

    // ======================================================================
    // Cửa sổ cài đặt
    // ======================================================================
    internal class SettingsForm : Form
    {
        TrayApp app;
        Settings cfg;
        HotkeyBox hkRegion, hkFull;
        TextBox txtFolder;
        ComboBox cboFormat;
        CheckBox chkAutoSave, chkStartup, chkAdmin, chkNotify, chkMagnifier;

        public SettingsForm(TrayApp app)
        {
            this.app = app;
            cfg = app.Cfg;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            Text = "ScreenSnap - Cài đặt";
            Icon = app.FormIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 540);

            // --- Phím tắt ---
            GroupBox g1 = Group("Phím tắt", 12, 10, 476, 120);
            Lbl(g1, "Chụp vùng chọn (vẽ, chú thích):", 14, 30);
            hkRegion = new HotkeyBox();
            hkRegion.SetBounds(250, 26, 210, 25);
            hkRegion.Value = cfg.HotkeyRegion;
            g1.Controls.Add(hkRegion);
            Lbl(g1, "Chụp toàn màn hình (lưu ngay):", 14, 62);
            hkFull = new HotkeyBox();
            hkFull.SetBounds(250, 58, 210, 25);
            hkFull.Value = cfg.HotkeyFull;
            g1.Controls.Add(hkFull);
            Label hint = Lbl(g1, "Bấm vào ô rồi nhấn tổ hợp phím mới (vd: Ctrl + Alt + A, PrintScreen).\nBackspace = bỏ phím tắt.", 14, 88);
            hint.ForeColor = Color.DimGray;

            // --- Lưu ảnh ---
            GroupBox g2 = Group("Lưu ảnh", 12, 138, 476, 126);
            Lbl(g2, "Thư mục lưu:", 14, 30);
            txtFolder = new TextBox();
            txtFolder.SetBounds(104, 26, 276, 25);
            txtFolder.Text = cfg.SaveFolder;
            g2.Controls.Add(txtFolder);
            Button btnBrowse = new Button();
            btnBrowse.Text = "Chọn...";
            btnBrowse.SetBounds(386, 25, 76, 27);
            btnBrowse.Click += (s, e) => Browse();
            g2.Controls.Add(btnBrowse);
            Lbl(g2, "Định dạng:", 14, 64);
            cboFormat = new ComboBox();
            cboFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFormat.Items.AddRange(new object[] { "PNG (nét, khuyên dùng)", "JPG (dung lượng nhỏ)" });
            cboFormat.SelectedIndex = cfg.Format == "jpg" ? 1 : 0;
            cboFormat.SetBounds(104, 60, 180, 25);
            g2.Controls.Add(cboFormat);
            chkAutoSave = Chk(g2, "Tự động lưu file vào thư mục mỗi khi sao chép (Enter)", 14, 94, cfg.AutoSaveOnCopy);

            // --- Hệ thống ---
            GroupBox g3 = Group("Hệ thống", 12, 272, 476, 210);
            chkStartup = Chk(g3, "Chạy cùng Windows khi khởi động máy", 14, 26, cfg.RunAtStartup);
            chkAdmin = Chk(g3, "Chạy với quyền Administrator", 14, 54, cfg.RunAsAdmin);
            Label ah = Lbl(g3, "Cần bật nếu muốn dùng phím tắt khi đang mở cửa sổ chạy quyền Admin\n(Task Manager, game, phần mềm cài đặt...). Khi bật cùng \"Chạy cùng\nWindows\", ScreenSnap tự khởi động quyền Admin mà không hỏi UAC.", 32, 76);
            ah.ForeColor = Color.DimGray;
            bool isAdmin = Util.IsAdmin();
            Label st = Lbl(g3, "Trạng thái hiện tại: " + (isAdmin ? "đang chạy quyền Administrator ✔" : "đang chạy quyền thường"), 32, 126);
            st.ForeColor = isAdmin ? Color.ForestGreen : Color.DarkOrange;
            chkNotify = Chk(g3, "Hiện thông báo sau khi chụp / lưu", 14, 152, cfg.Notify);
            chkMagnifier = Chk(g3, "Hiện kính lúp phóng to khi chọn vùng", 14, 178, cfg.Magnifier);

            // --- Nút ---
            Button btnFolder = new Button();
            btnFolder.Text = "Mở thư mục ảnh";
            btnFolder.SetBounds(12, 496, 130, 30);
            btnFolder.Click += (s, e) => app.OpenFolder();
            Controls.Add(btnFolder);

            Button btnOK = new Button();
            btnOK.Text = "Lưu";
            btnOK.SetBounds(316, 496, 84, 30);
            btnOK.Click += (s, e) => SaveAndClose();
            Controls.Add(btnOK);

            Button btnCancel = new Button();
            btnCancel.Text = "Hủy";
            btnCancel.SetBounds(406, 496, 82, 30);
            btnCancel.Click += (s, e) => Close();
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            Controls.Add(g1); Controls.Add(g2); Controls.Add(g3);
            ResumeLayout(false);
            PerformLayout();
        }

        GroupBox Group(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.SetBounds(x, y, w, h);
            return g;
        }

        Label Lbl(Control parent, string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        CheckBox Chk(Control parent, string text, int x, int y, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = new Point(x, y);
            c.Checked = value;
            parent.Controls.Add(c);
            return c;
        }

        void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Chọn thư mục lưu ảnh chụp màn hình";
                d.ShowNewFolderButton = true;
                try { if (Directory.Exists(txtFolder.Text)) d.SelectedPath = txtFolder.Text; } catch { }
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            }
        }

        void SaveAndClose()
        {
            if (hkRegion.Value != Keys.None && hkRegion.Value == hkFull.Value)
            {
                MessageBox.Show(this, "Hai phím tắt đang trùng nhau, hãy chọn phím khác.", "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string folder = txtFolder.Text.Trim();
            if (folder.Length == 0) folder = Settings.DefaultFolder();
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Thư mục lưu không hợp lệ: " + ex.Message, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool adminBefore = cfg.RunAsAdmin;
            cfg.HotkeyRegion = hkRegion.Value;
            cfg.HotkeyFull = hkFull.Value;
            cfg.SaveFolder = folder;
            cfg.Format = cboFormat.SelectedIndex == 1 ? "jpg" : "png";
            cfg.AutoSaveOnCopy = chkAutoSave.Checked;
            cfg.RunAtStartup = chkStartup.Checked;
            cfg.RunAsAdmin = chkAdmin.Checked;
            cfg.Notify = chkNotify.Checked;
            cfg.Magnifier = chkMagnifier.Checked;
            cfg.Save();

            string err = Util.ApplyStartup(cfg);
            if (err != null) MessageBox.Show(this, err, "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();

            bool admin = Util.IsAdmin();
            if (cfg.RunAsAdmin && !admin)
            {
                if (MessageBox.Show("Cần khởi động lại ScreenSnap với quyền Administrator.\nKhởi động lại ngay bây giờ?", "ScreenSnap",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    app.RestartElevated();
            }
            else if (!cfg.RunAsAdmin && adminBefore && admin)
            {
                MessageBox.Show("ScreenSnap sẽ chạy quyền thường từ lần mở tiếp theo.", "ScreenSnap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
