// Controller Media: a full-screen media centre driven entirely by a game controller.
// - Window + screens: WinForms hosting two WebView2 views (the app UI, and a web browser).
// - Controller input: SDL3 (XInput, DirectInput, PlayStation, Switch Pro and most third-party pads).
// - Media: local folders are exposed to the UI through WebView2 virtual host names (no web server).
// Built with the .NET Framework C# compiler (C# 5): no string interpolation or newer syntax.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    static void Main(string[] args)
    {
        bool first;
        using (Mutex m = new Mutex(true, @"Local\ControllerMedia.SingleInstance", out first))
        {
            if (!first) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor DPI aware: sharp text at 200% scaling
            Application.EnableVisualStyles();
            MainForm form = new MainForm();
            if (Array.IndexOf(args, "--pad") >= 0) form.TouchMode = false;   // start in the controller interface (otherwise decided by what's connected)
            Application.Run(form);
        }
    }
}

// ============================ Media roots ============================
class MediaRoot
{
    public string Id, Label, Host, Path;
    public MediaRoot(string id, string label, string path) { Id = id; Label = label; Path = path; Host = id + ".cm"; }
}

// ============================ Main window ============================
class MainForm : Form
{
    public const int MODE_UI = 0, MODE_BROWSER = 1;
    public volatile int Mode = MODE_UI;
    public volatile bool TvMode;          // web page is a TV-style site (YouTube TV): use keys instead of the cursor
    public IntPtr FormHandle;

    WebView2 ui, web;
    CoreWebView2Environment env;
    string appDir, uiDir, defaultUserAgent;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly List<MediaRoot> roots = new List<MediaRoot>();
    ControllerInput input;
    bool cursorHidden;

    // ---------------- input mode + layout ----------------
    // TouchMode follows the input used last: a touch switches to the touch interface, a controller press switches back.
    // While browsing: controller mode shows the page full-screen; touch mode adds a tablet-style bar on top
    // (drawn by the menu view, resized to a strip, so it costs no extra browser processes).
    public volatile bool TouchMode = true;
    bool browserActive, overlayOpen;
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    // The bar is 64 CSS pixels tall in the menu view; convert to real pixels with the window's actual scaling.
    int BarHeight
    {
        get
        {
            uint dpi = 0;
            try { dpi = GetDpiForWindow(Handle); } catch { }
            if (dpi == 0) dpi = 96;
            return (int)Math.Round(64 * dpi / 96.0);
        }
    }

    void LayoutViews()
    {
        if (ui == null || uiAsleep) return;   // asleep behind a game: everything is laid out again on waking
        int W = ClientSize.Width, H = ClientSize.Height;
        bool webShown = browserActive && !overlayOpen && WebReady;
        bool bar = webShown && TouchMode;
        if (webShown)
        {
            int top = bar ? BarHeight : 0;
            web.SetBounds(0, top, W, H - top);
            web.Visible = true;
            if (bar) { ui.SetBounds(0, 0, W, BarHeight); ui.Visible = true; }
            else ui.Visible = false;
        }
        else
        {
            ui.SetBounds(0, 0, W, H);
            ui.Visible = true;
            if (web != null) web.Visible = false;
        }
        if (videoPanel != null)
        {
            // VLC's picture sits behind the (see-through) menu view while a video plays.
            bool show = vlcActive && !webShown;
            if (show) { videoPanel.SetBounds(0, 0, W, H); videoPanel.Visible = true; videoPanel.SendToBack(); ui.BringToFront(); }
            else videoPanel.Visible = false;
        }
        SetUiMemoryLow(webShown && !bar);
        Mode = webShown ? MODE_BROWSER : MODE_UI;
        HideCursor(!webShown || TvMode || TouchMode);
        Dictionary<string, object> m = NavState(); m["evt"] = "bar"; m["show"] = bar; Post(m);
        if (webShown) web.Focus(); else ui.Focus();
    }

    public void SetTouchMode(bool touch)
    {
        if (TouchMode == touch) return;
        TouchMode = touch;
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "mode"; m["touch"] = touch; Post(m);
        LayoutViews();
        if (!touch && Mode == MODE_BROWSER && !TvMode) CenterCursor();
    }

