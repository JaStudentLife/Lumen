using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Lumen
{
    /// <summary>A fixed searchable thing: a Settings page, an admin tool, or a system action.</summary>
    internal sealed class CatalogEntry
    {
        public string Name, Category, Subtitle, Glyph, Target, Id;
        public System.Windows.Media.Brush Tile;
        public bool Confirm;
        public Action Run;
        public MatchTarget NameTarget;
        public List<MatchTarget> Keywords = new List<MatchTarget>();
    }

    internal static class SystemCatalog
    {
        public static readonly List<CatalogEntry> Entries = Build();

        static List<CatalogEntry> Build()
        {
            var list = new List<CatalogEntry>();
            Action<string, string, string> setting = (name, uri, keywords) =>
                list.Add(Make(name, Cat.Settings, "Settings", Glyphs.Settings, Glyphs.Gray, keywords, () => Shell.Open(uri), uri));
            Action<string, string, string, string> tool = (name, exe, args, keywords) =>
                list.Add(Make(name, Cat.Settings, "Windows tool", Glyphs.Tools, Glyphs.Indigo, keywords,
                    () => Shell.Run(exe, args.Length == 0 ? new List<string>() : args.Split('|').ToList(), null, false, true), exe));

            setting("Display", "ms-settings:display", "screen resolution scale monitor brightness hdr");
            setting("Night light", "ms-settings:nightlight", "blue light warm");
            setting("Sound", "ms-settings:sound", "audio speakers microphone output input");
            setting("Volume mixer", "ms-settings:apps-volume", "app volume audio");
            setting("Notifications", "ms-settings:notifications", "alerts toasts");
            setting("Focus / Do not disturb", "ms-settings:quiethours", "focus assist do not disturb dnd quiet hours");
            setting("Power & battery", "ms-settings:powersleep", "sleep battery energy saver screen timeout");
            setting("Storage", "ms-settings:storagesense", "disk space storage sense cleanup");
            setting("Multitasking", "ms-settings:multitasking", "snap windows virtual desktops alt tab");
            setting("Clipboard", "ms-settings:clipboard", "clipboard history");
            setting("Remote Desktop", "ms-settings:remotedesktop", "rdp");
            setting("About this PC", "ms-settings:about", "system info specs device name rename pc");
            setting("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth pair headphones devices");
            setting("Printers & scanners", "ms-settings:printers", "printer scanner");
            setting("Mouse", "ms-settings:mousetouchpad", "pointer cursor scroll");
            setting("Touchpad", "ms-settings:devices-touchpad", "trackpad gestures");
            setting("Typing", "ms-settings:typing", "keyboard autocorrect");
            setting("Network & internet", "ms-settings:network", "internet connection");
            setting("Wi-Fi", "ms-settings:network-wifi", "wifi wireless wlan");
            setting("Ethernet", "ms-settings:network-ethernet", "lan cable wired");
            setting("VPN", "ms-settings:network-vpn", "vpn");
            setting("Mobile hotspot", "ms-settings:network-mobilehotspot", "hotspot tethering share internet");
            setting("Airplane mode", "ms-settings:network-airplanemode", "flight mode");
            setting("Proxy", "ms-settings:network-proxy", "proxy");
            setting("Personalization", "ms-settings:personalization", "appearance look");
            setting("Background / wallpaper", "ms-settings:personalization-background", "wallpaper desktop background");
            setting("Colors & dark mode", "ms-settings:colors", "dark mode light mode accent color theme");
            setting("Themes", "ms-settings:themes", "theme");
            setting("Lock screen", "ms-settings:lockscreen", "lock screen");
            setting("Start menu", "ms-settings:personalization-start", "start");
            setting("Taskbar", "ms-settings:taskbar", "taskbar dock");
            setting("Fonts", "ms-settings:fonts", "fonts typefaces");
            setting("Installed apps", "ms-settings:appsfeatures", "uninstall programs apps features remove");
            setting("Default apps", "ms-settings:defaultapps", "default browser file associations open with");
            setting("Startup apps", "ms-settings:startupapps", "startup login items autostart");
            setting("Optional features", "ms-settings:optionalfeatures", "features");
            setting("Your account", "ms-settings:yourinfo", "account profile picture");
            setting("Sign-in options", "ms-settings:signinoptions", "password pin windows hello fingerprint face");
            setting("Email & accounts", "ms-settings:emailandaccounts", "accounts email");
            setting("Date & time", "ms-settings:dateandtime", "clock time zone");
            setting("Language & region", "ms-settings:regionlanguage", "language region locale keyboard layout");
            setting("Game Mode", "ms-settings:gaming-gamemode", "gaming game mode");
            setting("Accessibility", "ms-settings:easeofaccess", "ease of access");
            setting("Text size", "ms-settings:easeofaccess-display", "bigger text font size");
            setting("Privacy & security", "ms-settings:privacy", "privacy permissions");
            setting("Location", "ms-settings:privacy-location", "gps location services");
            setting("Camera permissions", "ms-settings:privacy-webcam", "webcam camera");
            setting("Microphone permissions", "ms-settings:privacy-microphone", "mic microphone");
            setting("Windows Security", "windowsdefender:", "defender antivirus virus firewall security");
            setting("Windows Update", "ms-settings:windowsupdate", "updates patch upgrade");
            setting("Recovery", "ms-settings:recovery", "reset this pc restore");
            setting("Activation", "ms-settings:activation", "product key license");
            setting("For developers", "ms-settings:developers", "developer mode sudo");
            setting("Troubleshoot", "ms-settings:troubleshoot", "fix problems");

            tool("Control Panel", "control.exe", "", "classic settings");
            tool("Device Manager", "devmgmt.msc", "", "drivers hardware");
            tool("Disk Management", "diskmgmt.msc", "", "partitions drives format");
            tool("Services", "services.msc", "", "windows services");
            tool("Event Viewer", "eventvwr.msc", "", "logs events errors");
            tool("Task Scheduler", "taskschd.msc", "", "scheduled tasks cron");
            tool("Computer Management", "compmgmt.msc", "", "management");
            tool("Registry Editor", "regedit.exe", "", "regedit registry");
            tool("System Information", "msinfo32.exe", "", "msinfo specs hardware");
            tool("Resource Monitor", "resmon.exe", "", "cpu memory disk network usage");
            tool("Environment Variables", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables", "path env variables");
            tool("System Properties", "SystemPropertiesAdvanced.exe", "", "advanced system settings performance");
            tool("Network Connections", "ncpa.cpl", "", "adapters ncpa");
            tool("Programs and Features", "appwiz.cpl", "", "uninstall appwiz");
            tool("Power Options", "powercfg.cpl", "", "power plan");
            tool("Disk Cleanup", "cleanmgr.exe", "", "clean temp files");
            tool("Character Map", "charmap.exe", "", "symbols special characters");
            tool("User Accounts", "netplwiz.exe", "", "users netplwiz");
            tool("Group Policy Editor", "gpedit.msc", "", "gpedit policy");

            Func<string, string, string, System.Windows.Media.Brush, string, bool, Action, CatalogEntry> sys =
                (name, sub, glyph, tile, keywords, confirm, run) =>
                {
                    var e = Make(name, Cat.System, sub, glyph, tile, keywords, run, "system:" + name);
                    e.Confirm = confirm; list.Add(e); return e;
                };
            sys("Lock", "Lock this PC", Glyphs.Lock, Glyphs.Blue, "lock screen away", false, () => Native.LockWorkStation());
            sys("Sleep", "Put this PC to sleep", Glyphs.Sleep, Glyphs.Indigo, "suspend standby", false, () => Native.SetSuspendState(false, false, false));
            sys("Hibernate", "Hibernate this PC", Glyphs.Sleep, Glyphs.Indigo, "hibernate", true, () => Native.SetSuspendState(true, false, false));
            sys("Shut down", "Turn off this PC", Glyphs.Power, Glyphs.Red, "shutdown power off turn off", true,
                () => Shell.Run("shutdown.exe", new List<string> { "/s", "/t", "0" }, null, false, false));
            sys("Restart", "Restart this PC", Glyphs.Restart, Glyphs.Orange, "reboot restart", true,
                () => Shell.Run("shutdown.exe", new List<string> { "/r", "/t", "0" }, null, false, false));
            sys("Sign out", "Sign out of Windows", Glyphs.SignOut, Glyphs.Orange, "log off logout sign out", true,
                () => Shell.Run("shutdown.exe", new List<string> { "/l" }, null, false, false));
            sys("Empty Recycle Bin", "Permanently delete what's in the Recycle Bin", Glyphs.Trash, Glyphs.Gray, "trash empty bin delete", false,
                () => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0));
            sys("Recycle Bin", "Open the Recycle Bin", Glyphs.Trash, Glyphs.Gray, "trash bin deleted", false, () => Shell.Open("shell:RecycleBinFolder"));
            sys("Turn off display", "Turn the screen off", Glyphs.Monitor, Glyphs.Gray, "screen off monitor off", false,
                () => Native.SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2)));
            sys("Toggle dark mode", "Switch Windows between dark and light", Glyphs.Sun, Glyphs.Purple, "dark mode light mode theme appearance", false, ToggleDarkMode);
            sys("Mute / unmute", "Toggle system volume mute", Glyphs.Mute, Glyphs.Gray, "mute sound volume", false, () => Native.TapKey(0xAD));
            return list;
        }

        static CatalogEntry Make(string name, string cat, string sub, string glyph, System.Windows.Media.Brush tile, string keywords, Action run, string id)
        {
            var e = new CatalogEntry { Name = name, Category = cat, Subtitle = sub, Glyph = glyph, Tile = tile, Run = run, Id = (cat == Cat.System ? "" : "set:") + id };
            e.NameTarget = new MatchTarget(name);
            if (!string.IsNullOrEmpty(keywords))
                foreach (var k in keywords.Split(' ').Where(k => k.Length > 1)) e.Keywords.Add(new MatchTarget(k));
            if (!string.IsNullOrEmpty(keywords)) e.Keywords.Add(new MatchTarget(keywords));
            return e;
        }

        static void ToggleDarkMode()
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using (var k = Registry.CurrentUser.OpenSubKey(key, true))
            {
                if (k == null) return;
                int light = Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1));
                int next = light == 0 ? 1 : 0;
                k.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
            }
            IntPtr res;
            Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out res);
        }

        /// <summary>"Run" results for things typed like the Run dialog: regedit, msconfig, devmgmt.msc, etc.</summary>
        public static string FindRunnable(string query)
        {
            string q = query.Trim();
            if (q.Length < 3 || q.IndexOfAny(new[] { ' ', '\\', '/', ':', '*', '?' }) >= 0) return null;
            foreach (var ext in Path.HasExtension(q) ? new[] { "" } : new[] { ".exe", ".msc", ".cpl" })
            {
                string p = Path.Combine(Environment.SystemDirectory, q + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    internal sealed class OpenWindow
    {
        public IntPtr Handle;
        public string Title, ProcessName, ExePath;
        public MatchTarget TitleTarget, ProcessTarget;
    }

    /// <summary>Snapshot of switchable top-level windows (taken each time Lumen opens).</summary>
    internal static class WindowLister
    {
        public static List<OpenWindow> Snapshot(IntPtr exclude)
        {
            var list = new List<OpenWindow>();
            var names = new Dictionary<uint, string>();
            Native.EnumWindows((h, l) =>
            {
                try
                {
                    if (h == exclude || !Native.IsWindowVisible(h)) return true;
                    if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                    if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                    int cloaked;
                    if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                    int len = Native.GetWindowTextLength(h);
                    if (len == 0) return true;
                    var sb = new System.Text.StringBuilder(len + 1);
                    Native.GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (title == "Program Manager") return true;
                    uint pid; Native.GetWindowThreadProcessId(h, out pid);
                    string exe = Native.GetProcessPath(pid);
                    string pname = exe == null ? "" : Path.GetFileNameWithoutExtension(exe);
                    if (pname.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) pname = title;
                    list.Add(new OpenWindow
                    {
                        Handle = h, Title = title, ProcessName = pname, ExePath = exe,
                        TitleTarget = new MatchTarget(title), ProcessTarget = new MatchTarget(pname)
                    });
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    internal sealed class Bookmark { public string Name, Url, Browser, BrowserExe; public MatchTarget NameTarget, UrlTarget; }

    /// <summary>Chrome and Edge bookmarks (all profiles), reloaded when the files change.</summary>
    internal static class Bookmarks
    {
        static List<Bookmark> cache = new List<Bookmark>();
        static DateTime stamp = DateTime.MinValue;
        static readonly object gate = new object();

        static IEnumerable<Tuple<string, string, string>> Sources()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                Tuple.Create("Chrome", Path.Combine(local, @"Google\Chrome\User Data"), "chrome.exe"),
                Tuple.Create("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "msedge.exe"),
                Tuple.Create("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "brave.exe")
            };
            foreach (var r in roots)
            {
                if (!Directory.Exists(r.Item2)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(r.Item2); } catch { continue; }
                foreach (var d in dirs)
                {
                    string n = Path.GetFileName(d);
                    if (n != "Default" && !n.StartsWith("Profile ")) continue;
                    string f = Path.Combine(d, "Bookmarks");
                    if (File.Exists(f)) yield return Tuple.Create(r.Item1, f, r.Item3);
                }
            }
        }

        public static List<Bookmark> All()
        {
            lock (gate)
            {
                var sources = Sources().ToList();
                DateTime newest = DateTime.MinValue;
                foreach (var s in sources) { var t = File.GetLastWriteTimeUtc(s.Item2); if (t > newest) newest = t; }
                if (newest == stamp) return cache;
                var list = new List<Bookmark>();
                foreach (var s in sources)
                {
                    try
                    {
                        var roots = Json.Obj(Json.Parse(File.ReadAllText(s.Item2)), "roots");
                        if (roots == null) continue;
                        foreach (var root in roots.Values) Collect(root, s.Item1, s.Item3, list);
                    }
                    catch (Exception ex) { Log.Error("Reading bookmarks " + s.Item2, ex); }
                }
                cache = list.GroupBy(b => b.Url).Select(g => g.First()).ToList();
                stamp = newest;
                return cache;
            }
        }

        static void Collect(object node, string browser, string exe, List<Bookmark> list)
        {
            if (list.Count > 5000) return;
            string type = Json.Str(node, "type", "");
            if (type == "url")
            {
                string url = Json.Str(node, "url", "");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
                string name = Json.Str(node, "name", url);
                list.Add(new Bookmark { Name = name, Url = url, Browser = browser, BrowserExe = exe, NameTarget = new MatchTarget(name), UrlTarget = new MatchTarget(HostOf(url)) });
                return;
            }
            var kids = Json.Arr(node, "children");
            if (kids != null) foreach (var k in kids) Collect(k, browser, exe, list);
        }

        public static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host.Replace("www.", "") : url;
        }
    }
}
