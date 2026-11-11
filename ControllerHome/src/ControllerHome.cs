// ControllerHome: press the Home/Guide button on any non-Xbox controller to open Xbox Game Bar.
// Xbox-type controllers are skipped, because Windows already opens Game Bar from their Xbox button.
// Runs in the background with no window. Uses SDL3 (SDL3.dll next to the exe) to read controllers.
//
// Game Bar is opened by starting its app, not by simulating Win+G: Windows silently drops simulated key presses
// while a window that runs as administrator is in front (some games do, for their anti-cheat), so Win+G only
// worked on the desktop. For the same reason the Capture button opens Lenovo's Quick Settings directly when such
// a window is in front; everywhere else Steam's own Capture mapping (Ctrl+Alt+F9) still does that.
//
// When ControllerHome itself runs as administrator (install-admin.ps1), it also lets the controller drive Game Bar
// and Quick Settings inside administrator games: D-pad or left stick moves, A selects, B closes.
//
// Switches: --gamebar and --quicksettings do that one action and exit (for shortcuts and testing).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class ControllerHome
{
    const string Sdl = "SDL3.dll";
    const uint SDL_INIT_GAMEPAD = 0x2000;
    const uint EV_GAMEPAD_AXIS = 0x650;
    const uint EV_GAMEPAD_BUTTON_DOWN = 0x651;
    const uint EV_GAMEPAD_ADDED = 0x653;
    const uint EV_GAMEPAD_REMOVED = 0x654;
    const byte BUTTON_GUIDE = 5;
    const byte BUTTON_CAPTURE = 15;   // SDL "misc1": Capture on a Switch Pro Controller, Share on others
    const int TYPE_XBOX360 = 2, TYPE_XBOXONE = 3;
    const string GameBarApp = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe!App";
    const string QuickSettingsArgs = "{\"businessName\":\"ShowOrHide\",\"businessValue\":3,\"msgType\":2}";

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool SDL_SetHint(string name, string value);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool SDL_Init(uint flags);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool SDL_WaitEventTimeout(byte[] ev, int timeoutMs);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    static extern void SDL_CloseGamepad(IntPtr pad);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    static extern int SDL_GetGamepadType(IntPtr pad);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SDL_GetGamepadName(IntPtr pad);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SDL_GetError();

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort vk;
        [FieldOffset(10)] public ushort scan;
        [FieldOffset(12)] public uint flags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr extra;
    }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

    static void Log(string msg)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200000) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    static string Str(IntPtr p) { return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p); }

    static INPUT Key(ushort vk, bool up)
    {
        INPUT i = new INPUT();
        i.type = 1; // keyboard
        i.vk = vk;
        i.flags = (vk == 0x5B ? 1u : 0u) | (up ? 2u : 0u); // extended key for Windows key; key-up flag
        i.extra = new IntPtr(0x434D4544); // marker so Controller Media lets this through its simulated-input filter
        return i;
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    // True when the window in front belongs to a program running as administrator (or one we may not inspect at
    // all). Simulated key presses from ordinary programs, ours and Steam's, don't reach those.
    static bool FrontIsElevated()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid == 0) return false;
        IntPtr p = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (p == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;   // access denied
        try
        {
            IntPtr token;
            if (!OpenProcessToken(p, 8, out token)) return Marshal.GetLastWin32Error() == 5;   // TOKEN_QUERY
            int elevated, len;
            bool ok = GetTokenInformation(token, 20, out elevated, 4, out len);                // TokenElevation
            CloseHandle(token);
            return ok && elevated != 0;
        }
        finally { CloseHandle(p); }
    }

    // Whether ControllerHome itself runs as administrator (see install-admin.ps1). Only then can it press keys
    // for you while an administrator game is in front.
    static readonly bool Admin = IsAdmin();
    static bool IsAdmin()
    {
        try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    static void Send(params INPUT[] seq) { SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT))); }

    static void Press(ushort vk)
    {
        INPUT d = Key(vk, false), u = Key(vk, true);
        d.scan = u.scan = (ushort)MapVirtualKey(vk, 0);
        if (vk >= 0x21 && vk <= 0x28) { d.flags |= 1; u.flags |= 1; }   // arrows are extended keys
        Send(d, u);
    }

    static void PressWinG() { Send(Key(0x5B, false), Key(0x47, false), Key(0x47, true), Key(0x5B, true)); }

    // ---- Driving an overlay with the controller in administrator games ----
    // On the desktop Steam turns the controller into keys and a mouse, so Game Bar and Lenovo's panel can be used.
    // Inside a game it doesn't, and in an administrator game its key presses would be dropped anyway. So after
    // ControllerHome opens one of them there, it presses the keys itself: D-pad or left stick = arrows, A = Enter,
    // LB/RB = previous/next item, B (or the same button again) = close. The game still sees the controller too.
    const int OV_NONE = 0, OV_GAMEBAR = 1, OV_QUICK = 2;
    static int overlay = OV_NONE;
    static DateTime overlayUsed;
    static int stickX, stickY;

    static bool CanDriveHere() { return Admin && FrontIsElevated(); }

    static void StartDriving(int which)
    {
        overlay = which; overlayUsed = DateTime.Now; stickX = stickY = 0;
        Log("Controller now drives " + (which == OV_GAMEBAR ? "Game Bar" : "Quick Settings") + " (D-pad, A, B to close)");
    }

    static void CloseOverlay(string why)
    {
        if (overlay == OV_GAMEBAR) PressWinG();
        else if (overlay == OV_QUICK) ToggleQuickSettings(why);
        Log(why + ": closed " + (overlay == OV_GAMEBAR ? "Game Bar" : "Quick Settings"));
        overlay = OV_NONE;
    }

    // Returns true when the press was used for the overlay.
    static bool DriveButton(byte button, bool nintendoLayout)
    {
        if (overlay == OV_NONE) return false;
        if (!CanDriveHere()) { overlay = OV_NONE; return false; }
        overlayUsed = DateTime.Now;
        byte confirm = (byte)(nintendoLayout ? 1 : 0), back = (byte)(nintendoLayout ? 0 : 1);   // follow the printed A and B
        if (button == confirm) Press(0x0D);
        else if (button == back) CloseOverlay("B pressed");
        else if (button == 11) Press(0x26);
        else if (button == 12) Press(0x28);
        else if (button == 13) Press(0x25);
        else if (button == 14) Press(0x27);
        else if (button == 10) Press(0x09);                                                       // RB: next item (Tab)
        else if (button == 9) Send(Key(0x10, false), Key(0x09, false), Key(0x09, true), Key(0x10, true));   // LB: previous
        else return false;
        return true;
    }

    static void DriveStick(byte axis, short value)
    {
        if (overlay == OV_NONE || axis > 1) return;
        int dir = value > 20000 ? 1 : value < -20000 ? -1 : (value > -12000 && value < 12000) ? 0 : (axis == 0 ? stickX : stickY);
        int was = axis == 0 ? stickX : stickY;
        if (axis == 0) stickX = dir; else stickY = dir;
        if (dir == 0 || dir == was || !CanDriveHere()) return;
        overlayUsed = DateTime.Now;
        Press(axis == 0 ? (ushort)(dir > 0 ? 0x27 : 0x25) : (ushort)(dir > 0 ? 0x28 : 0x26));
    }

    static void OpenGameBar(string why)
    {
        try
        {
            uint pid;
            int hr = ((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(GameBarApp, null, 0, out pid);
            if (hr >= 0) { Log(why + ": opened Game Bar"); return; }
            Log(why + ": could not start Game Bar (0x" + hr.ToString("X8") + "), sending Win+G instead");
        }
        catch (Exception ex) { Log(why + ": could not start Game Bar (" + ex.Message + "), sending Win+G instead"); }

        PressWinG();
    }

    // Lenovo's Quick Settings panel (Legion Go). The program sits in a folder named after its version, so take
    // the location from the copy that is already running, or else the newest one installed.
    static string QuickSettingsExe()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("LegionSettingMenu"))
                try { return p.MainModule.FileName; } catch { }
        }
        catch { }
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Lenovo\LegionSpace");
            string best = null;
            foreach (string f in Directory.GetFiles(root, "LegionSettingMenu.exe", SearchOption.AllDirectories))
                if (best == null || File.GetLastWriteTimeUtc(f) > File.GetLastWriteTimeUtc(best)) best = f;
            return best;
        }
        catch { return null; }
    }

    static void ToggleQuickSettings(string why)
    {
        string exe = QuickSettingsExe();
        if (exe == null) { Log(why + ": Lenovo Quick Settings is not installed"); return; }
        try
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, QuickSettingsArgs);
            si.UseShellExecute = false; si.WorkingDirectory = Path.GetDirectoryName(exe);
            using (Process.Start(si)) { }
            Log(why + ": toggled Lenovo Quick Settings");
        }
        catch (Exception ex) { Log(why + ": could not open Lenovo Quick Settings (" + ex.Message + ")"); }
    }

    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0] == "--gamebar") OpenGameBar("Command");
            else if (args[0] == "--quicksettings") ToggleQuickSettings("Command");
            return 0;
        }

        bool first;
        Mutex m;
        try { m = new Mutex(true, @"Local\ControllerHome.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { return 0; }   // a copy running as administrator already has it
        using (m)
        {
            if (!first) return 0;

            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (!SDL_Init(SDL_INIT_GAMEPAD))
            {
                Log("SDL_Init failed: " + Str(SDL_GetError()));
                return 1;
            }
            Log(Admin ? "Started (as administrator)" : "Started");

            Dictionary<uint, IntPtr> pads = new Dictionary<uint, IntPtr>();
            Dictionary<uint, int> kinds = new Dictionary<uint, int>();
            Dictionary<uint, int> tries = new Dictionary<uint, int>();
            List<uint> retry = new List<uint>();
            DateTime retryAt = DateTime.MinValue;
            byte[] ev = new byte[128];
            DateTime last = DateTime.MinValue, lastCapture = DateTime.MinValue;

            while (true)
            {
                bool got = SDL_WaitEventTimeout(ev, 250);
                if (overlay != OV_NONE && (DateTime.Now - overlayUsed).TotalSeconds > 60) { overlay = OV_NONE; Log("Controller no longer drives the overlay (not used for a minute)"); }
                if (!got)
                {
                    // A controller that could not be opened (it was busy starting up) gets another try every 2 seconds.
                    if (retry.Count > 0 && DateTime.Now >= retryAt)
                    {
                        uint rid = retry[0]; retry.RemoveAt(0);
                        ev = new byte[128];
                        BitConverter.GetBytes(EV_GAMEPAD_ADDED).CopyTo(ev, 0); BitConverter.GetBytes(rid).CopyTo(ev, 16);
                    }
                    else continue;
                }
                uint type = BitConverter.ToUInt32(ev, 0);
                uint id = BitConverter.ToUInt32(ev, 16);

                if (type == EV_GAMEPAD_ADDED)
                {
                    if (pads.ContainsKey(id)) continue;
                    IntPtr pad = SDL_OpenGamepad(id);
                    if (pad == IntPtr.Zero)
                    {
                        int n; tries.TryGetValue(id, out n); tries[id] = n + 1;
                        if (n == 0) Log("Could not open controller " + id + " yet (" + Str(SDL_GetError()) + "); trying again");
                        if (!retry.Contains(id)) { retry.Add(id); retryAt = DateTime.Now.AddSeconds(n < 30 ? 2 : 10); }
                        continue;
                    }
                    tries.Remove(id);
                    int kind = SDL_GetGamepadType(pad);
                    string name = Str(SDL_GetGamepadName(pad));
                    if (kind == TYPE_XBOX360 || kind == TYPE_XBOXONE)
                    {
                        Log("Skipping Xbox-type controller (Windows handles its Xbox button): " + name);
                        SDL_CloseGamepad(pad);
                    }
                    else
                    {
                        pads[id] = pad; kinds[id] = kind;
                        Log("Watching Home button on: " + name + " (type " + kind + ")");
                    }
                }
                else if (type == EV_GAMEPAD_REMOVED)
                {
                    IntPtr pad;
                    retry.Remove(id); tries.Remove(id);
                    if (pads.TryGetValue(id, out pad)) { SDL_CloseGamepad(pad); pads.Remove(id); kinds.Remove(id); Log("Controller disconnected"); }
                }
                else if (type == EV_GAMEPAD_BUTTON_DOWN && ev[20] == BUTTON_GUIDE && pads.ContainsKey(id))
                {
                    if ((DateTime.Now - last).TotalMilliseconds <= 400) continue;
                    last = DateTime.Now;
                    if (overlay == OV_GAMEBAR && CanDriveHere()) { CloseOverlay("Home pressed"); continue; }
                    OpenGameBar("Home pressed");
                    if (CanDriveHere()) StartDriving(OV_GAMEBAR); else overlay = OV_NONE;
                }
                else if (type == EV_GAMEPAD_BUTTON_DOWN && ev[20] == BUTTON_CAPTURE && pads.ContainsKey(id))
                {
                    // Only when Steam's key press can't get through; otherwise both would fire and cancel out.
                    if ((DateTime.Now - lastCapture).TotalMilliseconds <= 400 || !FrontIsElevated()) continue;
                    lastCapture = DateTime.Now;
                    if (overlay == OV_QUICK) { CloseOverlay("Capture pressed"); continue; }
                    ToggleQuickSettings("Capture pressed in an administrator game");
                    if (CanDriveHere()) StartDriving(OV_QUICK); else overlay = OV_NONE;
                }
                else if (type == EV_GAMEPAD_BUTTON_DOWN && pads.ContainsKey(id))
                {
                    int kind; kinds.TryGetValue(id, out kind);
                    DriveButton(ev[20], kind >= 7 && kind <= 10);   // Switch Pro Controller and Joy-Cons: A is on the right
                }
                else if (type == EV_GAMEPAD_AXIS && pads.ContainsKey(id))
                {
                    DriveStick(ev[20], BitConverter.ToInt16(ev, 24));
                }
            }
        }
    }
}