    void CenterCursor() { Rectangle b = Screen.FromHandle(Handle).Bounds; Cursor.Position = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2); }

    Dictionary<string, object> NavState()
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["browsing"] = browserActive;
        if (WebReady)
        {
            string url = web.CoreWebView2.Source;
            m["url"] = url; m["title"] = web.CoreWebView2.DocumentTitle;
            m["canBack"] = web.CoreWebView2.CanGoBack; m["canForward"] = web.CoreWebView2.CanGoForward;
            m["bookmarked"] = IsBookmarked(url);
        }
        return m;
    }

    void SendNav() { if (!WebReady) return; Dictionary<string, object> m = NavState(); m["evt"] = "nav"; Post(m); }

    // ---------------- bookmarks (saved in settings.json) ----------------
    class Bookmark { public string Title, Url; }
    readonly List<Bookmark> bookmarks = new List<Bookmark>();
    bool IsBookmarked(string url) { return bookmarks.Exists(b => b.Url == url); }

    void ToggleBookmark()
    {
        if (!WebReady) return;
        string url = web.CoreWebView2.Source;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        if (IsBookmarked(url)) bookmarks.RemoveAll(b => b.Url == url);
        else
        {
            Bookmark b = new Bookmark(); b.Url = url; b.Title = web.CoreWebView2.DocumentTitle;
            if (string.IsNullOrEmpty(b.Title)) b.Title = SafeHost(url);
            bookmarks.Add(b);
        }
        SaveSettings(); SendNav(); SendBookmarks();
    }

    void RemoveBookmark(string url) { bookmarks.RemoveAll(b => b.Url == url); SaveSettings(); SendNav(); SendBookmarks(); }

    void SendBookmarks()
    {
        List<object> items = new List<object>();
        foreach (Bookmark b in bookmarks) { Dictionary<string, object> d = new Dictionary<string, object>(); d["title"] = b.Title; d["url"] = b.Url; items.Add(d); }
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "bookmarks"; m["items"] = items; Post(m);
    }

    // ---------------- volume (0 to 200%) ----------------
    // The interface handles its own players. For web pages only the part above 100% applies: boost.js is injected
    // into every page with the level built in, and told about changes while a page is open.
    int volume = 100;                    // saved in settings.json
    string boostScript, boostScriptId;
    bool boostBusy, boostAgain;

    readonly Dictionary<string, string> boostStates = new Dictionary<string, string>();   // per page/frame, from boost.js

    string BoostState()
    {
        string best = "idle";
        foreach (string s in boostStates.Values)
        {
            if (s == "on") return "on";
            if (s == "waiting") best = "waiting";
            else if (s == "blocked" && best == "idle") best = "blocked";
        }
        return best;
    }

    void SendBoostState()
    {
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "boost"; m["state"] = BoostState(); Post(m);
    }

    string BoostLevel { get { return (Math.Max(100, Math.Min(200, volume)) / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); } }

    void SetVolume(int v)
    {
        v = Math.Max(0, Math.Min(200, v));
        if (v == volume) return;
        volume = v;
        SaveSettings();
        ApplyWebBoost();
    }

    async void ApplyWebBoost()
    {
        if (!WebReady || boostScript == null) return;
        try { await web.CoreWebView2.ExecuteScriptAsync("window.__cmBoost&&window.__cmBoost(" + BoostLevel + ")"); } catch { }   // the open page
        await RegisterBoostScript();                                                                                         // pages opened later
    }

    async System.Threading.Tasks.Task RegisterBoostScript()
    {
        if (boostBusy) { boostAgain = true; return; }
        boostBusy = true;
        try
        {
            do
            {
                boostAgain = false;
                if (!WebReady || boostScript == null) break;
                CoreWebView2 w = web.CoreWebView2;
                if (boostScriptId != null) { w.RemoveScriptToExecuteOnDocumentCreated(boostScriptId); boostScriptId = null; }
                string id = await w.AddScriptToExecuteOnDocumentCreatedAsync("window.__cmBoostLevel=" + BoostLevel + ";" + boostScript);
                if (WebReady && web.CoreWebView2 == w) boostScriptId = id;
            } while (boostAgain);
        }
        catch { }
        finally { boostBusy = false; }
    }

    // ---------------- ad blocker ----------------
    bool adblock = true;                 // saved in settings.json
    string adblockScript, adblockScriptId;
    int blockedOnPage;
    static readonly HashSet<string> AdHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "googletagservices.com", "adservice.google.com",
        "imasdk.googleapis.com", "google-analytics.com", "pagead2.googlesyndication.com", "adsense.google.com",
        "amazon-adsystem.com", "adnxs.com", "adsrvr.org", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "pubmatic.com", "rubiconproject.com", "openx.net", "casalemedia.com", "indexww.com", "smartadserver.com",
        "adform.net", "moatads.com", "scorecardresearch.com", "quantserve.com", "quantcount.com", "media.net", "advertising.com",
        "yieldmo.com", "sharethrough.com", "teads.tv", "33across.com", "lijit.com", "sovrn.com", "bidswitch.net", "zedo.com",
        "popads.net", "popcash.net", "propellerads.com", "exoclick.com", "juicyads.com", "trafficjunky.net", "adcolony.com",
        "applovin.com", "inmobi.com", "unityads.unity3d.com", "an.facebook.com", "ads.linkedin.com", "ads.twitter.com",
        "static.ads-twitter.com", "analytics.tiktok.com", "ads.tiktok.com", "hotjar.com", "clarity.ms", "adroll.com",
        "contextweb.com", "gumgum.com", "triplelift.com", "3lift.com", "undertone.com", "spotxchange.com", "spotx.tv",
        "springserve.com", "tremorhub.com", "yahoo-ads.com", "ads.yahoo.com", "adtech.com", "adtechus.com", "serving-sys.com",
        "flashtalking.com", "mathtag.com", "bluekai.com", "krxd.net", "demdex.net", "everesttech.net", "rlcdn.com",
        "adsafeprotected.com", "doubleverify.com", "innovid.com", "id5-sync.com", "onetag-sys.com", "seedtag.com"
    };
    static readonly string[] AdUrlParts = { "youtube.com/pagead/", "youtube.com/api/stats/ads", "youtube.com/get_midroll_info", "youtube.com/ptracking", "/ad_break", "youtube.com/youtubei/v1/log_event?" };

    static bool IsAdHost(string host)
    {
        string h = host;
        while (!string.IsNullOrEmpty(h))
        {
            if (AdHosts.Contains(h)) return true;
            int dot = h.IndexOf('.');
            if (dot < 0) break;
            h = h.Substring(dot + 1);
        }
        return false;
    }

    static readonly string[] VideoExt = { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi", ".wmv", ".ts", ".mpg", ".mpeg" };
    static readonly string[] MusicExt = { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma" };
    static readonly string[] PictureExt = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    const string TvUserAgent = "Mozilla/5.0 (SMART-TV; LINUX; Tizen 6.5) AppleWebKit/537.36 (KHTML, like Gecko) 85.0.4183.93/6.5 TV Safari/537.36";

    public MainForm()
    {
        Text = "Controller Media";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        try
        {
            // assets/app.ico is embedded by build.ps1; without it the default window icon is used.
            using (Stream ico = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                if (ico != null) Icon = new Icon(ico);
        }
        catch { }

        appDir = AppDomain.CurrentDomain.BaseDirectory;
        uiDir = Path.Combine(appDir, "ui");
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(new MediaRoot("videos", "Videos", Path.Combine(user, "Videos")));
        roots.Add(new MediaRoot("music", "Music", Path.Combine(user, "Music")));
        roots.Add(new MediaRoot("pictures", "Pictures", Path.Combine(user, "Pictures")));
        roots.Add(new MediaRoot("downloads", "Downloads", Path.Combine(user, "Downloads")));
        roots.Add(new MediaRoot("sd", "SD card", @"D:\"));

        // The menu view is created in StartUiAsync; the web browser view only when you open a web page (EnsureWebAsync).
        Resize += (s, e) => LayoutViews();
        Activated += (s, e) => SleepUi(false);   // back in front: wake the menu view straight away
        Load += async (s, e) => await InitAsync();
        FormClosing += (s, e) =>
        {
            if (input != null) input.Stop();
            try
            {
                if (vlcTimer != null) vlcTimer.Stop();
                if (vlcPlayer != IntPtr.Zero) { Vlc.libvlc_media_player_stop(vlcPlayer); Vlc.libvlc_media_player_release(vlcPlayer); vlcPlayer = IntPtr.Zero; }
                if (vlcInst != IntPtr.Zero) { Vlc.libvlc_release(vlcInst); vlcInst = IntPtr.Zero; }
            }
            catch { }
        };
    }

    async System.Threading.Tasks.Task InitAsync()
    {
        FormHandle = Handle;
        LoadSettings();
        try { boostScript = File.ReadAllText(Path.Combine(uiDir, "boost.js")); } catch { boostScript = null; }
        try { adblockScript = File.ReadAllText(Path.Combine(uiDir, "adblock.js")); } catch { adblockScript = null; }
        try { tvTouchScript = File.ReadAllText(Path.Combine(uiDir, "tvtouch.js")); } catch { tvTouchScript = null; }
        steamDir = SteamDir();
        gamesDir = Path.Combine(appDir, "games");
        try { Directory.CreateDirectory(gamesDir); } catch { }

        string error = await StartUiAsync();
        if (error != null)
        {
            MessageBox.Show("Could not start the WebView2 browser engine:\n" + error, "Controller Media");
            Close(); return;
        }

        HideCursor(true);
        InputFilter.Install(FormHandle);
        InputFilter.OnTouch = () => { if (!TouchMode) BeginInvoke((Action)(() => SetTouchMode(true))); };
        FormClosed += (s, e) => InputFilter.Remove();
        input = new ControllerInput(this);
        input.Custom = savedButtons;
        input.Start();
        if (ui != null) ui.Focus();
    }

    // Creates the menu view (and the browser engine behind it) and loads the interface. Used at start-up and again
    // each time the hub wakes after sleeping behind a game. Returns null, or what went wrong.
    async System.Threading.Tasks.Task<string> StartUiAsync()
    {
        WebView2 view = new WebView2(); view.DefaultBackgroundColor = Color.Black;
        view.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        Controls.Add(view);
        try
        {
            // Keep the browser engine's caches on disk small (64 MB each); they otherwise grow to hundreds of MB.
            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions("--disk-cache-size=67108864 --media-cache-size=67108864");
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(appDir, "data"), options);
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Controls.Remove(view); view.Dispose();
            return ex.Message;
        }
        ui = view;

        CoreWebView2 c = ui.CoreWebView2;
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.SetVirtualHostNameToFolderMapping("app.cm", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        // Games screen: Steam's downloaded cover art, and the app's own "games" folder for other shortcuts and their pictures.
        try
        {
            string art = steamDir == null ? null : Path.Combine(steamDir, @"appcache\librarycache");
            if (art != null && Directory.Exists(art)) c.SetVirtualHostNameToFolderMapping("steamart.cm", art, CoreWebView2HostResourceAccessKind.Allow);
            if (Directory.Exists(gamesDir)) c.SetVirtualHostNameToFolderMapping("games.cm", gamesDir, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }
        foreach (MediaRoot r in roots)
            if (Directory.Exists(r.Path)) c.SetVirtualHostNameToFolderMapping(r.Host, r.Path, CoreWebView2HostResourceAccessKind.Allow);
        c.WebMessageReceived += OnUiMessage;
        // The browser engine refuses to start playback with sound until the user has touched the page or pressed a
        // key in it. Controller presses reach the interface as messages, which don't count, so videos and music
        // would never start. Running a script marked as a user action lifts that for this page (not for websites).
        c.NavigationCompleted += async (s, e) =>
        {
            try { await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"1\",\"userGesture\":true}"); } catch { }
        };
        c.Navigate("https://app.cm/index.html");
        return null;
    }

    // ---------------- YouTube TV swipes ----------------
    // Accepts only {cmTv:'swipe', dir, steps} from youtube.com/tv while in TV mode, and turns it into arrow keys.
    readonly Queue<ushort> swipeKeys = new Queue<ushort>();
    System.Windows.Forms.Timer swipeTimer;

    // Web pages can send only two things: "a finger touched the page" (switches to the touch interface),
    // and swipes from youtube.com/tv. Anything else is ignored.
    const string TouchDetectScript = "(()=>{if(window.__cmIn)return;window.__cmIn=1;let t=0;addEventListener('pointerdown',e=>{if(e.pointerType==='touch'){const n=Date.now();if(n-t>800){t=n;try{window.chrome.webview.postMessage({cmInput:'touch'})}catch(_){}}}},true)})();";

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Dictionary<string, object> m;
        try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
        if (m == null) return;
        if (Get(m, "cmInput") == "touch") { SetTouchMode(true); return; }
        if (Get(m, "cmBoost") == "status")
        {
            // Each page and frame reports what the volume boost is doing there (see boost.js); the menu shows it.
            string st = Get(m, "state");
            if (st == "on" || st == "waiting" || st == "blocked" || st == "idle" || st == "off")
            {
                if (boostStates.Count > 200) boostStates.Clear();
                boostStates[e.Source ?? ""] = st;
                SendBoostState();
            }
            return;
        }
        if (!TvMode) return;
        string host = SafeHost(e.Source);
        if (!(host == "youtube.com" || host.EndsWith(".youtube.com"))) return;
        if (Get(m, "cmTv") != "swipe") return;
        ushort vk;
        switch (Get(m, "dir")) { case "up": vk = 0x26; break; case "down": vk = 0x28; break; case "left": vk = 0x25; break; case "right": vk = 0x27; break; default: return; }
        int steps; if (!int.TryParse(Get(m, "steps"), out steps)) steps = 1;
        steps = Math.Max(1, Math.Min(6, steps));
        for (int i = 0; i < steps; i++) swipeKeys.Enqueue(vk);
        if (swipeTimer == null)
        {
            swipeTimer = new System.Windows.Forms.Timer(); swipeTimer.Interval = 70;
            swipeTimer.Tick += (s, a) => { if (swipeKeys.Count == 0) { swipeTimer.Stop(); return; } Native.Key(swipeKeys.Dequeue()); };
        }
        swipeTimer.Start();
    }

    // ---------------- ad blocker ----------------
    static string SafeHost(string url) { try { return new Uri(url).Host; } catch { return ""; } }

    void OnWebRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!adblock) return;
        string url = e.Request.Uri;
        bool block = IsAdHost(SafeHost(url));
        if (!block) foreach (string part in AdUrlParts) if (url.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { block = true; break; }
        if (!block) return;
        e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
        blockedOnPage++;
    }

    async System.Threading.Tasks.Task EnableAdblockScript()
    {
        if (adblockScript == null || adblockScriptId != null || !WebReady) return;
        adblockScriptId = await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(adblockScript);
    }

    async void SetAdblock(bool on)
    {
        adblock = on;
        SaveSettings();
        if (WebReady)
        {
            if (on) await EnableAdblockScript();
            else if (adblockScriptId != null) { web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(adblockScriptId); adblockScriptId = null; }
            string src = web.CoreWebView2.Source;
            if (!string.IsNullOrEmpty(src) && src != "about:blank") web.CoreWebView2.Reload(); // apply to the page that's open
        }
        SendSettings();
    }

    // ---------------- browser view: created on demand, unloaded when you leave ----------------
    string tvTouchScript;
    bool creatingWeb;
    bool WebReady { get { return web != null && web.CoreWebView2 != null; } }

    async System.Threading.Tasks.Task<bool> EnsureWebAsync()
    {
        if (WebReady) return true;
        if (creatingWeb) return false;
        creatingWeb = true;
        try
        {
            web = new WebView2(); web.DefaultBackgroundColor = Color.Black; web.Visible = false;
            web.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            Controls.Add(web);
            await web.EnsureCoreWebView2Async(env);
            CoreWebView2 w = web.CoreWebView2;
            if (defaultUserAgent == null) defaultUserAgent = w.Settings.UserAgent;
            w.Settings.IsStatusBarEnabled = false;
            w.SourceChanged += (s, e) => SendNav();
            w.HistoryChanged += (s, e) => SendNav();
            w.DocumentTitleChanged += (s, e) => SendNav();
            await w.AddScriptToExecuteOnDocumentCreatedAsync(TouchDetectScript);
            w.NewWindowRequested += (s, e) => { e.Handled = true; if (!(adblock && IsAdHost(SafeHost(e.Uri)))) w.Navigate(e.Uri); }; // keep pop-ups in the same view; drop ad pop-ups
            w.NavigationStarting += (s, e) => { blockedOnPage = 0; boostStates.Clear(); SendBoostState(); };
            // Ad blocker: block requests to ad/tracker domains, and inject the page script (adblock.js)
            w.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            w.WebResourceRequested += OnWebRequest;
            adblockScriptId = null;
            if (adblock) await EnableAdblockScript();
            // Touch swiping for YouTube's TV interface (it only understands arrow keys)
            if (tvTouchScript != null) await w.AddScriptToExecuteOnDocumentCreatedAsync(tvTouchScript);
            boostScriptId = null;
            await RegisterBoostScript();
            w.WebMessageReceived += OnWebMessage;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the web browser:\n" + ex.Message, "Controller Media");
            UnloadWeb();
            return false;
        }
        finally { creatingWeb = false; }
    }

    void UnloadWeb()
    {
        if (web == null) return;
        WebView2 w = web; web = null; adblockScriptId = null; boostScriptId = null;
        Controls.Remove(w);
        w.Dispose();   // frees the page and its browser-engine process
    }

    void SetUiMemoryLow(bool low)
    {
        try { if (ui.CoreWebView2 != null) ui.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
    }

    void SendSettings()
    {
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "settings"; m["adblock"] = adblock; m["touch"] = TouchMode; m["volume"] = volume; m["vlc"] = Vlc.Dir != null; Post(m);
        SendButtons();
        if (restorePending)
        {
            // Just woken after sleeping behind a game: go back to the screen the menus were on.
            restorePending = false;
            Dictionary<string, object> r = new Dictionary<string, object>(); r["evt"] = "restore"; r["view"] = sleepView; Post(r);
        }
    }

    string SettingsPath { get { return Path.Combine(appDir, "settings.json"); } }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            Dictionary<string, object> s = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath));
            object v; if (s.TryGetValue("adblock", out v) && v is bool) adblock = (bool)v;
            int vol; if (s.TryGetValue("volume", out v) && v != null && int.TryParse(v.ToString(), out vol)) volume = Math.Max(0, Math.Min(200, vol));
            Dictionary<string, object> btn = s.TryGetValue("buttons", out v) ? v as Dictionary<string, object> : null;
            if (btn != null)
            {
                // { "ok": 0, "back": 1, ... }: the button position for each role. Ignored unless complete and without repeats.
                int[] t = new int[ControllerInput.R_COUNT]; bool good = true;
                for (int i = 0; i < t.Length; i++)
                {
                    object o; int n;
                    if (btn.TryGetValue(ControllerInput.RoleKeys[i], out o) && o != null && int.TryParse(o.ToString(), out n) && n >= 0 && n < 32 && Array.IndexOf(t, n, 0, i) < 0) t[i] = n; else { good = false; break; }
                }
                if (good) savedButtons = t;
            }
            System.Collections.IEnumerable list = s.TryGetValue("bookmarks", out v) ? v as System.Collections.IEnumerable : null;
            if (list != null)
                foreach (object o in list)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>; if (d == null) continue;
                    Bookmark b = new Bookmark(); b.Title = Get(d, "title"); b.Url = Get(d, "url");
                    if (b.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !IsBookmarked(b.Url)) bookmarks.Add(b);
                }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            Dictionary<string, object> s = new Dictionary<string, object>(); s["adblock"] = adblock; s["volume"] = volume;
            int[] custom = input != null ? input.Custom : savedButtons;
            if (custom != null)
            {
                Dictionary<string, object> btn = new Dictionary<string, object>();
                for (int i = 0; i < custom.Length; i++) btn[ControllerInput.RoleKeys[i]] = custom[i];
                s["buttons"] = btn;
            }
            List<object> items = new List<object>();
            foreach (Bookmark b in bookmarks) { Dictionary<string, object> d = new Dictionary<string, object>(); d["title"] = b.Title; d["url"] = b.Url; items.Add(d); }
            s["bookmarks"] = items;
            File.WriteAllText(SettingsPath, json.Serialize(s));
        }
        catch { }
    }

    // ---------------- cursor ----------------
    void HideCursor(bool hide)
    {
        if (hide && !cursorHidden) { Cursor.Hide(); cursorHidden = true; }
        else if (!hide && cursorHidden) { Cursor.Show(); cursorHidden = false; }
    }

    // ---------------- messages to / from the UI ----------------
    public void Post(object o)
    {
        if (uiAsleep || ui == null) return;   // asleep behind a game: the menus are loaded afresh on waking
        if (ui.CoreWebView2 != null) ui.CoreWebView2.PostWebMessageAsJson(json.Serialize(o));
    }

    public void SendAction(string action)
    {
        Dictionary<string, object> m = new Dictionary<string, object>(); m["a"] = action; Post(m);
    }

    void OnUiMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Dictionary<string, object> m;
        try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
        string cmd = Get(m, "cmd");
        switch (cmd)
        {
            case "roots": SendRoots(Get(m, "section")); break;
            case "list": SendListing(Get(m, "section"), Get(m, "root"), Get(m, "path")); break;
            case "browse": OpenBrowser(Get(m, "url"), Get(m, "tv") == "True"); break;
            case "browserBack": BrowserBack(); break;
            case "browserForward": if (WebReady) { if (web.CoreWebView2.CanGoForward) web.CoreWebView2.GoForward(); ShowWeb(); } break;
            case "browserReload": if (WebReady) { web.CoreWebView2.Reload(); ShowWeb(); } break;
            case "closeBrowser": CloseBrowser(); break;
            case "overlayClosed": ShowWeb(); break;
            case "typeText": ShowWeb(); TypeIntoPage(Get(m, "text"), Get(m, "enter") == "True"); break;
            case "getSettings": SendSettings(); break;
            case "setAdblock": SetAdblock(Get(m, "on") == "True"); break;
            case "setVolume": { int v; if (int.TryParse(Get(m, "v"), out v)) SetVolume(v); break; }
            case "playing": musicPlaying = Get(m, "on") == "True"; break;
            case "view": lastView = Get(m, "name"); break;
            case "getButtons": SendButtons(); break;
            case "bindButton": if (input != null) { input.BeginCapture(Get(m, "role")); SendButtons(); } break;
            case "cancelBind": if (input != null) { input.CancelCapture(); SendButtons(); } break;
            case "resetButtons": if (input != null) { input.CancelCapture(); input.Custom = null; ButtonsChanged(true); } break;
            case "games": SendGames(); break;
            case "launchGame": LaunchGame(Get(m, "kind"), Get(m, "id")); break;
            case "gameCover": GameCover(m); break;
            case "vlcPlay": VlcPlay(Get(m, "root"), Get(m, "path"), Get(m, "name")); break;
            case "vlc": VlcCommand(m); break;
            case "inputTouch": SetTouchMode(true); break;
            case "overlayOpen": if (browserActive) { overlayOpen = true; LayoutViews(); } break;
            case "openMenu": ShowBrowserMenu(); break;
            case "toggleBookmark": ToggleBookmark(); if (Mode == MODE_BROWSER && WebReady) web.Focus(); break;
            case "removeBookmark": RemoveBookmark(Get(m, "url")); break;
            case "getBookmarks": SendBookmarks(); break;
            case "exit": Close(); break;
        }
    }

    static string Get(Dictionary<string, object> m, string k)
    {
        object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : "";
    }

    string[] ExtFor(string section)
    {
        if (section == "music") return MusicExt;
        if (section == "pictures") return PictureExt;
        return VideoExt;
    }

    void SendRoots(string section)
    {
        List<object> list = new List<object>();
        foreach (MediaRoot r in roots)
        {
            if (section == "videos" && (r.Id == "music" || r.Id == "pictures")) continue;
            if (section == "music" && (r.Id == "videos" || r.Id == "pictures")) continue;
            if (section == "pictures" && (r.Id == "videos" || r.Id == "music")) continue;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = r.Id; d["label"] = r.Label; d["host"] = r.Host; d["available"] = Directory.Exists(r.Path);
            list.Add(d);
        }
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "roots"; m["section"] = section; m["roots"] = list;
        Post(m);
    }

    void SendListing(string section, string rootId, string rel)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["evt"] = "list"; m["section"] = section; m["root"] = rootId; m["path"] = rel;
        MediaRoot root = roots.Find(r => r.Id == rootId);
        try
        {
            if (root == null) throw new Exception("Unknown folder");
            string full = Path.GetFullPath(Path.Combine(root.Path, rel.Replace('/', '\\')));
            if (!full.StartsWith(Path.GetFullPath(root.Path), StringComparison.OrdinalIgnoreCase)) throw new Exception("Outside the media folder");
            string[] ext = ExtFor(section);
            List<object> dirs = new List<object>(), files = new List<object>();
            DirectoryInfo di = new DirectoryInfo(full);
            List<DirectoryInfo> subdirs = new List<DirectoryInfo>(di.GetDirectories());
            subdirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (DirectoryInfo d in subdirs)
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (d.Name.StartsWith("$") || d.Name == "System Volume Information") continue;
                dirs.Add(d.Name);
            }
            List<FileInfo> fl = new List<FileInfo>(di.GetFiles());
            fl.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (FileInfo f in fl)
            {
                if (Array.IndexOf(ext, f.Extension.ToLowerInvariant()) < 0) continue;
                Dictionary<string, object> fd = new Dictionary<string, object>();
                fd["name"] = f.Name; fd["size"] = f.Length;
                files.Add(fd);
            }
            m["dirs"] = dirs; m["files"] = files; m["host"] = root.Host;
        }
        catch (Exception ex) { m["error"] = ex.Message; }
        Post(m);
    }

    // ---------------- sleeping behind a game ----------------
    // Nothing is playing and no web page is open: the hub may let go of the controllers and suspend its menu
    // view while it is not the window in front (see ControllerInput.SleepUntilFront).
    volatile bool musicPlaying;   // reported by the menu view
    volatile bool uiAsleep;
    bool uiWaking, restorePending;
    string lastView = "home", sleepView = "home";   // the screen the menus are on, and the one to come back to
    DateTime gameLaunchedAt = DateTime.MinValue;
    public bool CanSleep { get { return !uiAsleep && !uiWaking && !vlcActive && !vlcStarting && !browserActive && !musicPlaying && !coverBusy; } }
    // How long the hub must be behind another window before it sleeps: a few seconds after starting a game from
    // it, half a minute otherwise (so a quick look at another window doesn't cost a reload).
    public int SleepDelayMs { get { return (DateTime.Now - gameLaunchedAt).TotalMinutes < 3 ? 5000 : 30000; } }

    // Sleeping closes the menu view and with it the whole browser engine (about 250 MB and six processes); only
    // this small program stays. Waking starts it again, which takes a second or two, and returns to the same screen.
    public async void SleepUi(bool sleep)
    {
        try
        {
            if (sleep)
            {
                if (!CanSleep || IsForeground() || ui == null) return;
                uiAsleep = true; sleepView = lastView;
                WebView2 old = ui; ui = null;
                Controls.Remove(old); old.Dispose();
            }
            else if (uiAsleep && !uiWaking)
            {
                uiWaking = true;
                try
                {
                    if (await StartUiAsync() == null) { uiAsleep = false; restorePending = true; LayoutViews(); if (ui != null) ui.Focus(); }
                }
                finally { uiWaking = false; }
            }
        }
        catch { }
    }

    // ---------------- controller buttons (Settings screen) ----------------
    int[] savedButtons;   // read from settings.json before the controller reader exists

    // Called by the controller reader when a role got a new button, the capture timed out, or another controller took over.
    public void ButtonsChanged(bool save)
    {
        if (save) SaveSettings();
        SendButtons();
    }

    void SendButtons()
    {
        if (input == null) return;
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["evt"] = "buttons"; m["names"] = input.ButtonNames(); m["custom"] = input.Custom != null; m["capturing"] = input.Capturing;
        Post(m);
    }

    // ---------------- games ----------------
    // The Games screen lists every installed Steam game (read from Steam's library folders, with the cover art
    // Steam has already downloaded), plus any shortcut placed in the app's "games" folder for games that are not
    // on Steam. A picture with the same name as a shortcut (jpg or png) is used as its cover.
    string steamDir, gamesDir;

    static string SteamDir()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string p = k == null ? null : k.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(p)) { p = Path.GetFullPath(p.Replace('/', '\\')); if (Directory.Exists(Path.Combine(p, "steamapps"))) return p; }
            }
        }
        catch { }
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(Path.Combine(d, "steamapps")) ? d : null;
    }

    static string VdfValue(string text, string key)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, "\"" + key + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
    }

    // Steam keeps cover art per game in appcache\librarycache\<id>\, either directly or one folder down.
    string SteamCover(string id)
    {
        try
        {
            string dir = Path.Combine(steamDir, @"appcache\librarycache\" + id);
            if (!Directory.Exists(dir)) return null;
            foreach (string want in new[] { "library_600x900.jpg", "library_capsule.jpg", "header.jpg", "library_header.jpg" })
            {
                string[] hits = Directory.GetFiles(dir, want, SearchOption.AllDirectories);
                // "#wide" marks the landscape pictures, so the menu can fit them into its upright tiles.
                if (hits.Length > 0) return "https://steamart.cm/" + id + "/" + hits[0].Substring(dir.Length + 1).Replace('\\', '/') + (want.Contains("header") ? "#wide" : "");
            }
        }
        catch { }
        return null;
    }

    // A picture you assigned to a game: it sits in the games folder, named after the shortcut, or "steam-<id>" for a
    // Steam game. ".wide" in the name marks a landscape picture. It wins over Steam's own art.
    static readonly string[] CoverExt = { ".jpg", ".jpeg", ".png", ".webp" };

    string CoverKey(string kind, string id) { return kind == "steam" ? "steam-" + id : Path.GetFileNameWithoutExtension(id); }

    string CustomCover(string key)
    {
        try
        {
            foreach (string wide in new[] { "", ".wide" })
                foreach (string e in CoverExt)
                {
                    string f = Path.Combine(gamesDir, key + wide + e);
                    if (File.Exists(f)) return "https://games.cm/" + Uri.EscapeDataString(key + wide + e) + "?v=" + File.GetLastWriteTimeUtc(f).Ticks + (wide != "" ? "#wide" : "");
                }
        }
        catch { }
        return null;
    }

    void RemoveCustomCover(string key)
    {
        foreach (string wide in new[] { "", ".wide" })
            foreach (string e in CoverExt)
                try { string f = Path.Combine(gamesDir, key + wide + e); if (File.Exists(f)) File.Delete(f); } catch { }
    }

    void SendGames()
    {
        Dictionary<string, object> msg = new Dictionary<string, object>(); msg["evt"] = "games"; msg["items"] = ListGames(); msg["folder"] = gamesDir; Post(msg);
    }

    List<Dictionary<string, object>> ListGames()
    {
        List<Dictionary<string, object>> games = new List<Dictionary<string, object>>();
        if (steamDir != null)
        {
            List<string> libs = new List<string> { Path.Combine(steamDir, "steamapps") };
            try
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(steamDir, @"steamapps\libraryfolders.vdf")), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    string lib = Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps");
                    if (Directory.Exists(lib) && !libs.Exists(x => string.Equals(x, lib, StringComparison.OrdinalIgnoreCase))) libs.Add(lib);
                }
            }
            catch { }
            foreach (string lib in libs)
            {
                string[] files; try { files = Directory.GetFiles(lib, "appmanifest_*.acf"); } catch { continue; }
                foreach (string f in files)
                {
                    try
                    {
                        string t = File.ReadAllText(f, Encoding.UTF8), id = VdfValue(t, "appid"), name = VdfValue(t, "name");
                        if (id == "" || name == "" || id == "228980" || name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("Proton") || name.StartsWith("Steam Linux Runtime")) continue;
                        if (games.Exists(g => (string)g["id"] == id)) continue;
                        long played; long.TryParse(VdfValue(t, "LastPlayed"), out played);
                        Dictionary<string, object> g2 = new Dictionary<string, object>();
                        string mine = CustomCover("steam-" + id);
                        g2["kind"] = "steam"; g2["id"] = id; g2["name"] = name; g2["played"] = played; g2["art"] = mine ?? SteamCover(id); g2["custom"] = mine != null;
                        games.Add(g2);
                    }
                    catch { }
                }
            }
        }
        try
        {
            if (Directory.Exists(gamesDir))
                foreach (string f in Directory.GetFiles(gamesDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".exe" && ext != ".bat") continue;
                    string baseName = Path.GetFileNameWithoutExtension(f);
                    Dictionary<string, object> g2 = new Dictionary<string, object>();
                    string mine = CustomCover(baseName);
                    g2["kind"] = "file"; g2["id"] = Path.GetFileName(f); g2["name"] = baseName; g2["played"] = 0L; g2["art"] = mine; g2["custom"] = mine != null;
                    games.Add(g2);
                }
        }
        catch { }
        // Most recently played first, then by name.
        games.Sort((a, b) => { int c = ((long)b["played"]).CompareTo((long)a["played"]); return c != 0 ? c : string.Compare((string)a["name"], (string)b["name"], StringComparison.OrdinalIgnoreCase); });
        return games;
    }

    // ---- game pictures: choose one of your own, or look one up on Steam's store ----
    bool coverBusy;

    async void GameCover(Dictionary<string, object> m)
    {
        string op = Get(m, "op"), kind = Get(m, "kind"), id = Get(m, "id");
        try
        {
            if (op == "findAll") { await FindCovers(null, null); return; }
            // Only games that are really in the list can be changed.
            Dictionary<string, object> game = ListGames().Find(g => (string)g["kind"] == kind && (string)g["id"] == id);
            if (game == null) return;
            string key = CoverKey(kind, id);
            if (op == "remove") { RemoveCustomCover(key); SendGames(); }
            else if (op == "find") await FindCovers(kind, id);
            else if (op == "pick")
            {
                MediaRoot root = roots.Find(r => r.Id == Get(m, "root"));
                if (root == null) return;
                string full = Path.GetFullPath(Path.Combine(Path.Combine(root.Path, Get(m, "path").Replace('/', '\\')), Get(m, "name")));
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (!full.StartsWith(Path.GetFullPath(root.Path), StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || Array.IndexOf(CoverExt, ext) < 0)
                { CoverNote("That file can't be used as a picture (use JPG, PNG or WebP)."); return; }
                RemoveCustomCover(key);
                File.Copy(full, Path.Combine(gamesDir, key + ext), true);
                File.SetLastWriteTimeUtc(Path.Combine(gamesDir, key + ext), DateTime.UtcNow);
                SendGames();
            }
        }
        catch (Exception ex) { CoverNote("Could not change the picture: " + ex.Message); }
    }

    void CoverNote(string text) { Dictionary<string, object> n = new Dictionary<string, object>(); n["evt"] = "coverSearch"; n["note"] = text; Post(n); }

    // Looks up pictures on Steam's store (no account needed) and saves them in the games folder. With no game
    // given, it does every game that has no picture yet.
    async System.Threading.Tasks.Task FindCovers(string kind, string id)
    {
        if (coverBusy) return;
        coverBusy = true;
        try
        {
            List<Dictionary<string, object>> targets = ListGames().FindAll(g => kind == null ? g["art"] == null : ((string)g["kind"] == kind && (string)g["id"] == id));
            if (targets.Count == 0) { CoverNote("Every game already has a picture."); return; }
            int found = 0, done = 0; string lastMiss = null;
            foreach (Dictionary<string, object> g in targets)
            {
                string gKind = (string)g["kind"], gId = (string)g["id"], name = (string)g["name"];
                CoverNote("Looking for pictures: " + (++done) + " of " + targets.Count);
                CoverArt art = await System.Threading.Tasks.Task.Run(() => CoverArt.Find(gKind == "steam" ? gId : null, name));
                if (art == null) { lastMiss = name; continue; }
                // A landscape header is a step down from the upright picture Steam already keeps for its own games.
                if (art.Wide && gKind == "steam" && SteamCover(gId) != null) { lastMiss = name; continue; }
                string key = CoverKey(gKind, gId);
                RemoveCustomCover(key);
                File.WriteAllBytes(Path.Combine(gamesDir, key + (art.Wide ? ".wide" : "") + ".jpg"), art.Data);
                found++;
            }
            SendGames();
            CoverNote(targets.Count == 1 ? (found == 1 ? "Picture updated." : "No picture found for " + lastMiss + ".")
                                         : "Found pictures for " + found + " of " + targets.Count + " games.");
        }
        catch (Exception ex) { CoverNote("The search failed: " + ex.Message); }
        finally { coverBusy = false; }
    }

    void LaunchGame(string kind, string id)
    {
        Dictionary<string, object> r = new Dictionary<string, object>(); r["evt"] = "gameStarted";
        try
        {
            ProcessStartInfo si;
            if (kind == "steam")
            {
                long n; if (!long.TryParse(id, out n)) throw new Exception("Unknown game");
                si = new ProcessStartInfo("steam://rungameid/" + n);
            }
            else
            {
                if (string.IsNullOrEmpty(id) || id.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) throw new Exception("Unknown game");
                string full = Path.Combine(gamesDir, id);
                if (!File.Exists(full)) throw new Exception("Shortcut not found");
                si = new ProcessStartInfo(full); si.WorkingDirectory = gamesDir;
            }
            si.UseShellExecute = true;
            using (Process.Start(si)) { }
            gameLaunchedAt = DateTime.Now;   // the hub goes to sleep soon after the game takes over the screen
            r["ok"] = true;
        }
        catch (Exception ex) { r["ok"] = false; r["msg"] = ex.Message; }
        Post(r);
    }

    // ---------------- VLC video player ----------------
    // VLC draws the picture into a plain panel behind the menu view; the menu view is made see-through while a
    // video plays, so the same on-screen controls and touch handling work on top of the picture.
    Panel videoPanel;
    IntPtr vlcInst, vlcPlayer;
    System.Windows.Forms.Timer vlcTimer;
    bool vlcActive;
    int vlcVolumeApplied = -1, vlcLastState = -1;

    bool vlcStarting;

    async void VlcPlay(string rootId, string rel, string name)
    {
        if (vlcStarting) return;
        Dictionary<string, object> fail = new Dictionary<string, object>(); fail["evt"] = "vlc"; fail["st"] = "error";
        try
        {
            MediaRoot root = roots.Find(r => r.Id == rootId);
            if (root == null) throw new Exception("Unknown folder");
            string full = Path.GetFullPath(Path.Combine(Path.Combine(root.Path, (rel ?? "").Replace('/', '\\')), name ?? ""));
            if (!full.StartsWith(Path.GetFullPath(root.Path), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new Exception("File not found");
            if (!Vlc.Load()) throw new Exception("VLC could not be loaded");
            if (vlcInst == IntPtr.Zero)
            {
                // No VLC messages on the picture and no VLC window title; subtitles next to the file are picked up by VLC itself.
                // Direct3D 9 output: VLC's default (Direct3D 11) is always drawn on top of the menu view, which would hide the controls.
                List<string> args = new List<string> { "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--quiet", "--vout=direct3d9" };
                // A fresh copy of the engine has no index of its plugins, and without one VLC inspects every plugin
                // file each time it starts (minutes). Have it write the index once; later starts take a moment.
                if (!File.Exists(Path.Combine(Vlc.Dir, @"plugins\plugins.dat")))
                {
                    args.Add("--reset-plugins-cache");
                    Dictionary<string, object> note = new Dictionary<string, object>(); note["evt"] = "vlc"; note["st"] = "opening";
                    note["msg"] = "Getting VLC ready for first use. This takes a minute or two, once."; Post(note);
                }
                vlcStarting = true;
                try { vlcInst = await System.Threading.Tasks.Task.Run(() => Vlc.NewInstance(args.ToArray())); }
                finally { vlcStarting = false; }
                if (vlcInst == IntPtr.Zero) throw new Exception("VLC did not start");
            }
            if (videoPanel == null)
            {
                videoPanel = new Panel(); videoPanel.BackColor = Color.Black; videoPanel.Enabled = false; videoPanel.Visible = false;
                Controls.Add(videoPanel);
            }
            if (vlcPlayer == IntPtr.Zero)
            {
                vlcPlayer = Vlc.libvlc_media_player_new(vlcInst);
                Vlc.libvlc_video_set_mouse_input(vlcPlayer, 0);   // touches and clicks go to the menu view on top
                Vlc.libvlc_video_set_key_input(vlcPlayer, 0);
                Vlc.libvlc_media_player_set_hwnd(vlcPlayer, videoPanel.Handle);
            }
            else if (vlcActive) Vlc.libvlc_media_player_stop(vlcPlayer);   // switching to another video
            IntPtr media = Vlc.libvlc_media_new_path(vlcInst, Vlc.Utf8(full));
            if (media == IntPtr.Zero) throw new Exception("VLC could not open the file");
            Vlc.libvlc_media_player_set_media(vlcPlayer, media);
            Vlc.libvlc_media_release(media);
            vlcVolumeApplied = -1; vlcLastState = -1;
            if (Vlc.libvlc_media_player_play(vlcPlayer) != 0) throw new Exception("VLC could not play the file");

            vlcActive = true;
            ui.DefaultBackgroundColor = Color.Transparent;
            LayoutViews();
            if (vlcTimer == null) { vlcTimer = new System.Windows.Forms.Timer(); vlcTimer.Interval = 400; vlcTimer.Tick += (s, e) => VlcTick(); }
            vlcTimer.Start();
        }
        catch (Exception ex) { fail["msg"] = ex.Message; Post(fail); }
    }

    void VlcTick()
    {
        if (!vlcActive || vlcPlayer == IntPtr.Zero) return;
        int st = Vlc.libvlc_media_player_get_state(vlcPlayer);
        // VLC only accepts a volume once its sound output exists, so keep trying until it takes.
        if (vlcVolumeApplied != volume && (st == Vlc.Playing || st == Vlc.Paused) && Vlc.libvlc_audio_set_volume(vlcPlayer, volume) == 0) vlcVolumeApplied = volume;
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["evt"] = "vlc";
        m["st"] = st == Vlc.Playing ? "playing" : st == Vlc.Paused ? "paused" : st == Vlc.Ended ? "ended" : st == Vlc.Error ? "error" : st == Vlc.Stopped ? "stopped" : "opening";
        m["t"] = Math.Max(0, Vlc.libvlc_media_player_get_time(vlcPlayer)) / 1000.0;
        m["len"] = Math.Max(0, Vlc.libvlc_media_player_get_length(vlcPlayer)) / 1000.0;
        Post(m);
        if ((st == Vlc.Ended || st == Vlc.Error) && st != vlcLastState) vlcTimer.Stop();   // the menu decides what plays next
        vlcLastState = st;
    }

    void VlcStop()
    {
        if (vlcTimer != null) vlcTimer.Stop();
        if (vlcPlayer != IntPtr.Zero) Vlc.libvlc_media_player_stop(vlcPlayer);
        if (!vlcActive) return;
        vlcActive = false;
        ui.DefaultBackgroundColor = Color.Black;
        LayoutViews();
    }

    // Steps to the next subtitle or audio track and tells the menu its name ("Off" is one of the subtitle choices).
    void VlcCycleTrack(bool subtitles)
    {
        if (!vlcActive) return;
        List<KeyValuePair<int, string>> list = Vlc.Tracks(subtitles ? Vlc.libvlc_video_get_spu_description(vlcPlayer) : Vlc.libvlc_audio_get_track_description(vlcPlayer));
        if (!subtitles) list.RemoveAll(t => t.Key < 0);   // "Disable" makes no sense for sound; the volume does that
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "vlcTrack"; m["kind"] = subtitles ? "Subtitles" : "Audio";
        if (list.Count == 0) { m["name"] = subtitles ? "none in this video" : "only one track"; Post(m); return; }
        int cur = subtitles ? Vlc.libvlc_video_get_spu(vlcPlayer) : Vlc.libvlc_audio_get_track(vlcPlayer);
        int i = list.FindIndex(t => t.Key == cur);
        KeyValuePair<int, string> next = list[(i + 1) % list.Count];
        if (subtitles) Vlc.libvlc_video_set_spu(vlcPlayer, next.Key); else Vlc.libvlc_audio_set_track(vlcPlayer, next.Key);
        m["name"] = next.Key < 0 ? "Off" : next.Value;
        if (!subtitles && list.Count == 1) m["name"] = next.Value + " (only one)";
        Post(m);
    }

    void VlcCommand(Dictionary<string, object> m)
    {
        string op = Get(m, "op");
        if (op == "stop") { VlcStop(); return; }
        if (!vlcActive || vlcPlayer == IntPtr.Zero) return;
        int st = Vlc.libvlc_media_player_get_state(vlcPlayer);
        if (op == "toggle")
        {
            if (st == Vlc.Ended || st == Vlc.Stopped) { Vlc.libvlc_media_player_stop(vlcPlayer); Vlc.libvlc_media_player_play(vlcPlayer); vlcTimer.Start(); }
            else Vlc.libvlc_media_player_set_pause(vlcPlayer, st == Vlc.Playing ? 1 : 0);
        }
        else if (op == "seek")
        {
            double by; if (!double.TryParse(Get(m, "by"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out by)) return;
            long len = Vlc.libvlc_media_player_get_length(vlcPlayer), t = Vlc.libvlc_media_player_get_time(vlcPlayer) + (long)(by * 1000);
            Vlc.libvlc_media_player_set_time(vlcPlayer, Math.Max(0, len > 0 ? Math.Min(len - 1000, t) : t));
        }
        else if (op == "seekTo")
        {
            double pos; if (!double.TryParse(Get(m, "pos"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pos)) return;
            long len = Vlc.libvlc_media_player_get_length(vlcPlayer);
            if (len > 0) Vlc.libvlc_media_player_set_time(vlcPlayer, (long)(Math.Max(0, Math.Min(1, pos)) * len));
        }
        else if (op == "sub") VlcCycleTrack(true);
        else if (op == "audio") VlcCycleTrack(false);
        VlcTick();
    }

    // ---------------- browser ----------------
    public async void OpenBrowser(string url, bool tv)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!await EnsureWebAsync()) return;
        if (!url.Contains("://"))
        {
            if (url.Contains(".") && !url.Contains(" ")) url = "https://" + url;
            else url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
        }
        TvMode = tv;
        web.CoreWebView2.Settings.UserAgent = tv ? TvUserAgent : defaultUserAgent;
        web.CoreWebView2.Navigate(url);
        ShowWeb();
    }

    public void ShowWeb()
    {
        if (!WebReady) return;
        browserActive = true; overlayOpen = false;
        LayoutViews();
        if (!TvMode && !TouchMode) CenterCursor();
    }

    public void ShowBrowserMenu()
    {
        if (!WebReady) return;
        overlayOpen = true;
        LayoutViews();
        Dictionary<string, object> m = NavState();
        m["evt"] = "browserMenu"; m["adblock"] = adblock; m["blocked"] = blockedOnPage; m["boost"] = BoostState();
        Post(m);
    }

    public void OpenPageKeyboard()
    {
        if (!WebReady) return;
        overlayOpen = true;
        LayoutViews();
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "pageKeyboard"; Post(m);
    }

    public void BrowserBack()
    {
        if (WebReady && web.CoreWebView2.CanGoBack) { web.CoreWebView2.GoBack(); ShowWeb(); }
        else CloseBrowser();
    }

    public void CloseBrowser()
    {
        browserActive = false; overlayOpen = false;
        UnloadWeb();   // release the browser's memory until it's needed again
        LayoutViews();
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "browserClosed"; Post(m);
    }

    public void Zoom(double factor)
    {
        if (!WebReady) return;
        double z = Math.Max(0.5, Math.Min(3.0, web.ZoomFactor * factor));
        web.ZoomFactor = z;
    }

    void TypeIntoPage(string text, bool enter)
    {
        // Give the page a moment to get focus back, then type the text as real key presses.
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer(); t.Interval = 150;
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); Native.TypeText(text); if (enter) Native.Key(0x0D); };
        t.Start();
    }

    public bool IsForeground() { return Native.GetForegroundWindow() == FormHandle; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.F4)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

// ============================ Game pictures from Steam's store ============================
// Uses Steam's public store pages only (no account or key): a name search to find the game, then its library
// picture. Newer games no longer have the upright picture at a fixed address; for those the store's landscape
// header picture is used instead.
class CoverArt
{
    public byte[] Data; public bool Wide;

    static byte[] Get(string url, string wantType)
    {
        try
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;   // TLS 1.2
            System.Net.HttpWebRequest r = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            r.Timeout = 12000; r.ReadWriteTimeout = 12000; r.UserAgent = "Mozilla/5.0 (ControllerMedia)";
            using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)r.GetResponse())
            {
                if (resp.StatusCode != System.Net.HttpStatusCode.OK || (resp.ContentType ?? "").IndexOf(wantType, StringComparison.OrdinalIgnoreCase) < 0) return null;
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[65536]; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 8000000) return null; }
                    return ms.ToArray();
                }
            }
        }
        catch { return null; }
    }

    static string Norm(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
        return b.ToString();
    }

    // The Steam app id whose name matches, or null. A loose match is refused rather than risk a wrong picture.
    static string Search(string name)
    {
        byte[] raw = Get("https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(name) + "&l=english&cc=US", "json");
        if (raw == null) return null;
        try
        {
            Dictionary<string, object> o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
            System.Collections.IEnumerable items = o.ContainsKey("items") ? o["items"] as System.Collections.IEnumerable : null;
            if (items == null) return null;
            string want = Norm(name), loose = null;
            foreach (object it in items)
            {
                Dictionary<string, object> d = it as Dictionary<string, object>; if (d == null || !d.ContainsKey("id") || !d.ContainsKey("name")) continue;
                string n = Norm(Convert.ToString(d["name"])), id = Convert.ToString(d["id"]);
                if (n == want) return id;
                if (loose == null && want.Length >= 4 && n.Length >= 4 && (n.StartsWith(want) || want.StartsWith(n))) loose = id;
            }
            return loose;
        }
        catch { return null; }
    }

    public static CoverArt Find(string appId, string name)
    {
        if (appId == null) appId = Search(name);
        if (appId == null) return null;
        foreach (string file in new[] { "library_600x900_2x.jpg", "library_600x900.jpg" })
            foreach (string host in new[] { "https://shared.steamstatic.com/store_item_assets/steam/apps/", "https://cdn.cloudflare.steamstatic.com/steam/apps/" })
            {
                byte[] img = Get(host + appId + "/" + file, "image/");
                if (img != null) return new CoverArt { Data = img, Wide = false };
            }
        // Newer games: ask the store for the header picture's address.
        byte[] raw = Get("https://store.steampowered.com/api/appdetails?appids=" + appId + "&filters=basic", "json");
        if (raw == null) return null;
        try
        {
            Dictionary<string, object> o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
            Dictionary<string, object> app = o.ContainsKey(appId) ? o[appId] as Dictionary<string, object> : null;
            Dictionary<string, object> data = app != null && app.ContainsKey("data") ? app["data"] as Dictionary<string, object> : null;
            string url = data != null && data.ContainsKey("header_image") ? Convert.ToString(data["header_image"]) : null;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://")) return null;
            byte[] img = Get(url, "image/");
            return img == null ? null : new CoverArt { Data = img, Wide = true };
        }
        catch { return null; }
    }
}

