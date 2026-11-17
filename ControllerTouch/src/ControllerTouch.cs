// ControllerTouch: an on-screen controller for touch screens (the Legion Go without its controllers attached).
// Games see it as a real Xbox 360 controller (XInput).
//
// How games come to see it: Windows has no way for an ordinary program to add a controller, so this uses the
// ViGEmBus driver (github.com/nefarius/ViGEmBus, installed once by you). The program asks the driver for a virtual
// Xbox 360 pad and sends it the state of the on-screen sticks and buttons. Without the driver it explains what to
// install and exits.
//
// How it sits on the screen: every stick, button group and button is its own small see-through window that stays
// on top and never takes the focus, so the game keeps running underneath and everything between the controls is
// the game's to touch. Several fingers work at once (WM_TOUCH); a mouse works too, one control at a time.
//
// The tab at the top centre hides and shows the pad; the X next to it closes the program.
// Starting it a second time shows the pad again; ControllerTouch.exe --quit closes a running one.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    const string QuitName = @"Local\ControllerTouch.Quit", ShowName = @"Local\ControllerTouch.Show";

    [STAThread]
    static int Main(string[] args)
    {
        bool quit = Array.IndexOf(args, "--quit") >= 0, first;
        using (Mutex m = new Mutex(true, @"Local\ControllerTouch.SingleInstance", out first))
        {
            if (!first)
            {
                // Already running: show its pad again, or close it.
                try { using (EventWaitHandle e = EventWaitHandle.OpenExisting(quit ? QuitName : ShowName)) e.Set(); } catch { }
                return 0;
            }
            if (quit) return 0;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // work in real pixels
            Application.EnableVisualStyles();

            // --preview shows the pad without a driver or a virtual controller (for looking at the layout).
            string problem = Array.IndexOf(args, "--preview") >= 0 ? null : VPad.Connect();
            if (problem != null)
            {
                using (Form owner = new Form()) { owner.TopMost = true; MessageBox.Show(owner, problem, "On-screen controller", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                return 2;
            }
            using (EventWaitHandle quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName))
            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName))
            {
                TouchPad pad = new TouchPad();
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer(); t.Interval = 250;
                t.Tick += delegate
                {
                    if (quitEvent.WaitOne(0)) { Application.Exit(); return; }
                    if (showEvent.WaitOne(0)) pad.SetShown(true);
                    pad.KeepOnTop();
                };
                t.Start();
                try { Application.Run(); }
                finally { VPad.Disconnect(); }
            }
        }
        return 0;
    }
}

