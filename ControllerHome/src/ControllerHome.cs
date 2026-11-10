// ControllerHome: press the Home/Guide button on any non-Xbox controller to open Xbox Game Bar (Win+G).
// Xbox-type controllers are skipped, because Windows already opens Game Bar from their Xbox button.
// Runs in the background with no window. Uses SDL3 (SDL3.dll next to the exe) to read controllers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class ControllerHome
{
    const string Sdl = "SDL3.dll";
    const uint SDL_INIT_GAMEPAD = 0x2000;
    const uint EV_GAMEPAD_BUTTON_DOWN = 0x651;
    const uint EV_GAMEPAD_ADDED = 0x653;
    const uint EV_GAMEPAD_REMOVED = 0x654;
    const byte BUTTON_GUIDE = 5;
    const int TYPE_XBOX360 = 2, TYPE_XBOXONE = 3;

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

    static void OpenGameBar()
    {
        INPUT[] seq = { Key(0x5B, false), Key(0x47, false), Key(0x47, true), Key(0x5B, true) }; // Win down, G down, G up, Win up
        uint sent = SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
        Log(sent == seq.Length ? "Home pressed: sent Win+G" : "Home pressed: SendInput failed, error " + Marshal.GetLastWin32Error());
    }

    static int Main()
    {
        bool first;
        using (Mutex m = new Mutex(true, @"Local\ControllerHome.SingleInstance", out first))
        {
            if (!first) return 0;

            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (!SDL_Init(SDL_INIT_GAMEPAD))
            {
                Log("SDL_Init failed: " + Str(SDL_GetError()));
                return 1;
            }
            Log("Started");

            Dictionary<uint, IntPtr> pads = new Dictionary<uint, IntPtr>();
            byte[] ev = new byte[128];
            DateTime last = DateTime.MinValue;

            while (true)
            {
                if (!SDL_WaitEventTimeout(ev, 250)) continue;
                uint type = BitConverter.ToUInt32(ev, 0);
                uint id = BitConverter.ToUInt32(ev, 16);

                if (type == EV_GAMEPAD_ADDED)
                {
                    IntPtr pad = SDL_OpenGamepad(id);
                    if (pad == IntPtr.Zero) { Log("Could not open controller " + id + ": " + Str(SDL_GetError())); continue; }
                    int kind = SDL_GetGamepadType(pad);
                    string name = Str(SDL_GetGamepadName(pad));
                    if (kind == TYPE_XBOX360 || kind == TYPE_XBOXONE)
                    {
                        Log("Skipping Xbox-type controller (Windows handles its Xbox button): " + name);
                        SDL_CloseGamepad(pad);
                    }
                    else
                    {
                        pads[id] = pad;
                        Log("Watching Home button on: " + name + " (type " + kind + ")");
                    }
                }
                else if (type == EV_GAMEPAD_REMOVED)
                {
                    IntPtr pad;
                    if (pads.TryGetValue(id, out pad)) { SDL_CloseGamepad(pad); pads.Remove(id); Log("Controller disconnected"); }
                }
                else if (type == EV_GAMEPAD_BUTTON_DOWN && ev[20] == BUTTON_GUIDE && pads.ContainsKey(id))
                {
                    if ((DateTime.Now - last).TotalMilliseconds > 400) { last = DateTime.Now; OpenGameBar(); }
                }
            }
        }
    }
}