// ============================ VLC engine (libVLC) ============================
// Videos are played by VLC's own engine when VLC media player is installed: every format VLC plays, subtitles,
// several audio tracks, and VLC's volume up to 200%. The engine is loaded from VLC's folder the first time a
// video is played, so it costs nothing until then. Without VLC the app falls back to the browser engine's player.
static class Vlc
{
    const string Lib = "libvlc.dll";
    const CallingConvention C = CallingConvention.Cdecl;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr libvlc_new(int argc, IntPtr[] argv);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_release(IntPtr inst);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr libvlc_media_new_path(IntPtr inst, byte[] path);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_release(IntPtr media);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr libvlc_media_player_new(IntPtr inst);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_set_media(IntPtr mp, IntPtr media);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_release(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_set_hwnd(IntPtr mp, IntPtr hwnd);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_media_player_play(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_set_pause(IntPtr mp, int pause);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_stop(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern long libvlc_media_player_get_time(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_media_player_set_time(IntPtr mp, long ms);
    [DllImport(Lib, CallingConvention = C)] public static extern long libvlc_media_player_get_length(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_media_player_get_state(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_audio_set_volume(IntPtr mp, int volume);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_video_set_mouse_input(IntPtr mp, uint on);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_video_set_key_input(IntPtr mp, uint on);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_video_get_spu(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_video_set_spu(IntPtr mp, int id);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr libvlc_video_get_spu_description(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_audio_get_track(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern int libvlc_audio_set_track(IntPtr mp, int id);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr libvlc_audio_get_track_description(IntPtr mp);
    [DllImport(Lib, CallingConvention = C)] public static extern void libvlc_track_description_list_release(IntPtr list);

    public const int Opening = 1, Buffering = 2, Playing = 3, Paused = 4, Stopped = 5, Ended = 6, Error = 7;

    // Where the engine is: the app's own "vlc" folder, else an installed VLC media player, else null.
    public static readonly string Dir = FindDir();
    static bool loaded;

    static string FindDir()
    {
        List<string> tries = new List<string>();
        // A copy of the engine inside the app's own folder comes first, so the app doesn't depend on VLC staying installed.
        tries.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vlc"));
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                if (k != null) tries.Add(k.GetValue("InstallDir") as string);
        }
        catch { }
        tries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"VideoLAN\VLC"));
        foreach (string d in tries)
            if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "libvlc.dll")) && Directory.Exists(Path.Combine(d, "plugins"))) return d;
        return null;
    }

    // Loads the engine from VLC's folder (0x8 = also look there for the files it depends on, and for its plugins).
    public static bool Load()
    {
        if (loaded) return true;
        if (Dir == null) return false;
        if (LoadLibraryEx(Path.Combine(Dir, "libvlccore.dll"), IntPtr.Zero, 8) == IntPtr.Zero) return false;
        if (LoadLibraryEx(Path.Combine(Dir, "libvlc.dll"), IntPtr.Zero, 8) == IntPtr.Zero) return false;
        loaded = true;
        return true;
    }

    public static byte[] Utf8(string s) { byte[] b = Encoding.UTF8.GetBytes(s); byte[] z = new byte[b.Length + 1]; b.CopyTo(z, 0); return z; }

    public static IntPtr NewInstance(params string[] args)
    {
        IntPtr[] argv = new IntPtr[args.Length];
        try
        {
            for (int i = 0; i < args.Length; i++) { byte[] b = Utf8(args[i]); argv[i] = Marshal.AllocHGlobal(b.Length); Marshal.Copy(b, 0, argv[i], b.Length); }
            return libvlc_new(args.Length, argv);
        }
        finally { foreach (IntPtr p in argv) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    static string PtrToUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
        byte[] b = new byte[n]; Marshal.Copy(p, b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    // A track list from VLC: linked list of { int id; char* name; next* }.
    public static List<KeyValuePair<int, string>> Tracks(IntPtr list)
    {
        List<KeyValuePair<int, string>> r = new List<KeyValuePair<int, string>>();
        for (IntPtr p = list; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p, 16))
            r.Add(new KeyValuePair<int, string>(Marshal.ReadInt32(p, 0), PtrToUtf8(Marshal.ReadIntPtr(p, 8))));
        if (list != IntPtr.Zero) libvlc_track_description_list_release(list);
        return r;
    }
}

// ============================ Native input output ============================
static class Native
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort vk;
        [FieldOffset(10)] public ushort scan;
        [FieldOffset(12)] public uint kflags;
        [FieldOffset(8)] public int dx;
        [FieldOffset(12)] public int dy;
        [FieldOffset(16)] public int mouseData;
        [FieldOffset(20)] public uint mflags;
        [FieldOffset(24)] public IntPtr kextra;   // keyboard dwExtraInfo
        [FieldOffset(32)] public IntPtr mextra;   // mouse dwExtraInfo
    }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    // Marker carried by simulated input from Controller Media and ControllerHome. While Controller Media is in front,
    // simulated input WITHOUT this marker (Steam's desktop controller mode) is dropped, so presses don't count twice.
    public static readonly IntPtr Marker = new IntPtr(0x434D4544);

    static void Send(INPUT i)
    {
        if (i.type == 1) i.kextra = Marker; else i.mextra = Marker;
        SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    static bool Extended(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }

    public static void KeyDown(ushort vk) { INPUT i = new INPUT(); i.type = 1; i.vk = vk; i.scan = (ushort)MapVirtualKey(vk, 0); i.kflags = Extended(vk) ? 1u : 0u; Send(i); }
    public static void KeyUp(ushort vk) { INPUT i = new INPUT(); i.type = 1; i.vk = vk; i.scan = (ushort)MapVirtualKey(vk, 0); i.kflags = (Extended(vk) ? 1u : 0u) | 2u; Send(i); }
    public static void Key(ushort vk) { KeyDown(vk); KeyUp(vk); }

    public static void TypeText(string text)
    {
        foreach (char ch in text)
        {
            INPUT d = new INPUT(); d.type = 1; d.scan = ch; d.kflags = 0x0004; Send(d);
            INPUT u = new INPUT(); u.type = 1; u.scan = ch; u.kflags = 0x0004 | 0x0002; Send(u);
        }
    }

    public static void MouseMove(int dx, int dy) { INPUT i = new INPUT(); i.dx = dx; i.dy = dy; i.mflags = 0x0001; Send(i); }
    public static void MouseButton(uint flag) { INPUT i = new INPUT(); i.mflags = flag; Send(i); }
    public static void Wheel(int amount, bool horizontal) { INPUT i = new INPUT(); i.mouseData = amount; i.mflags = horizontal ? 0x1000u : 0x0800u; Send(i); }
}

// ============================ Simulated-input filter ============================
// Low-level hooks: while Controller Media is the foreground window, drop simulated (injected) keyboard and mouse
// input that doesn't carry Native.Marker. Real keyboard and mouse input passes, and so does mouse input that
// Windows converted from touch or pen (it carries the MI_WP_SIGNATURE stamp).
static class InputFilter
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    static HookProc kbProc, msProc;   // kept referenced so they aren't garbage-collected
    static IntPtr kbHook, msHook, ownWindow;
    public static int Blocked;
    public static Action OnTouch;     // called when a real finger tap is seen

    public static void Install(IntPtr window)
    {
        ownWindow = window;
        kbProc = KeyboardHook; msProc = MouseHook;
        IntPtr mod = GetModuleHandle(null);
        kbHook = SetWindowsHookEx(13, kbProc, mod, 0); // WH_KEYBOARD_LL
        msHook = SetWindowsHookEx(14, msProc, mod, 0); // WH_MOUSE_LL
    }

    public static void Remove()
    {
        if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) UnhookWindowsHookEx(msHook);
        kbHook = msHook = IntPtr.Zero;
    }

    // Windows stamps mouse input that it converted from touch or pen with this signature (MI_WP_SIGNATURE).
    // That input can be flagged as simulated, so it must be let through or touch stops working.
    static bool FromTouchOrPen(IntPtr extra) { return (extra.ToInt64() & 0xFFFFFF00L) == 0xFF515700L; }

    static bool ShouldBlock(bool injected, IntPtr extra, bool mouse, uint flags, int code)
    {
        if (!injected || extra == Native.Marker) return false;
        if (mouse && FromTouchOrPen(extra)) return false;
        if (Native.GetForegroundWindow() != ownWindow) return false;
        LogBlocked(mouse, flags, extra, code);
        return true;
    }

    // ---- diagnostic log: the first 200 blocked events per run, in input-filter.log next to the app ----
    static readonly StringBuilder pending = new StringBuilder();
    static int logged;
    static System.Threading.Timer flusher;
    static void LogBlocked(bool mouse, uint flags, IntPtr extra, int code)
    {
        if (logged >= 200) return;
        logged++;
        lock (pending) pending.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + (mouse ? "  mouse msg 0x" : "  key vk 0x") + code.ToString("X") +
            "  flags 0x" + flags.ToString("X") + "  extra 0x" + extra.ToInt64().ToString("X"));
        if (flusher == null) flusher = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
    }
    static void Flush()
    {
        string s; lock (pending) { if (pending.Length == 0) return; s = pending.ToString(); pending.Clear(); }
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "input-filter.log"), s); } catch { }
    }

    // KBDLLHOOKSTRUCT (x64): vkCode 0, scanCode 4, flags 8, time 12, dwExtraInfo 16. LLKHF_INJECTED = 0x10.
    static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(lParam, 8);
            IntPtr extra = Marshal.ReadIntPtr(lParam, 16);
            int vk = Marshal.ReadInt32(lParam, 0);
            if (ShouldBlock((flags & 0x10) != 0, extra, false, flags, vk)) { Blocked++; return new IntPtr(1); }
        }
        return CallNextHookEx(kbHook, nCode, wParam, lParam);
    }

    // MSLLHOOKSTRUCT (x64): pt 0, mouseData 8, flags 12, time 16, dwExtraInfo 24. LLMHF_INJECTED = 0x01.
    static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(lParam, 12);
            IntPtr extra = Marshal.ReadIntPtr(lParam, 24);
            // A finger tap (touch-converted left button down) while the app is in front: tell the app touch is in use.
            if (wParam.ToInt32() == 0x0201 && FromTouchOrPen(extra) && (extra.ToInt64() & 0x80) != 0 && OnTouch != null
                && Native.GetForegroundWindow() == ownWindow) OnTouch();
            if (ShouldBlock((flags & 0x01) != 0, extra, true, flags, wParam.ToInt32())) { Blocked++; return new IntPtr(1); }
        }
        return CallNextHookEx(msHook, nCode, wParam, lParam);
    }
}