// ============================ The virtual Xbox 360 pad (ViGEmBus) ============================
// The driver's interface is a handful of device requests, taken from its public header (ViGEm/km/BusShared.h):
// check the version, plug in a target with a serial number, submit a report, unplug.
static class VPad
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, IntPtr outBuf, int outLen, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid g, string id, int flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List(ref Guid g, string id, char[] buf, int len, int flags);

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER 0x2A, 0x801 + n, METHOD_BUFFERED, FILE_WRITE_DATA)
    const uint IOCTL_PLUGIN = 0x2AA004, IOCTL_UNPLUG = 0x2AA008, IOCTL_CHECK_VERSION = 0x2AA00C, IOCTL_WAIT_READY = 0x2AA010, IOCTL_XUSB_SUBMIT = 0x2AA808;
    public const ushort UP = 0x0001, DOWN = 0x0002, LEFT = 0x0004, RIGHT = 0x0008, START = 0x0010, BACK = 0x0020, LS = 0x0040, RS = 0x0080,
        LB = 0x0100, RB = 0x0200, GUIDE = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    static SafeFileHandle bus;
    static uint serial;
    static ManualResetEvent done;
    static IntPtr overlapped;        // one OVERLAPPED block, reused: every request is made from the window thread
    static byte[] lastReport;

    // What the on-screen controls set; Submit() sends it when it has changed.
    public static ushort Buttons; public static byte LT, RT; public static short LX, LY, RX, RY;

    static bool Request(uint code, byte[] data)
    {
        done.Reset();
        int n;
        if (DeviceIoControl(bus, code, data, data.Length, IntPtr.Zero, 0, out n, overlapped)) return true;
        if (Marshal.GetLastWin32Error() != 997) return false;   // 997 = still in progress
        return GetOverlappedResult(bus, overlapped, out n, true);
    }

    static byte[] Pack(params uint[] values) { byte[] b = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(b, i * 4); return b; }

    // Returns null when the pad is plugged in, or what to tell the user.
    public static string Connect()
    {
        Guid g = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");   // GUID_DEVINTERFACE_BUSENUM_VIGEM
        int len; string path = null;
        if (CM_Get_Device_Interface_List_Size(out len, ref g, null, 0) == 0 && len > 1)
        {
            char[] buf = new char[len];
            if (CM_Get_Device_Interface_List(ref g, null, buf, len, 0) == 0)
                foreach (string s in new string(buf).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)) { path = s; break; }
        }
        if (path == null)
            return "The on-screen controller needs the ViGEmBus driver, which is not installed on this computer.\n\n" +
                   "Download ViGEmBus_1.22.0_x64_x86_arm64.exe from\nhttps://github.com/nefarius/ViGEmBus/releases\nrun it once, then start the on-screen controller again.";
        // GENERIC_READ | GENERIC_WRITE, shared, OPEN_EXISTING, NORMAL | NO_BUFFERING | WRITE_THROUGH | OVERLAPPED (as the driver's own client does)
        bus = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0xE0000080u, IntPtr.Zero);
        if (bus.IsInvalid) return "The ViGEmBus driver is installed but could not be opened (error " + Marshal.GetLastWin32Error() + ").";
        done = new ManualResetEvent(false);
        NativeOverlapped ov = new NativeOverlapped(); ov.EventHandle = done.SafeWaitHandle.DangerousGetHandle();
        overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
        Marshal.StructureToPtr(ov, overlapped, false);

        if (!Request(IOCTL_CHECK_VERSION, Pack(8, 1)))
            return "This version of the ViGEmBus driver is not the one the on-screen controller knows (error " + Marshal.GetLastWin32Error() + ").";
        for (uint s = 1; s <= 16; s++)
        {
            // Size, SerialNo, TargetType (0 = Xbox 360 wired), VendorId 0x045E + ProductId 0x028E (Microsoft's own pad)
            if (Request(IOCTL_PLUGIN, Pack(16, s, 0, 0x028E045E))) { serial = s; break; }
        }
        if (serial == 0) return "The ViGEmBus driver would not add a controller (error " + Marshal.GetLastWin32Error() + ").";
        Request(IOCTL_WAIT_READY, Pack(8, serial));   // older drivers don't know this request; that is fine
        Submit();
        return null;
    }

    public static void Submit()
    {
        if (serial == 0) return;
        byte[] r = new byte[20];   // XUSB_SUBMIT_REPORT: Size, SerialNo, then the XINPUT_GAMEPAD-shaped report
        BitConverter.GetBytes(20u).CopyTo(r, 0); BitConverter.GetBytes(serial).CopyTo(r, 4);
        BitConverter.GetBytes(Buttons).CopyTo(r, 8); r[10] = LT; r[11] = RT;
        BitConverter.GetBytes(LX).CopyTo(r, 12); BitConverter.GetBytes(LY).CopyTo(r, 14); BitConverter.GetBytes(RX).CopyTo(r, 16); BitConverter.GetBytes(RY).CopyTo(r, 18);
        if (lastReport != null) { bool same = true; for (int i = 8; i < 20 && same; i++) same = r[i] == lastReport[i]; if (same) return; }
        lastReport = r;
        Request(IOCTL_XUSB_SUBMIT, r);
    }

    public static void Release() { Buttons = 0; LT = RT = 0; LX = LY = RX = RY = 0; Submit(); }

    public static void Disconnect()
    {
        if (serial != 0) { try { Release(); Request(IOCTL_UNPLUG, Pack(8, serial)); } catch { } serial = 0; }
        if (bus != null && !bus.IsInvalid) bus.Dispose();   // closing the driver also removes the pad
    }
}

