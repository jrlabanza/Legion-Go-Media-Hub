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
        if (ui == null) return;
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

        ui = new WebView2(); ui.DefaultBackgroundColor = Color.Black;
        Controls.Add(ui);   // the web browser view is created only when you open a web page (see EnsureWebAsync)
        Resize += (s, e) => LayoutViews();
        Load += async (s, e) => await InitAsync();
        FormClosing += (s, e) => { if (input != null) input.Stop(); };
    }

    async System.Threading.Tasks.Task InitAsync()
    {
        FormHandle = Handle;
        ui.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(appDir, "data"), null);
            await ui.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the WebView2 browser engine:\n" + ex.Message, "Controller Media");
            Close(); return;
        }

        CoreWebView2 c = ui.CoreWebView2;
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.SetVirtualHostNameToFolderMapping("app.cm", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        foreach (MediaRoot r in roots)
            if (Directory.Exists(r.Path)) c.SetVirtualHostNameToFolderMapping(r.Host, r.Path, CoreWebView2HostResourceAccessKind.Allow);
        c.WebMessageReceived += OnUiMessage;
        c.Navigate("https://app.cm/index.html");

        LoadSettings();
        try { adblockScript = File.ReadAllText(Path.Combine(uiDir, "adblock.js")); } catch { adblockScript = null; }
        try { tvTouchScript = File.ReadAllText(Path.Combine(uiDir, "tvtouch.js")); } catch { tvTouchScript = null; }

        HideCursor(true);
        InputFilter.Install(FormHandle);
        InputFilter.OnTouch = () => { if (!TouchMode) BeginInvoke((Action)(() => SetTouchMode(true))); };
        FormClosed += (s, e) => InputFilter.Remove();
        input = new ControllerInput(this);
        input.Start();
        ui.Focus();
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
            w.NavigationStarting += (s, e) => { blockedOnPage = 0; };
            // Ad blocker: block requests to ad/tracker domains, and inject the page script (adblock.js)
            w.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            w.WebResourceRequested += OnWebRequest;
            adblockScriptId = null;
            if (adblock) await EnableAdblockScript();
            // Touch swiping for YouTube's TV interface (it only understands arrow keys)
            if (tvTouchScript != null) await w.AddScriptToExecuteOnDocumentCreatedAsync(tvTouchScript);
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
        WebView2 w = web; web = null; adblockScriptId = null;
        Controls.Remove(w);
        w.Dispose();   // frees the page and its browser-engine process
    }

    void SetUiMemoryLow(bool low)
    {
        try { if (ui.CoreWebView2 != null) ui.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
    }

    void SendSettings()
    {
        Dictionary<string, object> m = new Dictionary<string, object>(); m["evt"] = "settings"; m["adblock"] = adblock; m["touch"] = TouchMode; Post(m);
    }

    string SettingsPath { get { return Path.Combine(appDir, "settings.json"); } }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            Dictionary<string, object> s = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath));
            object v; if (s.TryGetValue("adblock", out v) && v is bool) adblock = (bool)v;
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
            Dictionary<string, object> s = new Dictionary<string, object>(); s["adblock"] = adblock;
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
        m["evt"] = "browserMenu"; m["adblock"] = adblock; m["blocked"] = blockedOnPage;
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

    class Pad { public IntPtr Handle; public bool Nintendo; public float LX, LY, RX, RY, LT, RT; public bool[] Btn = new bool[32]; }

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
        long last = clock.ElapsedMilliseconds;
        while (running)
        {
            if (SDL_WaitEventTimeout(ev, 8)) { Handle(ev); while (SDL_PollEvent(ev)) Handle(ev); }
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Min(0.05, (now - last) / 1000.0); last = now;
            if (!form.IsForeground()) { ReleaseBrowserInput(); continue; }
            if (form.Mode == MainForm.MODE_BROWSER) BrowserTick(dt, now); else UiTick(now);
        }
        SDL_Quit();
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
            p = new Pad(); p.Handle = h; p.Nintendo = kind >= 7 && kind <= 10;
            pads[id] = p;
            Ui(() => form.SetTouchMode(false));          // a controller connected: show the controller interface
        }
        else if (type == EV_REMOVED)
        {
            if (pads.TryGetValue(id, out p)) { SDL_CloseGamepad(p.Handle); pads.Remove(id); ReleaseBrowserInput(); }
            if (pads.Count == 0) Ui(() => form.SetTouchMode(true));   // no controllers left: touch interface
        }
        else if ((type == EV_DOWN || type == EV_UP) && pads.TryGetValue(id, out p))
        {
            int b = ev[20]; bool down = type == EV_DOWN;
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

    // Buttons by printed label: Nintendo pads put A on the right, Xbox/PlayStation on the bottom.
    static int ConfirmButton(Pad p) { return p.Nintendo ? B_EAST : B_SOUTH; }
    static int BackButton(Pad p) { return p.Nintendo ? B_SOUTH : B_EAST; }
    static int XButton(Pad p) { return p.Nintendo ? B_NORTH : B_WEST; }
    static int YButton(Pad p) { return p.Nintendo ? B_WEST : B_NORTH; }

    void OnButton(Pad p, int b, bool down)
    {
        if (b == B_GUIDE) return; // Home stays with Windows / Game Bar
        if (form.Mode == MainForm.MODE_BROWSER) { BrowserButton(p, b, down); return; }
        if (!down) return;
        if (b == ConfirmButton(p)) Emit("ok");
        else if (b == BackButton(p)) Emit("back");
        else if (b == XButton(p)) Emit("x");
        else if (b == YButton(p)) Emit("y");
        else if (b == B_START) Emit("menu");
        else if (b == B_BACK) Emit("view");
        else if (b == B_LB) Emit("lb");
        else if (b == B_RB) Emit("rb");
        else if (b == B_LSTICK) Emit("ls");
        else if (b == B_RSTICK) Emit("rs");
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
        if (b == ConfirmButton(p))
        {
            if (tv) { if (down) Native.Key(0x0D); }
            else if (down != mouseDown) { mouseDown = down; Native.MouseButton(down ? 0x0002u : 0x0004u); }
            return;
        }
        if (!down) { if (b >= B_UP && b <= B_RIGHT) { /* arrows released in tick */ } return; }
        if (b == BackButton(p)) { if (tv) Native.Key(0x1B); else Ui(() => form.BrowserBack()); }
        else if (b == XButton(p)) Native.Key(0x0D);
        else if (b == YButton(p)) Ui(() => form.OpenPageKeyboard());
        else if (b == B_START) Ui(() => form.ShowBrowserMenu());
        else if (b == B_BACK) Ui(() => form.CloseBrowser());
        else if (b == B_LB) Ui(() => form.Zoom(1 / 1.1));
        else if (b == B_RB) Ui(() => form.Zoom(1.1));
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