// ============================ Controller input (SDL3) ============================
class ControllerInput
{
    const string Sdl = "SDL3.dll";
    const uint SDL_INIT_GAMEPAD = 0x2000;
    const uint EV_AXIS = 0x650, EV_DOWN = 0x651, EV_UP = 0x652, EV_ADDED = 0x653, EV_REMOVED = 0x654;
    const int B_SOUTH = 0, B_EAST = 1, B_WEST = 2, B_NORTH = 3, B_BACK = 4, B_GUIDE = 5, B_START = 6, B_LSTICK = 7, B_RSTICK = 8,
              B_LB = 9, B_RB = 10, B_UP = 11, B_DOWN = 12, B_LEFT = 13, B_RIGHT = 14;

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_SetHint(string n, string v);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_Init(uint f);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] static extern void SDL_Quit();
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_WaitEventTimeout(byte[] e, int ms);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_PollEvent(byte[] e);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] static extern void SDL_CloseGamepad(IntPtr p);
    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)] static extern int SDL_GetGamepadType(IntPtr p);

    class Pad { public IntPtr Handle; public int Kind; public bool Nintendo; public float LX, LY, RX, RY, LT, RT; public bool[] Btn = new bool[32]; }

    // ---------------- button roles (what each button does) and remapping ----------------
    // By default the roles follow the letters printed on the controller. The Settings screen can give any role to
    // any button (D-pad and Home excepted); the choice is stored by button position and then used for every controller.
    public const int R_OK = 0, R_BACK = 1, R_X = 2, R_Y = 3, R_LB = 4, R_RB = 5, R_MENU = 6, R_VIEW = 7, R_LS = 8, R_RS = 9, R_COUNT = 10;
    public static readonly string[] RoleKeys = { "ok", "back", "x", "y", "lb", "rb", "menu", "view", "ls", "rs" };
    volatile int[] custom;            // null = follow the printed letters
    volatile int captureRole = -1;    // a role waiting for "press the button you want"
    long captureUntil;
    volatile Pad current;             // the controller used last (its button names are shown in the menus)

    static int[] Defaults(Pad p)
    {
        bool n = p != null && p.Nintendo;   // Nintendo pads put A on the right, Xbox/PlayStation on the bottom
        return new[] { n ? B_EAST : B_SOUTH, n ? B_SOUTH : B_EAST, n ? B_NORTH : B_WEST, n ? B_WEST : B_NORTH, B_LB, B_RB, B_START, B_BACK, B_LSTICK, B_RSTICK };
    }
    int[] Table(Pad p) { return custom ?? Defaults(p); }
    int RoleOf(Pad p, int b) { int[] t = Table(p); for (int i = 0; i < R_COUNT; i++) if (t[i] == b) return i; return -1; }

    public int[] Custom { get { return custom; } set { custom = value != null && value.Length == R_COUNT ? value : null; } }
    public string Capturing { get { int r = captureRole; return r >= 0 ? RoleKeys[r] : ""; } }
    public void BeginCapture(string role) { int i = Array.IndexOf(RoleKeys, role); if (i >= 0) { captureUntil = clock.ElapsedMilliseconds + 8000; captureRole = i; } }
    public void CancelCapture() { captureRole = -1; }

    // What the current controller calls the button in each role, for the hints and the Settings screen.
    public Dictionary<string, object> ButtonNames()
    {
        Pad p = current; int kind = p == null ? 0 : p.Kind; int[] t = Table(p);
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i < R_COUNT; i++) d[RoleKeys[i]] = NameOf(t[i], kind);
        return d;
    }

    static string NameOf(int b, int kind)
    {
        bool nin = kind >= 7 && kind <= 10, ps = kind >= 4 && kind <= 6;
        switch (b)
        {
            // ✕ cross, ○ circle, □ square, △ triangle, − minus sign
            case B_SOUTH: return nin ? "B" : ps ? "✕" : "A";
            case B_EAST: return nin ? "A" : ps ? "○" : "B";
            case B_WEST: return nin ? "Y" : ps ? "□" : "X";
            case B_NORTH: return nin ? "X" : ps ? "△" : "Y";
            case B_BACK: return nin ? "−" : ps ? "Share" : "View";
            case B_START: return nin ? "+" : ps ? "Options" : "Menu";
            case B_LSTICK: return ps ? "L3" : "LS";
            case B_RSTICK: return ps ? "R3" : "RS";
            case B_LB: return nin ? "L" : ps ? "L1" : "LB";
            case B_RB: return nin ? "R" : ps ? "R1" : "RB";
            case 15: return nin ? "Capture" : ps ? "Mute" : "Share";
            case 16: case 17: case 18: case 19: return "Paddle " + (b - 15);
            case 20: return "Touchpad";
            default: return "Button " + b;
        }
    }

    readonly MainForm form;
    readonly Dictionary<uint, Pad> pads = new Dictionary<uint, Pad>();
    volatile bool running;
    Thread thread;
    readonly Stopwatch clock = Stopwatch.StartNew();

    // navigation repeat state
    string heldDir; long nextRepeat;
    string heldPage; long nextPageRepeat;
    // browser mode state
    double fracX, fracY, wheelY, wheelX;
    bool mouseDown; ushort heldArrow; long nextArrowRepeat;
    bool ltDown, rtDown;

    public ControllerInput(MainForm f) { form = f; }

    public void Start() { running = true; thread = new Thread(Run); thread.IsBackground = true; thread.Start(); }
    public void Stop() { running = false; }

    void Ui(Action a) { try { form.BeginInvoke(a); } catch { } }
    void Emit(string name) { Ui(() => form.SendAction(name)); }

    void Run()
    {
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        if (!SDL_Init(SDL_INIT_GAMEPAD)) return;
        byte[] ev = new byte[128];
        long last = clock.ElapsedMilliseconds, awaySince = -1;
        while (running)
        {
            // A new event ends the wait at once, so the timeout only matters for things that run on a clock:
            // 8 ms while something is held (pointer movement, auto-repeat), otherwise a slow idle beat.
            bool front = form.IsForeground();
            if (SDL_WaitEventTimeout(ev, !front ? 250 : Busy() ? 8 : 100)) { Handle(ev); while (SDL_PollEvent(ev)) Handle(ev); }
            long now = clock.ElapsedMilliseconds;
            if (captureRole >= 0 && now > captureUntil) { captureRole = -1; Ui(() => form.ButtonsChanged(false)); }   // nobody pressed anything
            double dt = Math.Min(0.05, (now - last) / 1000.0); last = now;
            if (!form.IsForeground())
            {
                ReleaseBrowserInput();
                if (awaySince < 0) awaySince = now;
                else if (now - awaySince > form.SleepDelayMs && form.CanSleep) { SleepUntilFront(); awaySince = -1; last = clock.ElapsedMilliseconds; }
                continue;
            }
            awaySince = -1;
            if (form.Mode == MainForm.MODE_BROWSER) BrowserTick(dt, now); else UiTick(now);
        }
        SDL_Quit();
    }

    // Whether anything needs the fast 8 ms beat: a held direction or button, a stick off centre, a trigger down.
    bool Busy()
    {
        if (heldDir != null || heldPage != null || heldArrow != 0 || mouseDown || captureRole >= 0) return true;
        foreach (Pad p in pads.Values)
        {
            if (Math.Abs(p.LX) > 0.15f || Math.Abs(p.LY) > 0.15f || Math.Abs(p.RX) > 0.15f || Math.Abs(p.RY) > 0.15f || p.LT > 0.4f || p.RT > 0.4f) return true;
            if (p.Btn[B_UP] || p.Btn[B_DOWN] || p.Btn[B_LEFT] || p.Btn[B_RIGHT]) return true;
        }
        return false;
    }

    // The hub has been behind another window (a game) for a while and nothing is playing: let go of the
    // controllers completely and put the menu view to sleep, until the hub is in front again. That leaves the
    // controller to the game, and takes the hub's processor use to nothing.
    long quietUntil;   // just after waking, controllers are announced again; that must not switch the interface
    void SleepUntilFront()
    {
        foreach (Pad p in pads.Values) SDL_CloseGamepad(p.Handle);
        pads.Clear();
        SDL_Quit();
        Ui(() => form.SleepUi(true));
        while (running && !form.IsForeground()) Thread.Sleep(200);
        Ui(() => form.SleepUi(false));
        if (!running) return;
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        SDL_Init(SDL_INIT_GAMEPAD);
        quietUntil = clock.ElapsedMilliseconds + 2000;
    }

    void Handle(byte[] ev)
    {
        uint type = BitConverter.ToUInt32(ev, 0);
        uint id = BitConverter.ToUInt32(ev, 16);
        Pad p;
        if (type == EV_ADDED)
        {
            if (pads.ContainsKey(id)) return;
            IntPtr h = SDL_OpenGamepad(id);
            if (h == IntPtr.Zero) return;
            int kind = SDL_GetGamepadType(h);
            p = new Pad(); p.Handle = h; p.Kind = kind; p.Nintendo = kind >= 7 && kind <= 10;
            pads[id] = p;
            if (current == null) { current = p; Ui(() => form.ButtonsChanged(false)); }
            if (clock.ElapsedMilliseconds > quietUntil) Ui(() => form.SetTouchMode(false));   // a controller connected: show the controller interface
        }
        else if (type == EV_REMOVED)
        {
            if (pads.TryGetValue(id, out p))
            {
                SDL_CloseGamepad(p.Handle); pads.Remove(id); ReleaseBrowserInput();
                if (current == p) { current = null; foreach (Pad other in pads.Values) { current = other; break; } Ui(() => form.ButtonsChanged(false)); }
            }
            if (pads.Count == 0) Ui(() => form.SetTouchMode(true));   // no controllers left: touch interface
        }
        else if ((type == EV_DOWN || type == EV_UP) && pads.TryGetValue(id, out p))
        {
            int b = ev[20]; bool down = type == EV_DOWN;
            if (down && current != p && b != B_GUIDE) { current = p; Ui(() => form.ButtonsChanged(false)); }   // show this controller's button names
            if (down && captureRole >= 0 && form.IsForeground())
            {
                // Settings is waiting for "press the button you want": give that button the role. If the button
                // already had a role, the two swap, so nothing is left without a button.
                if (b == B_GUIDE || (b >= B_UP && b <= B_RIGHT)) return;
                int role = captureRole; captureRole = -1;
                int[] t = (int[])Table(p).Clone();
                int other = Array.IndexOf(t, b), old = t[role];
                t[role] = b; if (other >= 0 && other != role) t[other] = old;
                custom = t;
                if (b < 32) p.Btn[b] = true;
                Ui(() => form.ButtonsChanged(true));
                return;
            }
            if (down && b != B_GUIDE && form.TouchMode && form.IsForeground())
            {
                // First controller press after using touch: switch to the controller interface, and don't act on the press.
                Ui(() => form.SetTouchMode(false));
                return;
            }
            if (b < 32) p.Btn[b] = down;
            if (form.IsForeground()) OnButton(p, b, down);
        }
        else if (type == EV_AXIS && pads.TryGetValue(id, out p))
        {
            float v = Math.Max(-1f, BitConverter.ToInt16(ev, 24) / 32767f);
            if (Math.Abs(v) > 0.6f && form.TouchMode && form.IsForeground()) Ui(() => form.SetTouchMode(false));
            switch (ev[20])
            {
                case 0: p.LX = v; break;
                case 1: p.LY = v; break;
                case 2: p.RX = v; break;
                case 3: p.RY = v; break;
                case 4: p.LT = v; break;
                case 5: p.RT = v; break;
            }
        }
    }

    void OnButton(Pad p, int b, bool down)
    {
        if (b == B_GUIDE) return; // Home stays with Windows / Game Bar
        if (form.Mode == MainForm.MODE_BROWSER) { BrowserButton(p, b, down); return; }
        if (!down) return;
        int role = RoleOf(p, b);
        if (role >= 0) Emit(RoleKeys[role]);   // the menus use the role names as actions: ok, back, x, y, lb, rb, menu, view, ls, rs
    }

    // ---------------- app screens: directions with auto-repeat ----------------
    void UiTick(long now)
    {
        string dir = null; float bestLY = 0, bestLX = 0, ry = 0, lt = 0, rt = 0;
        foreach (Pad p in pads.Values)
        {
            if (p.Btn[B_UP]) dir = "up"; else if (p.Btn[B_DOWN]) dir = "down"; else if (p.Btn[B_LEFT]) dir = "left"; else if (p.Btn[B_RIGHT]) dir = "right";
            if (Math.Abs(p.LX) > Math.Abs(bestLX)) bestLX = p.LX;
            if (Math.Abs(p.LY) > Math.Abs(bestLY)) bestLY = p.LY;
            if (Math.Abs(p.RY) > Math.Abs(ry)) ry = p.RY;
            lt = Math.Max(lt, p.LT); rt = Math.Max(rt, p.RT);
        }
        if (dir == null)
        {
            if (Math.Abs(bestLY) > 0.55f && Math.Abs(bestLY) >= Math.Abs(bestLX)) dir = bestLY < 0 ? "up" : "down";
            else if (Math.Abs(bestLX) > 0.55f) dir = bestLX < 0 ? "left" : "right";
        }
        if (dir != heldDir) { heldDir = dir; if (dir != null) { Emit(dir); nextRepeat = now + 380; } }
        else if (dir != null && now >= nextRepeat) { Emit(dir); nextRepeat = now + 110; }

        string page = Math.Abs(ry) > 0.55f ? (ry < 0 ? "pageup" : "pagedown") : null;
        if (page != heldPage) { heldPage = page; if (page != null) { Emit(page); nextPageRepeat = now + 300; } }
        else if (page != null && now >= nextPageRepeat) { Emit(page); nextPageRepeat = now + 150; }

        bool l = lt > 0.5f, r = rt > 0.5f;
        if (l && !ltDown) Emit("lt");
        if (r && !rtDown) Emit("rt");
        ltDown = l; rtDown = r;
    }

    // ---------------- browser: cursor, scrolling, clicks ----------------
    void BrowserButton(Pad p, int b, bool down)
    {
        bool tv = form.TvMode;
        int role = RoleOf(p, b);
        if (role == R_OK)
        {
            if (tv) { if (down) Native.Key(0x0D); }
            else if (down != mouseDown) { mouseDown = down; Native.MouseButton(down ? 0x0002u : 0x0004u); }
            return;
        }
        if (!down) return;   // arrows are released in the tick
        if (role == R_BACK) { if (tv) Native.Key(0x1B); else Ui(() => form.BrowserBack()); }
        else if (role == R_X) Native.Key(0x0D);
        else if (role == R_Y) Ui(() => form.OpenPageKeyboard());
        else if (role == R_MENU) Ui(() => form.ShowBrowserMenu());
        else if (role == R_VIEW) Ui(() => form.CloseBrowser());
        else if (role == R_LB) Ui(() => form.Zoom(1 / 1.1));
        else if (role == R_RB) Ui(() => form.Zoom(1.1));
    }

    void BrowserTick(double dt, long now)
    {
        bool tv = form.TvMode;
        float lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0; ushort arrow = 0;
        foreach (Pad p in pads.Values)
        {
            if (Math.Abs(p.LX) > Math.Abs(lx)) lx = p.LX; if (Math.Abs(p.LY) > Math.Abs(ly)) ly = p.LY;
            if (Math.Abs(p.RX) > Math.Abs(rx)) rx = p.RX; if (Math.Abs(p.RY) > Math.Abs(ry)) ry = p.RY;
            lt = Math.Max(lt, p.LT); rt = Math.Max(rt, p.RT);
            if (p.Btn[B_UP]) arrow = 0x26; else if (p.Btn[B_DOWN]) arrow = 0x28; else if (p.Btn[B_LEFT]) arrow = 0x25; else if (p.Btn[B_RIGHT]) arrow = 0x27;
        }
        if (tv && arrow == 0)
        {
            if (Math.Abs(ly) > 0.55f && Math.Abs(ly) >= Math.Abs(lx)) arrow = (ushort)(ly < 0 ? 0x26 : 0x28);
            else if (Math.Abs(lx) > 0.55f) arrow = (ushort)(lx < 0 ? 0x25 : 0x27);
        }

        // Arrow keys (D-pad; also the left stick on TV sites) with auto-repeat
        if (arrow != heldArrow)
        {
            if (heldArrow != 0) Native.KeyUp(heldArrow);
            heldArrow = arrow;
            if (arrow != 0) { Native.KeyDown(arrow); nextArrowRepeat = now + 380; }
        }
        else if (arrow != 0 && now >= nextArrowRepeat) { Native.KeyDown(arrow); nextArrowRepeat = now + 90; }

        if (!tv)
        {
            // Left stick: mouse pointer
            double mag = Math.Sqrt(lx * lx + ly * ly);
            if (mag > 0.15)
            {
                double n = Math.Min(1.0, (mag - 0.15) / 0.85), speed = 1800 * n * n;
                fracX += lx / mag * speed * dt; fracY += ly / mag * speed * dt;
                int ix = (int)fracX, iy = (int)fracY;
                if (ix != 0 || iy != 0) { fracX -= ix; fracY -= iy; Native.MouseMove(ix, iy); }
            }
            // Right stick: scroll
            if (Math.Abs(ry) > 0.2) { wheelY += -ry * 1400 * dt; int w = (int)wheelY; if (w != 0) { wheelY -= w; Native.Wheel(w, false); } }
            if (Math.Abs(rx) > 0.3) { wheelX += rx * 1000 * dt; int w = (int)wheelX; if (w != 0) { wheelX -= w; Native.Wheel(w, true); } }
        }

        // Triggers: Page Up / Page Down
        bool l = lt > 0.5f, r = rt > 0.5f;
        if (l && !ltDown) Native.Key(0x21);
        if (r && !rtDown) Native.Key(0x22);
        ltDown = l; rtDown = r;
    }

    void ReleaseBrowserInput()
    {
        if (mouseDown) { mouseDown = false; Native.MouseButton(0x0004); }
        if (heldArrow != 0) { Native.KeyUp(heldArrow); heldArrow = 0; }
        heldDir = null; heldPage = null;
    }
}