// ============================ One see-through piece of the pad ============================
abstract class Piece : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte op, flags, alpha, format; }
    [StructLayout(LayoutKind.Sequential)]
    struct TOUCHINPUT { public int x, y; public IntPtr source; public int id, flags, mask, time; public IntPtr extra; public int cx, cy; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool RegisterTouchWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] static extern bool GetTouchInputInfo(IntPtr input, int count, [Out] TOUCHINPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool CloseTouchInputHandle(IntPtr input);
    [DllImport("user32.dll")] static extern bool SetWindowFeedbackSetting(IntPtr h, int feedback, int flags, int size, ref int value);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);

    public const int T_DOWN = 0, T_MOVE = 1, T_UP = 2, MouseId = -1;
    Bitmap canvas;
    bool mouseHeld;
    protected float U;   // one hundredth of the screen height: every size is given in these

    protected Piece()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x08000000 | 0x8 | 0x80;   // layered, never activated, topmost, kept out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterTouchWindow(Handle, 0);
        int off = 0;   // no ripple, no press-and-hold square: this is a game pad, not a button to right-click
        foreach (int feedback in new int[] { 1, 7, 8, 9, 10, 11 }) SetWindowFeedbackSetting(Handle, feedback, 0, 4, ref off);
    }

    public void Place(Rectangle r, float u) { U = u; Bounds = r; canvas = null; Redraw(); }

    public void KeepOnTop() { if (Visible) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }   // topmost; no size, move or activate

    // Draw the piece and hand the picture, with its transparency, to Windows.
    public void Redraw()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        if (canvas == null || canvas.Width != Width || canvas.Height != Height) { if (canvas != null) canvas.Dispose(); canvas = new Bitmap(Width, Height, PixelFormat.Format32bppArgb); }
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g);
        }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = canvas.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, bmp);
        try
        {
            POINT at = new POINT(), from = new POINT(); at.x = Left; at.y = Top;
            SIZE size = new SIZE(); size.cx = Width; size.cy = Height;
            BLENDFUNCTION blend = new BLENDFUNCTION(); blend.alpha = 255; blend.format = 1;   // use each pixel's own transparency
            UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2);
        }
        finally { SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }

    protected abstract void Draw(Graphics g);
    protected abstract void Touch(int id, int kind, PointF p);   // p is inside this piece, in pixels
    public virtual void LetGo() { }                              // forget every finger (the pad was hidden)

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0021: m.Result = new IntPtr(3); return;   // WM_MOUSEACTIVATE: never take the focus from the game
            case 0x0240:                                     // WM_TOUCH
                {
                    int count = (int)(m.WParam.ToInt64() & 0xFFFF);
                    TOUCHINPUT[] t = new TOUCHINPUT[count];
                    if (GetTouchInputInfo(m.LParam, count, t, Marshal.SizeOf(typeof(TOUCHINPUT))))
                    {
                        foreach (TOUCHINPUT c in t)
                        {
                            PointF p = new PointF(c.x / 100f - Left, c.y / 100f - Top);   // touch positions come in hundredths of a pixel
                            if ((c.flags & 0x0002) != 0) Touch(c.id, T_DOWN, p);
                            else if ((c.flags & 0x0004) != 0) Touch(c.id, T_UP, p);
                            else if ((c.flags & 0x0001) != 0) Touch(c.id, T_MOVE, p);
                        }
                        CloseTouchInputHandle(m.LParam);
                    }
                    m.Result = IntPtr.Zero; return;
                }
            case 0x0201: case 0x0200: case 0x0202:           // left button down, mouse move, left button up
                {
                    // Windows also reports the first finger as a mouse; that copy carries this stamp and is ignored.
                    if ((GetMessageExtraInfo().ToInt64() & 0xFFFFFF00L) == 0xFF515700L) return;
                    long l = m.LParam.ToInt64(); PointF p = new PointF((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                    if (m.Msg == 0x0201) { mouseHeld = true; SetCapture(Handle); Touch(MouseId, T_DOWN, p); }
                    else if (m.Msg == 0x0202) { if (mouseHeld) { mouseHeld = false; ReleaseCapture(); Touch(MouseId, T_UP, p); } }
                    else if (mouseHeld) Touch(MouseId, T_MOVE, p);
                    return;
                }
        }
        base.WndProc(ref m);
    }

    // ---- drawing helpers shared by the pieces ----
    protected static Color Ink(int alpha) { return Color.FromArgb(alpha, 255, 255, 255); }
    protected static void Disc(Graphics g, float x, float y, float r, Color fill, Color line, float width)
    {
        using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        if (width > 0) using (Pen p = new Pen(line, width)) g.DrawEllipse(p, x - r, y - r, r * 2, r * 2);
    }
    protected static GraphicsPath Pill(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    protected static void Label(Graphics g, string text, float size, RectangleF box, Color color)
    {
        using (Font f = new Font("Segoe UI", Math.Max(6f, size), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush b = new SolidBrush(color))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center; sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            g.DrawString(text, f, b, box, sf);
        }
    }
}

