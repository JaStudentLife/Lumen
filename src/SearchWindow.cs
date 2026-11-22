using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lumen
{
    /// <summary>The Spotlight-style search panel.</summary>
    internal sealed class SearchWindow
    {
        public readonly Window Win;
        readonly SearchEngine engine;
        readonly TextBox query;
        readonly ListBox list;
        readonly FrameworkElement body, divider, footer, chip, preview, pvGlyphTile;
        readonly TextBlock placeholder, emptyHint, footerLeft, footerRight, chipText, pvTitle, pvKind, pvNote, pvGlyph;
        readonly Image chipIcon, pvImage;
        readonly ItemsControl pvDetails;
        readonly ColumnDefinition previewColumn;
        readonly DispatcherTimer fileTimer, armTimer, footerTimer;
        IntPtr hwnd;
        int version;
        bool userMoved, backdrop;
        SearchResult current = new SearchResult();
        List<FileHit> fileNames, fileContents;
        CancellationTokenSource cts;

        public event Action HotkeyPressed;
        uint hkMods, hkVk;

        public SearchWindow(SearchEngine engine)
        {
            this.engine = engine;
            Win = (Window)XamlReader.Parse(WindowXaml.Markup);
            Func<string, object> f = n => Win.FindName(n);
            query = (TextBox)f("Query"); list = (ListBox)f("List");
            body = (FrameworkElement)f("Body"); divider = (FrameworkElement)f("Divider"); footer = (FrameworkElement)f("Footer");
            chip = (FrameworkElement)f("Chip"); preview = (FrameworkElement)f("Preview"); pvGlyphTile = (FrameworkElement)f("PvGlyphTile");
            placeholder = (TextBlock)f("Placeholder"); emptyHint = (TextBlock)f("EmptyHint");
            footerLeft = (TextBlock)f("FooterLeft"); footerRight = (TextBlock)f("FooterRight"); chipText = (TextBlock)f("ChipText");
            pvTitle = (TextBlock)f("PvTitle"); pvKind = (TextBlock)f("PvKind"); pvNote = (TextBlock)f("PvNote"); pvGlyph = (TextBlock)f("PvGlyph");
            chipIcon = (Image)f("ChipIcon"); pvImage = (Image)f("PvImage"); pvDetails = (ItemsControl)f("PvDetails");
            previewColumn = (ColumnDefinition)f("PreviewColumn");

            fileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            fileTimer.Tick += (s, e) => { fileTimer.Stop(); StartFileSearch(); };
            armTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            armTimer.Tick += (s, e) => { armTimer.Stop(); Disarm(); };
            footerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            footerTimer.Tick += (s, e) => { footerTimer.Stop(); UpdateFooter(Selected); };

            query.TextChanged += (s, e) => RunSearch();
            Win.PreviewKeyDown += OnKey;
            Win.Deactivated += (s, e) => { if (Settings.Current.HideOnFocusLoss) Hide(); };
            Win.Closing += (s, e) => { e.Cancel = true; Hide(); };
            list.PreviewMouseLeftButtonDown += OnListMouse;
            list.SelectionChanged += (s, e) => { Disarm(); UpdatePreview(Selected); UpdateFooter(Selected); };
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    Win.Dispatcher.BeginInvoke(new Action(ApplyTheme));
            };
            ApplyLayoutSettings();
        }

        ResultItem Selected { get { return list.SelectedItem as ResultItem; } }
        public bool IsOpen { get { return Win.IsVisible; } }
        public IntPtr Handle { get { return hwnd; } }

        // ------------------------------------------------------------------ window setup
        public void Initialize()
        {
            hwnd = new WindowInteropHelper(Win).EnsureHandle();
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            backdrop = Environment.OSVersion.Version.Build >= 22000;
            if (backdrop)
            {
                WindowChrome.SetWindowChrome(Win, new WindowChrome
                {
                    CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
                });
                var src = HwndSource.FromHwnd(hwnd);
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                int type = 3; // DWMSBT_TRANSIENTWINDOW = acrylic
                backdrop = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) == 0;
                int corner = 2; // round
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            }
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            ApplyTheme();
            Log.Info("Window ready, acrylic backdrop: " + backdrop);
        }

        public void ApplyLayoutSettings()
        {
            Win.Width = Settings.Current.Width;
            bool pv = Settings.Current.ShowPreview;
            previewColumn.Width = new GridLength(pv ? Math.Round(Settings.Current.Width * 0.38) : 0);
            preview.Visibility = pv ? Visibility.Visible : Visibility.Collapsed;
        }

        static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k == null || Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return true; }
        }

        static Color AccentColor()
        {
            string s = Settings.Current.Accent;
            if (!string.IsNullOrEmpty(s) && s.StartsWith("#"))
                try { return (Color)ColorConverter.ConvertFromString(s); } catch { }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        uint abgr = unchecked((uint)(int)v);
                        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                    }
                }
            }
            catch { }
            return Color.FromRgb(0x0A, 0x84, 0xFF);
        }

        public void ApplyTheme()
        {
            string t = Settings.Current.Theme;
            bool dark = t == "dark" || (t != "light" && SystemIsDark());
            Action<string, string> set = (key, hex) =>
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze();
                Win.Resources[key] = b;
            };
            if (dark)
            {
                set("Fg", "#F2FFFFFF"); set("Fg2", "#A3FFFFFF"); set("Fg3", "#7AFFFFFF"); set("Line", "#1CFFFFFF");
                set("Hover", "#12FFFFFF"); set("PreviewBg", "#08FFFFFF"); set("Chip", "#1FFFFFFF");
                set("Surface", backdrop ? "#2E000000" : "#FF252528");
            }
            else
            {
                set("Fg", "#E6000000"); set("Fg2", "#99000000"); set("Fg3", "#70000000"); set("Line", "#17000000");
                set("Hover", "#0D000000"); set("PreviewBg", "#06000000"); set("Chip", "#12000000");
                set("Surface", backdrop ? "#33FFFFFF" : "#FFF6F6F8");
            }
            Color a = AccentColor();
            var accent = new SolidColorBrush(Color.FromArgb(0xEE, a.R, a.G, a.B)); accent.Freeze();
            Win.Resources["Accent"] = accent;
            double lum = (0.2126 * a.R + 0.7152 * a.G + 0.0722 * a.B) / 255.0;
            set("OnAccent", lum > 0.62 ? "#F0000000" : "#FFFFFFFF");
            set("OnAccent2", lum > 0.62 ? "#B3000000" : "#D9FFFFFF");
            if (hwnd != IntPtr.Zero)
            {
                int v = dark ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
            }
        }

        // ------------------------------------------------------------------ hotkey
        public bool RegisterHotkey(string spec)
        {
            if (hkVk != 0) Native.UnregisterHotKey(hwnd, 1);
            hkVk = 0;
            uint mods, vk;
            if (!ParseHotkey(spec, out mods, out vk)) return false;
            if (!Native.RegisterHotKey(hwnd, 1, mods | Native.MOD_NOREPEAT, vk)) return false;
            hkMods = mods; hkVk = vk;
            return true;
        }

        public static bool ParseHotkey(string spec, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var raw in (spec ?? "").Split('+'))
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p.Length == 0) continue;
                if (p == "ctrl" || p == "control") mods |= Native.MOD_CONTROL;
                else if (p == "alt") mods |= Native.MOD_ALT;
                else if (p == "shift") mods |= Native.MOD_SHIFT;
                else if (p == "win" || p == "windows") mods |= Native.MOD_WIN;
                else
                {
                    Key k;
                    if (p.Length == 1 && char.IsDigit(p[0])) k = Key.D0 + (p[0] - '0');
                    else if (!Enum.TryParse(p, true, out k)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                }
            }
            return vk != 0 && mods != 0;
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && w.ToInt32() == 1)
            {
                handled = true;
                if (!Win.IsVisible && PassThrough()) return IntPtr.Zero;
                var hk = HotkeyPressed; if (hk != null) hk();
            }
            return IntPtr.Zero;
        }

        /// <summary>For apps listed in passThroughApps, give them the keystroke instead (e.g. VS Code's Ctrl+Space).</summary>
        bool PassThrough()
        {
            var apps = Settings.Current.PassThroughApps;
            if (apps == null || apps.Count == 0) return false;
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            string exe = System.IO.Path.GetFileName(Native.GetProcessPath(pid) ?? "");
            if (!apps.Any(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase) || a.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase))) return false;
            Native.UnregisterHotKey(hwnd, 1);
            Native.keybd_event((byte)hkVk, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)hkVk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (s, e) => { t.Stop(); Native.RegisterHotKey(hwnd, 1, hkMods | Native.MOD_NOREPEAT, hkVk); };
            t.Start();
            return true;
        }

        // ------------------------------------------------------------------ show / hide
        public void Toggle() { if (Win.IsVisible) Hide(); else Show(null); }

        public void Show(string preset)
        {
            engine.Windows = WindowLister.Snapshot(hwnd);
            engine.Apps.RefreshIfStale(TimeSpan.FromMinutes(10));
            if (preset != null) query.Text = preset;
            else if (!Settings.Current.RememberLastQuery) query.Text = "";
            Position();
            Win.Show();
            Position();
            Win.Activate();
            Native.SetForegroundWindow(hwnd);
            query.Focus();
            Keyboard.Focus(query);
            if (preset != null) query.CaretIndex = query.Text.Length; else query.SelectAll();
            RunSearch();
        }

        public void Hide()
        {
            if (!Win.IsVisible) return;
            if (cts != null) cts.Cancel();
            fileTimer.Stop();
            Disarm();
            Win.Hide();
        }

        public void SetQuery(string text)
        {
            if (!Win.IsVisible) { Show(text); return; }
            query.Text = text;
            query.CaretIndex = text.Length;
            query.Focus();
        }

        void Position()
        {
            Native.POINT pt; Native.GetCursorPos(out pt);
            IntPtr mon = Native.MonitorFromPoint(pt, 2);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(mon, ref mi)) return;
            uint dx, dy; double scale = 1;
            if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0) scale = dx / 96.0;
            int w = (int)Math.Round(Win.Width * scale);
            int workW = mi.rcWork.Right - mi.rcWork.Left, workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int x = mi.rcWork.Left + (workW - w) / 2;
            int y = mi.rcWork.Top + (int)(workH * 0.2);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------ searching
        public void RunSearch()
        {
            version++;
            if (cts != null) { cts.Cancel(); cts = null; }
            fileTimer.Stop();
            userMoved = false;
            string text = query.Text;
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            try { current = engine.Search(text); }
            catch (Exception ex) { Log.Error("Search failed for '" + text + "'", ex); current = new SearchResult { Query = text }; }
            fileNames = null; fileContents = null;
            Display(engine.Compose(current, null, null), false);
            UpdateChip();
            if (current.WantsFiles) fileTimer.Start();
        }

        void StartFileSearch()
        {
            int v = version;
            var c = new CancellationTokenSource(); cts = c;
            var snapshot = current;
            int max = Settings.Current.MaxFileResults;
            bool contents = Settings.Current.SearchFileContents;
            string text = snapshot.Query;
            Task.Run(() =>
            {
                try
                {
                    var names = engine.Files.SearchNames(text, max, c.Token);
                    foreach (var h in names.Take(max + 6)) FileSearcher.Enrich(h);
                    if (c.IsCancellationRequested) return;
                    Win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (v != version) return;
                        fileNames = names;
                        Display(engine.Compose(snapshot, fileNames, fileContents), true);
                    }));
                    if (!contents || c.IsCancellationRequested) return;
                    var more = engine.Files.SearchContents(text, 6, c.Token);
                    foreach (var h in more.Take(10)) FileSearcher.Enrich(h);
                    if (c.IsCancellationRequested) return;
                    Win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (v != version) return;
                        fileContents = more;
                        Display(engine.Compose(snapshot, fileNames, fileContents), true);
                    }));
                }
                catch (Exception ex) { Log.Error("File search", ex); }
            });
        }

        void Display(List<ResultItem> items, bool keep)
        {
            string selectedId = keep && userMoved && Selected != null ? Selected.Id : null;
            list.ItemsSource = items;
            bool any = items.Any(i => !i.IsHeader);
            var vis = any ? Visibility.Visible : Visibility.Collapsed;
            body.Visibility = vis; divider.Visibility = vis; footer.Visibility = vis;
            ResultItem sel = selectedId == null ? null : items.FirstOrDefault(i => i.Id == selectedId && !i.IsHeader);
            if (sel == null) sel = items.FirstOrDefault(i => !i.IsHeader);
            list.SelectedItem = sel;
            if (sel != null) list.ScrollIntoView(sel);
            if (sel == null) { UpdatePreview(null); UpdateFooter(null); }
            LoadIcons(items);
        }

        int IconPx()
        {
            double s = 1;
            try { s = VisualTreeHelper.GetDpi(Win).DpiScaleX; } catch { }
            return s > 1.25 ? 64 : s > 1 ? 48 : 32;
        }

        void LoadIcons(List<ResultItem> items)
        {
            int px = IconPx();
            foreach (var it in items)
            {
                if (it.IsHeader || it.Icon != null || string.IsNullOrEmpty(it.IconPath)) continue;
                var cached = IconLoader.Cached(it.IconPath, px, false);
                if (cached != null) { it.Icon = cached; continue; }
                var target = it;
                IconLoader.Get(it.IconPath, px, false, img => target.Icon = img);
            }
        }

        void UpdateChip()
        {
            bool mode = current.CommandMode && current.ModeSet != null;
            chip.Visibility = mode ? Visibility.Visible : Visibility.Collapsed;
            emptyHint.Visibility = !mode && query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!mode) return;
            chipText.Text = current.ModeSet.Name;
            chipIcon.Source = null;
            string icon = engine.Commands.IconFor(current.ModeSet, engine.Apps, engine.AppsVersion);
            chipIcon.Visibility = icon == null ? Visibility.Collapsed : Visibility.Visible;
            if (icon != null)
            {
                var c = IconLoader.Cached(icon, 32, false);
                if (c != null) chipIcon.Source = c;
                else IconLoader.Get(icon, 32, false, img => chipIcon.Source = img);
            }
        }

        // ------------------------------------------------------------------ preview + footer
        void UpdatePreview(ResultItem it)
        {
            if (it == null || !Settings.Current.ShowPreview)
            {
                pvTitle.Text = ""; pvKind.Text = ""; pvNote.Text = ""; pvDetails.ItemsSource = null;
                pvImage.Source = null; pvGlyphTile.Visibility = Visibility.Collapsed;
                return;
            }
            pvTitle.Text = it.Title;
            pvKind.Text = it.Kind ?? it.Category;
            pvDetails.ItemsSource = it.Details;
            pvNote.Text = it.Note ?? "";
            pvNote.Visibility = string.IsNullOrEmpty(it.Note) ? Visibility.Collapsed : Visibility.Visible;
            pvImage.Source = null;
            string path = it.PreviewImagePath ?? it.IconPath;
            if (!string.IsNullOrEmpty(path))
            {
                pvGlyphTile.Visibility = Visibility.Collapsed;
                bool thumb = it.PreviewImagePath != null;
                pvImage.Width = thumb ? double.NaN : 72; pvImage.Height = thumb ? double.NaN : 72;
                var cached = IconLoader.Cached(path, 256, thumb);
                if (cached != null) pvImage.Source = cached;
                else IconLoader.Get(path, 256, thumb, img => { if (Selected == it) pvImage.Source = img; });
                // Show the small icon right away while the big one loads.
                if (pvImage.Source == null && it.Icon != null) pvImage.Source = it.Icon;
            }
            else
            {
                pvGlyphTile.Visibility = string.IsNullOrEmpty(it.Glyph) ? Visibility.Collapsed : Visibility.Visible;
                ((System.Windows.Controls.Border)pvGlyphTile).Background = it.GlyphBrush ?? Glyphs.Gray;
                pvGlyph.Text = it.Glyph ?? "";
            }
        }

        void UpdateFooter(ResultItem it)
        {
            if (it == null) { footerLeft.Text = ""; footerRight.Text = ""; return; }
            var left = "↵  " + (it.Armed ? "Confirm" : it.ActionName ?? "Open");
            if (it.AltActionName != null) left += "      Ctrl+↵  " + it.AltActionName;
            if (it.SupportsAdmin) left += "      Ctrl+Shift+↵  Run as admin";
            footerLeft.Text = left;
            var right = new List<string>();
            if (!string.IsNullOrEmpty(it.AutoComplete)) right.Add("Tab  " + (it.Category == Cat.Apps || it.Category == Cat.TopHit && it.Id.StartsWith("app:") ? "Commands" : "Complete"));
            if (it.CopyText != null) right.Add("Ctrl+C  Copy");
            footerRight.Text = string.Join("      ", right);
        }

        void Flash(string text)
        {
            footerLeft.Text = text; footerRight.Text = "";
            footerTimer.Stop(); footerTimer.Start();
        }

        void Disarm()
        {
            if (list.ItemsSource == null) return;
            foreach (ResultItem i in list.ItemsSource) if (i.Armed) i.Armed = false;
        }

        // ------------------------------------------------------------------ input
        void OnKey(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    if (query.Text.Length > 0) query.Text = ""; else Hide();
                    e.Handled = true; break;
                case Key.Down:
                    if (query.Text.Length == 0 && !list.Items.Cast<ResultItem>().Any(i => !i.IsHeader)) ShowRecent();
                    else if (ctrl) JumpGroup(1); else Move(1);
                    e.Handled = true; break;
                case Key.Up: if (ctrl) JumpGroup(-1); else Move(-1); e.Handled = true; break;
                case Key.PageDown: Move(7); e.Handled = true; break;
                case Key.PageUp: Move(-7); e.Handled = true; break;
                case Key.Enter:
                    Execute(ctrl && shift ? ExecMode.Admin : ctrl ? ExecMode.Alternate : ExecMode.Primary, Selected);
                    e.Handled = true; break;
                case Key.Tab:
                    Complete(); e.Handled = true; break;
                case Key.C:
                    if (ctrl && query.SelectionLength == 0 && Selected != null)
                    {
                        if (shift && Selected.FilePath != null) { Shell.CopyFile(Selected.FilePath); Flash("Copied file"); }
                        else { Shell.CopyText(Selected.CopyText ?? Selected.Title); Flash("Copied  " + Trim(Selected.CopyText ?? Selected.Title, 70)); }
                        e.Handled = true;
                    }
                    break;
                case Key.OemComma:
                    if (ctrl) { Hide(); Shell.Edit(Paths.SettingsFile); e.Handled = true; }
                    break;
                default:
                    if (ctrl && key >= Key.D1 && key <= Key.D9)
                    {
                        var rows = list.Items.Cast<ResultItem>().Where(i => !i.IsHeader).ToList();
                        int n = key - Key.D1;
                        if (n < rows.Count) Execute(ExecMode.Primary, rows[n]);
                        e.Handled = true;
                    }
                    break;
            }
        }

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        void ShowRecent()
        {
            var r = new SearchResult();
            r.Items.AddRange(engine.RecentItems());
            Display(engine.Compose(r, null, null), false);
        }

        void Move(int delta)
        {
            var rows = list.Items.Cast<ResultItem>().Where(i => !i.IsHeader).ToList();
            if (rows.Count == 0) return;
            int idx = rows.IndexOf(Selected);
            int next = idx < 0 ? 0 : idx + delta;
            if (Math.Abs(delta) == 1) next = (next + rows.Count) % rows.Count;
            else next = Math.Max(0, Math.Min(rows.Count - 1, next));
            list.SelectedItem = rows[next];
            list.ScrollIntoView(rows[next]);
            userMoved = true;
        }

        void JumpGroup(int dir)
        {
            var all = list.Items.Cast<ResultItem>().ToList();
            int idx = all.IndexOf(Selected);
            if (idx < 0) return;
            int i = idx + dir;
            // Find the next header in that direction, then its first row.
            while (i >= 0 && i < all.Count && !all[i].IsHeader) i += dir;
            if (dir < 0)
            {
                i--; // skip the current section's header; find the previous header
                while (i >= 0 && !all[i].IsHeader) i--;
            }
            if (i < 0 || i >= all.Count) return;
            var target = all.Skip(i + 1).FirstOrDefault(r => !r.IsHeader);
            if (target == null) return;
            list.SelectedItem = target; list.ScrollIntoView(target); userMoved = true;
        }

        void Complete()
        {
            var it = Selected;
            if (it == null) return;
            if (!string.IsNullOrEmpty(it.AutoComplete)) SetQuery(it.AutoComplete);
        }

        void OnListMouse(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;
            var it = item.DataContext as ResultItem;
            if (it == null || it.IsHeader) return;
            list.SelectedItem = it;
            userMoved = true;
            if (e.ClickCount >= 2) Execute(ExecMode.Primary, it);
            e.Handled = true;
            query.Focus();
        }

        void Execute(ExecMode mode, ResultItem it)
        {
            if (it == null || it.IsHeader || it.Execute == null) return;
            if (it.Confirm && !it.Armed)
            {
                it.Armed = true; UpdateFooter(it);
                armTimer.Stop(); armTimer.Start();
                return;
            }
            if (mode == ExecMode.Admin && !it.SupportsAdmin) mode = ExecMode.Primary;
            if (mode == ExecMode.Alternate && it.AltActionName == null) mode = ExecMode.Primary;
            string text = query.Text;
            if (!it.KeepOpen) engine.Usage.Record(text, it.Id);
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            try { it.Execute(mode); }
            catch (Exception ex)
            {
                Log.Error("Running " + it.Id, ex);
                Shell.Report("Couldn't open " + it.Title, ex.Message);
            }
            if (!it.KeepOpen) Hide();
        }
    }
}
