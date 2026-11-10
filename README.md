# Legion Go Media Hub

<img src="assets/logo.png" width="128" alt="Legion Go Media Hub logo">

A lightweight media centre for the Lenovo Legion Go (and other Windows handhelds and tablets) that is driven
entirely by a game controller or the touchscreen. It has two small programs:

| Program | What it does |
|---|---|
| **Controller Media** | Full-screen media hub: Videos, Music, Pictures, a built-in web browser and YouTube's TV interface |
| **ControllerHome** | Tiny background helper: the Home button on a non-Xbox controller opens Xbox Game Bar |

## Controller Media

- **Two interfaces, switched automatically.** Press a controller button and you get the controller interface
  (focus ring, button hints, no touch buttons). Touch the screen and you get the touch interface (tap targets,
  Back/Home buttons, on-screen player controls).
- **Any controller SDL recognises**: XInput, DirectInput, PlayStation, Switch Pro and most third-party pads.
- **Local media** from your Videos, Music, Pictures and Downloads folders and the SD card (`D:\`).
- **Built-in browser** (WebView2):
  - Controller mode: left stick moves the pointer, A clicks, right stick scrolls, Y opens an on-screen keyboard,
    Start opens the browser menu. The page is full-screen.
  - Touch mode: a tablet-style bar with back, forward, reload, address bar, bookmark star, bookmarks, menu and exit.
- **YouTube TV interface** (`youtube.com/tv`), with swipes translated to arrow keys in touch mode.
- **Bookmarks**, shown as tiles on the Web page.
- **Ad blocker** with an on/off switch: blocks a built-in list of ad and tracker domains, hides leftover ad boxes,
  and strips or skips YouTube ads. YouTube changes often, so the YouTube part (`ui/adblock.js`) may need updating.
- **Simulated-input filter**: while the app is in front, it drops simulated mouse and keyboard input from other
  programs (for example Steam's desktop controller mode), so controller presses don't count twice. Touch, real
  keyboards and real mice are unaffected.
- **Light on resources**: the browser view is created only when a web page is opened and unloaded on exit.
  Measured on a Legion Go: about 210 MB of memory on the home screen and under 0.3% CPU when idle.

### Controls (controller)

| Where | Buttons |
|---|---|
| Menus | D-pad / left stick: move · A: select · B: back · −: home · X: play/pause music · LB/RB: track · Y: search the web |
| Video | A: play/pause · ◀ ▶: 10 s · LT/RT: 1 min · ▲ ▼: volume · LB/RB: previous/next · B: close |
| Browser | Left stick: pointer · A: click · right stick: scroll · LT/RT: page up/down · LB/RB: zoom · Y: keyboard · Start: menu · B: back · −: exit |
| Keyboard | A: type · X: delete · Y: space · LS: shift · Start: go · B: cancel |

Buttons follow the letters printed on the controller (Nintendo and Xbox layouts are both handled).
The Home button is left alone so ControllerHome or Windows can use it.

### Start options

- `ControllerMedia.exe --pad` starts in the controller interface even when no controller is connected.

## ControllerHome

Runs in the background with no window. When the Home/Guide button is pressed on a non-Xbox controller, it sends
Win+G to open Xbox Game Bar. Xbox-type controllers are skipped, because Windows already does this for them.
It writes a small `log.txt` next to the program (controllers seen and Home presses).

To start it with Windows, put a shortcut to `ControllerHome.exe` in the Startup folder (`shell:startup`).

## Requirements

- Windows 10 or 11, 64-bit
- .NET Framework 4.8 (included with Windows; its C# compiler is used to build)
- Microsoft Edge WebView2 Runtime (included with Windows 11)

## Libraries

The third-party libraries are not in this repository. Put them next to each program before building:

| File | Needed by | Where to get it |
|---|---|---|
| `SDL3.dll` (x64) | both | [SDL releases](https://github.com/libsdl-org/SDL/releases), `SDL3-<version>-win32-x64.zip` |
| `Microsoft.Web.WebView2.Core.dll` | Controller Media | NuGet package [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2), `lib\net462` |
| `Microsoft.Web.WebView2.WinForms.dll` | Controller Media | same package, `lib\net462` |
| `WebView2Loader.dll` | Controller Media | same package, `runtimes\win-x64\native` |

A `.nupkg` file is a zip archive: rename it to `.zip` to extract it. This project was built with WebView2 SDK
1.0.4314.35 and tested with the SDL 3 build that ships with Steam (version 3.5).

## Building

```powershell
.\build.ps1            # build both programs
.\build.ps1 -Restart   # stop the running programs, build, and start them again
```

The script uses `csc.exe` from .NET Framework, so no other tools are needed. That compiler supports C# 5 only,
so the source avoids newer syntax such as string interpolation.

## Layout

```
ControllerHome/
  src/ControllerHome.cs      the whole program
ControllerMedia/
  src/ControllerMedia.cs     window, controller input (SDL), browser, ad blocking, input filter
  ui/index.html, app.css, app.js   the interface (runs in WebView2)
  ui/adblock.js              injected into web pages while the ad blocker is on
  ui/tvtouch.js              swipe support for youtube.com/tv
assets/
  logo.svg, logo-small.svg   the logo (detailed, and a simplified one for 16 to 24 px)
  make-icon.ps1              renders the SVGs with Edge and writes app.ico and logo.png
  app.ico, logo.png          generated; app.ico is built into ControllerMedia.exe
build.ps1
```

Created at runtime and ignored by Git: `ControllerMedia/data/` (browser profile, including website sign-ins),
`ControllerMedia/settings.json` (ad blocker setting and bookmarks) and log files.

## Known limits

- MKV and AVI files may not play; the player uses the formats the Edge engine supports (MP4, WebM and similar).
- No browser tabs: each open tab would cost roughly 150 to 250 MB of memory.
- Game Bar can be opened with Home but not closed with it; simulated keys don't reliably reach Game Bar while it is open.
- If Steam Input is also managing the same controller, some controllers (seen with a Switch Pro Controller over
  Bluetooth) can briefly disconnect after Home is pressed.