// ============================ Analogue stick ============================
class StickPiece : Piece
{
    readonly bool right;
    int finger = int.MinValue;   // the finger that is steering, if any
    PointF knob;                 // offset of the knob from the centre, in pixels

    public StickPiece(bool right) { this.right = right; }
    float Reach { get { return Width * 0.30f; } }   // how far the knob travels for full tilt

    protected override void Touch(int id, int kind, PointF p)
    {
        if (kind == T_DOWN && finger == int.MinValue) finger = id;
        if (id != finger) return;
        if (kind == T_UP) { finger = int.MinValue; knob = PointF.Empty; }
        else
        {
            float dx = p.X - Width / 2f, dy = p.Y - Height / 2f, d = (float)Math.Sqrt(dx * dx + dy * dy), reach = Reach;
            if (d > reach) { dx *= reach / d; dy *= reach / d; }
            knob = new PointF(dx, dy);
        }
        Send(); Redraw();
    }

    public override void LetGo() { finger = int.MinValue; knob = PointF.Empty; Send(); }

    void Send()
    {
        short x = (short)Math.Round(knob.X / Reach * 32767f), y = (short)Math.Round(-knob.Y / Reach * 32767f);   // up is positive for games
        if (right) { VPad.RX = x; VPad.RY = y; } else { VPad.LX = x; VPad.LY = y; }
        VPad.Submit();
    }

    protected override void Draw(Graphics g)
    {
        float c = Width / 2f, held = finger != int.MinValue ? 1f : 0f;
        Disc(g, c, c, Width * 0.47f, Ink(38), Ink(120), U * 0.35f);                                     // the well: the whole of it takes a finger
        Disc(g, c + knob.X, c + knob.Y, Width * 0.19f, Ink(held > 0 ? 190 : 110), Ink(220), U * 0.3f);  // the knob
        Label(g, right ? "R" : "L", U * 3.2f, new RectangleF(c + knob.X - U * 4, c + knob.Y - U * 4, U * 8, U * 8), Color.FromArgb(held > 0 ? 220 : 150, 20, 24, 32));
    }
}

// ============================ A group of round buttons: the D-pad or A B X Y ============================
class ClusterPiece : Piece
{
    readonly string[] labels; readonly ushort[] bits; readonly Color[] tints; readonly bool dpad;
    readonly Dictionary<int, int> fingers = new Dictionary<int, int>();   // finger -> the buttons it is holding (as bits of the list)

