using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReelKeep.Core;

namespace ReelKeep.Host;

/// <summary>
/// The app window: a WebView2 that shows the UI (HTML/CSS/JS embedded in the exe) and a <see cref="Bridge"/>
/// connecting it to the <see cref="Engine"/>. Library files are served from a virtual host mapped to the library
/// folder, so the built-in player streams them straight from disk (with seeking).
/// </summary>
public sealed class MainWindow : Form
{
    private const string AppHost = "app.reelkeep";

    private readonly Engine _engine = new();
    private readonly WebView2 _web;
    private Bridge? _bridge;
    private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppSettings.AppName;
        BackColor = IsDark ? DarkBack : LightBack;
        MinimumSize = new Size(940, 680);
        Size = new Size(1360, 860);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }
        RestoreBounds_();

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);

        // Index the UI files embedded in the exe ("wwwroot/js/app.js" -> resource name)
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (name.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("wwwroot\\", StringComparison.OrdinalIgnoreCase))
                _assets[name.Replace('\\', '/')["wwwroot/".Length..]] = name;

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => SaveBounds();

        // "Auto" follows Windows: repaint the title bar when the user switches app mode
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        FormClosed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // =========================================================================================
    // Theme (the UI themes itself; the window matches it so there's no white flash or light title bar)
    // =========================================================================================
    private static readonly Color LightBack = Color.FromArgb(0xF2, 0xF2, 0xF3);
    private static readonly Color DarkBack = Color.FromArgb(0x16, 0x17, 0x19);

    private bool IsDark => _engine.Settings.Theme switch
    {
        "dark" => true,
        "light" => false,
        _ => SystemUsesDarkTheme(),
    };

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public void ApplyTheme()
    {
        var dark = IsDark;
        BackColor = dark ? DarkBack : LightBack;
        if (_web != null) _web.DefaultBackgroundColor = BackColor;
        if (IsHandleCreated) SetDarkTitleBar(Handle, dark);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _engine.Settings.Theme == "system" && IsHandleCreated)
            BeginInvoke(ApplyTheme);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetDarkTitleBar(Handle, IsDark);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int on = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+ / 11, 19 on older Windows 10 builds
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        // Repaint the frame now rather than on the next activation (NOMOVE|NOSIZE|NOZORDER|NOACTIVATE|FRAMECHANGED)
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(AppSettings.AppDataDir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "ReelKeep needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10/11.\n\n" +
                "If it's missing, install it from https://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message,
                AppSettings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;   // no Ctrl+R / Ctrl+F / F5 etc.
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        // The browser's own context menu only where it's useful (cut/copy/paste in text boxes)
        core.ContextMenuRequested += (_, e) => { if (!e.ContextMenuTarget.IsEditable) e.Handled = true; };

        // Serve the embedded UI
        core.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => ServeAsset(core, e);

        // Library files (videos, thumbnails) straight from disk
        Bridge.MapMediaFolder(core, _engine.Settings.LibraryFolder);

        // Never navigate away from the app; real links open in the default browser
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.DocumentTitleChanged += (_, _) => { /* keep the window title fixed */ };

        // Hand the saved theme to the page before it paints (index.html reads window.__rkTheme)
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__rkTheme = " + System.Text.Json.JsonSerializer.Serialize(_engine.Settings.Theme) + ";");

        _bridge = new Bridge(_engine, core, this);
        core.Navigate($"https://{AppHost}/index.html");

        await _engine.InitAsync(new Progress<string>(_ => { }));
    }

    private void ServeAsset(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        if (!_assets.TryGetValue(path, out var resource))
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        var headers = $"Content-Type: {ContentType(path)}\r\nCache-Control: no-cache";
        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".woff2" => "font/woff2",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { /* ignore */ }
    }

    // ---- window position ----
    private void RestoreBounds_()
    {
        var b = _engine.Settings.WindowBounds;
        if (b is { Length: 4 })
        {
            var rect = new Rectangle(b[0], b[1], b[2], b[3]);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(rect)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = rect;
            }
        }
        if (_engine.Settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _engine.Settings.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
        _engine.Settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        _engine.Settings.Save();
    }
}