    // Order is always: up, right, down, left (for A B X Y that is Y, B, A, X).
    public ClusterPiece(bool dpad)
    {
        this.dpad = dpad;
        if (dpad)
        {
            labels = new string[] { "", "", "", "" };   // the arrows are drawn
            bits = new ushort[] { VPad.UP, VPad.RIGHT, VPad.DOWN, VPad.LEFT };
            tints = new Color[] { Color.White, Color.White, Color.White, Color.White };
        }
        else
        {
            labels = new string[] { "Y", "B", "A", "X" };
            bits = new ushort[] { VPad.Y, VPad.B, VPad.A, VPad.X };
            tints = new Color[] { Color.FromArgb(255, 214, 80), Color.FromArgb(255, 110, 110), Color.FromArgb(120, 230, 140), Color.FromArgb(110, 170, 255) };
        }
    }

    PointF Spot(int i) { float c = Width / 2f, off = Width * 0.30f; return new PointF(c + (i == 1 ? off : i == 3 ? -off : 0), c + (i == 2 ? off : i == 0 ? -off : 0)); }
    float Dot { get { return Width * 0.165f; } }   // radius of one button

    int Under(PointF p)
    {
        float dx = p.X - Width / 2f, dy = p.Y - Height / 2f, d = (float)Math.Sqrt(dx * dx + dy * dy);
        if (dpad)
        {
            // By direction from the middle, so a thumb can roll round it; diagonals press two.
            if (d < Width * 0.09f) return 0;
            double a = Math.Atan2(dx, -dy) * 180 / Math.PI; if (a < 0) a += 360;   // 0 = up, clockwise
            int sector = (int)Math.Round(a / 45.0) % 8;
            int[] masks = { 1, 1 | 2, 2, 2 | 4, 4, 4 | 8, 8, 8 | 1 };
            return masks[sector];
        }
        // A B X Y: the button the finger is on (a little generous), or none.
        int best = 0; float bestD = Dot * 1.45f;
        for (int i = 0; i < 4; i++) { PointF s = Spot(i); float bd = (float)Math.Sqrt((p.X - s.X) * (p.X - s.X) + (p.Y - s.Y) * (p.Y - s.Y)); if (bd < bestD) { bestD = bd; best = 1 << i; } }
        return best;
    }

    int Held() { int all = 0; foreach (int v in fingers.Values) all |= v; return all; }

    protected override void Touch(int id, int kind, PointF p)
    {
        int before = Held();
        if (kind == T_UP) fingers.Remove(id);
        else if (kind == T_DOWN || fingers.ContainsKey(id)) fingers[id] = Under(p);
        if (Held() != before) { Send(); Redraw(); }
    }

    public override void LetGo() { fingers.Clear(); Send(); }

    void Send()
    {
        int held = Held(); ushort all = 0, mine = 0;
        for (int i = 0; i < 4; i++) { mine |= bits[i]; if ((held & (1 << i)) != 0) all |= bits[i]; }
        VPad.Buttons = (ushort)((VPad.Buttons & ~mine) | all);
        VPad.Submit();
    }

    protected override void Draw(Graphics g)
    {
        int held = Held();
        if (dpad) Disc(g, Width / 2f, Height / 2f, Width * 0.47f, Ink(30), Ink(90), U * 0.3f);   // one disc: the gaps between the arrows count too
        for (int i = 0; i < 4; i++)
        {
            PointF s = Spot(i); bool on = (held & (1 << i)) != 0; Color t = tints[i];
            Disc(g, s.X, s.Y, Dot, Color.FromArgb(on ? 200 : (dpad ? 70 : 85), t), Color.FromArgb(on ? 255 : 170, t), U * 0.3f);
            Color ink = Color.FromArgb(on ? 255 : 215, on ? Color.FromArgb(20, 24, 32) : Color.White);
            if (!dpad) { Label(g, labels[i], Dot * 1.15f, new RectangleF(s.X - Dot, s.Y - Dot, Dot * 2, Dot * 2), ink); continue; }
            float a = Dot * 0.5f; PointF tip, l, r;   // a small triangle pointing outwards
            if (i == 0) { tip = new PointF(s.X, s.Y - a); l = new PointF(s.X - a, s.Y + a * 0.6f); r = new PointF(s.X + a, s.Y + a * 0.6f); }
            else if (i == 1) { tip = new PointF(s.X + a, s.Y); l = new PointF(s.X - a * 0.6f, s.Y - a); r = new PointF(s.X - a * 0.6f, s.Y + a); }
            else if (i == 2) { tip = new PointF(s.X, s.Y + a); l = new PointF(s.X - a, s.Y - a * 0.6f); r = new PointF(s.X + a, s.Y - a * 0.6f); }
            else { tip = new PointF(s.X - a, s.Y); l = new PointF(s.X + a * 0.6f, s.Y - a); r = new PointF(s.X + a * 0.6f, s.Y + a); }
            using (SolidBrush b = new SolidBrush(ink)) g.FillPolygon(b, new PointF[] { tip, l, r });
        }
    }
}

// ============================ A single button: shoulders, triggers, Back, Start, Home, stick clicks ============================
class ButtonPiece : Piece
{
    readonly string label; readonly ushort bit; readonly int trigger;   // trigger: 1 = left, 2 = right, 0 = an ordinary button
    readonly HashSet<int> fingers = new HashSet<int>();
    public Action Tapped;   // for buttons that do something in this program rather than on the pad

    public ButtonPiece(string label, ushort bit, int trigger) { this.label = label; this.bit = bit; this.trigger = trigger; }

    protected override void Touch(int id, int kind, PointF p)
    {
        bool was = fingers.Count > 0;
        if (kind == T_DOWN) fingers.Add(id);
        else if (kind == T_UP && fingers.Remove(id) && fingers.Count == 0 && Tapped != null && ClientRectangle.Contains(Point.Round(p))) { Redraw(); Tapped(); return; }
        if ((fingers.Count > 0) != was) { Send(); Redraw(); }
    }

    public override void LetGo() { fingers.Clear(); Send(); }

    void Send()
    {
        bool on = fingers.Count > 0;
        if (trigger == 1) VPad.LT = (byte)(on ? 255 : 0);
        else if (trigger == 2) VPad.RT = (byte)(on ? 255 : 0);
        else if (bit != 0) VPad.Buttons = (ushort)(on ? VPad.Buttons | bit : VPad.Buttons & ~bit);
        VPad.Submit();
    }

    protected override void Draw(Graphics g)
    {
        bool on = fingers.Count > 0; float w = U * 0.3f;
        RectangleF r = new RectangleF(w, w, Width - 2 * w, Height - 2 * w);
        using (GraphicsPath path = Pill(r, Height * 0.42f))
        using (SolidBrush fill = new SolidBrush(Ink(on ? 200 : 60)))
        using (Pen line = new Pen(Ink(on ? 255 : 160), w))
        { g.FillPath(fill, path); g.DrawPath(line, path); }
        // As tall as the button allows, but narrow enough for the whole word (a letter is roughly 0.62 of its height wide).
        Label(g, label, Math.Min(Height * 0.46f, Width * 0.78f / (Math.Max(1, label.Length) * 0.62f)), r, on ? Color.FromArgb(255, 20, 24, 32) : Ink(225));
    }
}

// ============================ The whole pad ============================
class TouchPad
{
    readonly List<Piece> pieces = new List<Piece>();     // everything that hides with the pad
    readonly List<Rectangle> spots = new List<Rectangle>();
    readonly ButtonPiece tab, close;
    bool shown = true;

    public TouchPad()
    {
        pieces.Add(new StickPiece(false));                    // 0 left stick
        pieces.Add(new ClusterPiece(true));                   // 1 D-pad
        pieces.Add(new ClusterPiece(false));                  // 2 A B X Y
        pieces.Add(new StickPiece(true));                     // 3 right stick
        pieces.Add(new ButtonPiece("LT", 0, 1));              // 4
        pieces.Add(new ButtonPiece("LB", VPad.LB, 0));        // 5
        pieces.Add(new ButtonPiece("RT", 0, 2));              // 6
        pieces.Add(new ButtonPiece("RB", VPad.RB, 0));        // 7
        pieces.Add(new ButtonPiece("Back", VPad.BACK, 0));    // 8
        pieces.Add(new ButtonPiece("Home", VPad.GUIDE, 0));   // 9
        pieces.Add(new ButtonPiece("Start", VPad.START, 0));  // 10
        pieces.Add(new ButtonPiece("L3", VPad.LS, 0));        // 11 left stick click
        pieces.Add(new ButtonPiece("R3", VPad.RS, 0));        // 12 right stick click
        tab = new ButtonPiece("Pad", 0, 0); tab.Tapped = delegate { SetShown(!shown); };
        close = new ButtonPiece("X", 0, 0); close.Tapped = delegate { Application.Exit(); };
        SystemEvents.DisplaySettingsChanged += delegate { Layout(); };
        Layout();
        tab.Show(); close.Show();
        foreach (Piece p in pieces) p.Show();
        Layout();   // the windows exist now: draw them
    }

    // Everything is placed in hundredths of the screen height (u), measured in from the screen's edges, so it keeps
    // its shape on the Legion Go's own screen and on a TV: sticks and buttons low at the sides under the thumbs,
    // shoulders above them, the small buttons along the bottom, the hide tab at the top.
    void Layout()
    {
        Rectangle s = Screen.PrimaryScreen.Bounds; float u = s.Height / 100f;
        // The taskbar stays in front of everything, so the controls sit above where it is (or would be).
        float lift = Math.Max(0, s.Bottom - Screen.PrimaryScreen.WorkingArea.Bottom) / u;
        Func<float, float, float, float, Rectangle> at = delegate(float x, float y, float w, float h)
        {
            if (y > 20) y -= lift;   // everything but the tab at the top
            return new Rectangle(s.Left + (int)Math.Round(x * u), s.Top + (int)Math.Round(y * u), (int)Math.Round(w * u), (int)Math.Round(h * u));
        };
        float W = s.Width / u;   // screen width in u (160 on a 16:10 screen)
        Rectangle[] r =
        {
            at(3, 58, 30, 30),               // left stick
            at(31, 72, 25, 25),              // D-pad, inward of the stick
            at(W - 34, 56, 32, 32),          // A B X Y
            at(W - 57, 72, 25, 25),          // right stick, inward of the buttons
            at(3, 30, 20, 10),               // LT
            at(3, 42, 20, 10),               // LB
            at(W - 23, 30, 20, 10),          // RT
            at(W - 23, 42, 20, 10),          // RB
            at(W / 2 - 17, 90, 10, 7),       // View
            at(W / 2 - 5, 90, 10, 7),        // Home
            at(W / 2 + 7, 90, 10, 7),        // Menu
            at(34, 60, 10, 8),               // L3
            at(W - 44, 60, 10, 8)            // R3
        };
        for (int i = 0; i < pieces.Count; i++) pieces[i].Place(r[i], u);
        tab.Place(at(W / 2 - 7, 0.6f, 9, 5.5f), u);
        close.Place(at(W / 2 + 3, 0.6f, 6, 5.5f), u);
    }

    public void SetShown(bool show)
    {
        if (show == shown) return;
        shown = show;
        foreach (Piece p in pieces) { if (show) { p.Show(); p.Redraw(); } else { p.LetGo(); p.Hide(); } }
        if (show) close.Show(); else close.Hide();
        if (!show) VPad.Release();   // nothing stays held while the pad is out of sight
    }

    // A game that goes full screen can slip in front; put the pad back on top now and then.
    public void KeepOnTop() { tab.KeepOnTop(); close.KeepOnTop(); foreach (Piece p in pieces) p.KeepOnTop(); }
}
