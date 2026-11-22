using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Lumen")]
[assembly: AssemblyProduct("Lumen")]
[assembly: AssemblyDescription("Spotlight-style launcher for Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Lumen
{
    internal sealed class Options
    {
        public bool Show, Background, Headless, DryRun, Exec, Quit, Reindex, Files;
        public string Query, Out;
        public List<string> Queries = new List<string>();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                Func<string> next = () => i + 1 < args.Length ? args[++i] : "";
                switch (a)
                {
                    case "--show": o.Show = true; break;
                    case "--background": o.Background = true; break;
                    case "--headless": o.Headless = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--exec": o.Exec = true; break;
                    case "--quit": o.Quit = true; break;
                    case "--reindex": o.Reindex = true; break;
                    case "--files": o.Files = true; break;
                    case "--out": o.Out = next(); break;
                    case "--query": o.Query = next(); o.Queries.Add(o.Query); o.Show = true; break;
                }
            }
            return o;
        }
    }

    internal sealed class LumenApp : ILumenHost
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string PipeName = "Lumen.Launcher." + Process.GetCurrentProcess().SessionId;

        SearchEngine engine;
        SearchWindow window;
        AppLearner learner;
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem startupItem;
        Dispatcher ui;
        FileSystemWatcher configWatcher;
        DispatcherTimer reloadTimer;
        string registeredHotkey;

        [STAThread]
        static int Main(string[] args)
        {
            var opts = Options.Parse(args);
            try { Paths.Ensure(); } catch { }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
            if (opts.Headless) return Headless.Run(opts);

            bool created;
            var mutex = new Mutex(true, @"Local\Lumen.Launcher.SingleInstance", out created);
            if (!created)
            {
                SendToRunningInstance(args.Length == 0 ? new[] { "--show" } : args);
                return 0;
            }
            if (opts.Quit) return 0;
            new LumenApp().Run(opts);
            GC.KeepAlive(mutex);
            return 0;
        }

        static void SendToRunningInstance(string[] args)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(3000);
                    var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
                    c.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex) { Log.Error("Signalling the running instance", ex); }
        }

        void Run(Options opts)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ui = app.Dispatcher;
            app.DispatcherUnhandledException += (s, e) => { Log.Error("UI exception", e.Exception); e.Handled = true; };
            Forms.Application.EnableVisualStyles();

            bool firstRun = false;
            try { firstRun = Settings.EnsureDefaultFiles(); } catch (Exception ex) { Log.Error("Writing default config", ex); }
            Settings.Current = Settings.Load();

            engine = new SearchEngine { Host = this };
            engine.Commands = CommandRegistry.Load(Paths.CommandsFile);
            engine.Apps.SetAliases(engine.Commands.AppAliases);
            engine.Apps.LoadCache();
            engine.Usage.Load();
            engine.Apps.Changed += () => ui.BeginInvoke(new Action(() => engine.AppsVersion++));
            CommandRegistry.AppsSource = engine.Apps;
            learner = new AppLearner(engine.Apps);
            engine.Apps.Changed += () => { try { learner.CheckForNewApps(); } catch (Exception ex) { Log.Error("New-app check", ex); } };
            // Store apps don't touch the Start menu folders, so also re-scan every 10 minutes.
            var rescan = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            rescan.Tick += (s, e) => engine.Apps.RefreshAsync();
            rescan.Start();
            IconLoader.Init(ui);

            window = new SearchWindow(engine);
            window.Initialize();
            window.HotkeyPressed += () => window.Toggle();
            Shell.Notify += (title, msg) => ui.BeginInvoke(new Action(() => Balloon(title, msg)));

            var sw = Stopwatch.StartNew();
            RegisterHotkey();
            StartPipeServer();
            Log.Info("hotkey+pipe " + sw.ElapsedMilliseconds + " ms");
            CreateTray();
            Log.Info("tray " + sw.ElapsedMilliseconds + " ms");
            engine.Apps.RefreshAsync();
            engine.Apps.WatchStartMenu();
            WatchConfig();
            Log.Info("watchers " + sw.ElapsedMilliseconds + " ms");
            WarmUp();
            Log.Info("warm-up " + sw.ElapsedMilliseconds + " ms");

            if (Settings.LastError != null) Balloon("Lumen settings problem", Settings.LastError);
            if (engine.Commands.LoadError != null) Balloon("Lumen commands problem", engine.Commands.LoadError);
            if (firstRun)
                Balloon("Lumen is running", "Press " + Settings.Current.Hotkey + " to search. Type an app name and “-” to see its commands.");

            if (opts.Show || (!opts.Background && !firstRun)) ui.BeginInvoke(new Action(() => window.Show(opts.Query)), DispatcherPriority.ApplicationIdle);
            else if (firstRun && !opts.Background) ui.BeginInvoke(new Action(() => window.Show(null)), DispatcherPriority.ApplicationIdle);
            Log.Info("Lumen started (" + Paths.ExePath + ")");
            app.Run();
        }

        void RegisterHotkey()
        {
            string hk = Settings.Current.Hotkey;
            if (hk == registeredHotkey) return;
            if (window.RegisterHotkey(hk)) { registeredHotkey = hk; Log.Info("Hotkey registered: " + hk); }
            else
            {
                registeredHotkey = null;
                Balloon("Couldn't use " + hk, "Another app already uses that shortcut (or it isn't valid). Change \"hotkey\" in settings.json.");
            }
            if (tray != null) tray.Text = "Lumen — " + hk;
        }

        /// <summary>Renders the window once off-screen so the first real open is instant.</summary>
        void WarmUp()
        {
            try
            {
                // JIT the search path too, so the first keystroke is as fast as the rest.
                engine.Compose(engine.Search("ex"), null, null);
                engine.Compose(engine.Search("excel - n"), null, null);
                window.Win.ShowActivated = false;
                window.Win.Left = -20000; window.Win.Top = -20000;
                window.Win.Show();
                window.Win.UpdateLayout();
                window.Win.Hide();
                window.Win.ShowActivated = true;
            }
            catch (Exception ex) { Log.Error("Warm-up", ex); }
        }

        // ---------------------------------------------------------------- tray
        void CreateTray()
        {
            var sw = Stopwatch.StartNew();
            tray = new Forms.NotifyIcon { Text = "Lumen" };
            try
            {
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumen.ico"))
                    tray.Icon = st != null ? new System.Drawing.Icon(st, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
            }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            Log.Info("  tray icon " + sw.ElapsedMilliseconds + " ms");
            tray.Visible = true;
            Log.Info("  tray visible " + sw.ElapsedMilliseconds + " ms");
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Lumen", null, (s, e) => ui.BeginInvoke(new Action(() => window.Show(null))));
            menu.Items.Add("Settings…", null, (s, e) => Shell.Edit(Paths.SettingsFile));
            menu.Items.Add("Edit app commands…", null, (s, e) => Shell.Edit(Paths.CommandsFile));
            menu.Items.Add("Open config folder", null, (s, e) => Shell.Open(Paths.ConfigDir));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Rebuild app index", null, (s, e) => Reindex());
            startupItem = new Forms.ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup()) { Checked = StartupEnabled };
            menu.Items.Add(startupItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit Lumen", null, (s, e) => Quit());
            menu.Opening += (s, e) => startupItem.Checked = StartupEnabled;
            Log.Info("  tray menu " + sw.ElapsedMilliseconds + " ms");
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ui.BeginInvoke(new Action(() => window.Show(null))); };
        }

        void Balloon(string title, string msg)
        {
            if (tray == null) return;
            try { tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(msg) ? " " : msg, Forms.ToolTipIcon.None); } catch { }
        }

        // ---------------------------------------------------------------- config reload
        void WatchConfig()
        {
            reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); ReloadConfig(); };
            configWatcher = new FileSystemWatcher(Paths.ConfigDir, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (s, e) => ui.BeginInvoke(new Action(() => { reloadTimer.Stop(); reloadTimer.Start(); }));
            configWatcher.Changed += h; configWatcher.Created += h;
            configWatcher.Renamed += (s, e) => ui.BeginInvoke(new Action(() => { reloadTimer.Stop(); reloadTimer.Start(); }));
            configWatcher.EnableRaisingEvents = true;
        }

        void ReloadConfig()
        {
            Settings.Current = Settings.Load();
            if (Settings.LastError != null) Balloon("Lumen settings problem", Settings.LastError);
            var reg = CommandRegistry.Load(Paths.CommandsFile);
            if (reg.LoadError != null) Balloon("Lumen commands problem", reg.LoadError + " — still using the last good version.");
            else
            {
                engine.Commands = reg;
                engine.Apps.SetAliases(reg.AppAliases);
                engine.AppsVersion++;
            }
            RegisterHotkey();
            window.ApplyLayoutSettings();
            window.ApplyTheme();
            Log.Info("Config reloaded");
        }

        // ---------------------------------------------------------------- IPC
        void StartPipeServer()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var r = new StreamReader(server, Encoding.UTF8))
                            {
                                var args = r.ReadToEnd().Split('\n');
                                var o = Options.Parse(args);
                                ui.BeginInvoke(new Action(() =>
                                {
                                    if (o.Quit) Quit();
                                    else if (o.Reindex) Reindex();
                                    else window.Show(o.Query);
                                }));
                            }
                        }
                    }
                    catch (Exception ex) { Log.Error("Pipe server", ex); Thread.Sleep(1000); }
                }
            });
            t.IsBackground = true; t.Name = "Lumen IPC";
            t.Start();
        }

        // ---------------------------------------------------------------- ILumenHost
        public void Reindex()
        {
            engine.Apps.RefreshAsync();
            Balloon("Lumen", "Rebuilding the app index…");
        }

        bool StartupEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Lumen") != null;
            }
        }

        public void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue("Lumen") != null)
                {
                    k.DeleteValue("Lumen", false);
                    Balloon("Lumen", "Won't start with Windows any more.");
                }
                else
                {
                    k.SetValue("Lumen", "\"" + Paths.ExePath + "\" --background");
                    Balloon("Lumen", "Will start automatically when you sign in.");
                }
            }
            if (startupItem != null) startupItem.Checked = StartupEnabled;
        }

        public void Quit()
        {
            try { engine.Usage.Save(); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Current.Shutdown();
        }

        public void SetQuery(string text) { window.SetQuery(text); }

        public void Learn(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) { window.SetQuery("lumen - learn "); return; }
            learner.LearnByName(appName.Trim());
        }

        public void ResetCommands()
        {
            try
            {
                if (File.Exists(Paths.CommandsFile))
                    File.Copy(Paths.CommandsFile, Path.Combine(Paths.ConfigDir, "commands.backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
                File.WriteAllText(Paths.CommandsFile, Settings.Resource("Lumen.commands.json"), new UTF8Encoding(false));
                Balloon("Lumen", "Restored the default commands (your old file was backed up).");
            }
            catch (Exception ex) { Balloon("Couldn't reset commands", ex.Message); }
        }
    }

    /// <summary>Command-line test mode: Lumen.exe --headless --query "excel - new" [--exec --dry-run] --out results.txt</summary>
    internal static class Headless
    {
        sealed class Host : ILumenHost
        {
            public void Reindex() { Shell.DryRunLog.Add("reindex"); }
            public void ToggleStartup() { Shell.DryRunLog.Add("toggle-startup"); }
            public void Quit() { Shell.DryRunLog.Add("quit"); }
            public void SetQuery(string text) { Shell.DryRunLog.Add("set-query \"" + text + "\""); }
            public void ResetCommands() { Shell.DryRunLog.Add("reset-commands"); }
            public void Learn(string appName) { Shell.DryRunLog.Add("learn \"" + appName + "\""); }
        }

        public static int Run(Options o)
        {
            var sb = new StringBuilder();
            try
            {
                Settings.EnsureDefaultFiles();
                Settings.Current = Settings.Load();
                Shell.DryRun = o.DryRun;
                var engine = new SearchEngine { Host = new Host() };
                engine.Commands = CommandRegistry.Load(Paths.CommandsFile);
                if (engine.Commands.LoadError != null) sb.AppendLine("!! " + engine.Commands.LoadError);
                engine.Apps.SetAliases(engine.Commands.AppAliases);
                CommandRegistry.AppsSource = engine.Apps;
                var sw = Stopwatch.StartNew();
                engine.Apps.LoadCache();
                if (o.Reindex || engine.Apps.Apps.Count == 0) engine.Apps.RefreshNow();
                sb.AppendLine("apps: " + engine.Apps.Apps.Count + " (" + sw.ElapsedMilliseconds + " ms)  command sets: " + engine.Commands.Sets.Count);
                engine.Windows = WindowLister.Snapshot(IntPtr.Zero);
                int max = Settings.Current.MaxFileResults;
                foreach (var q in o.Queries)
                {
                    sw.Restart();
                    var r = engine.Search(q);
                    long syncMs = sw.ElapsedMilliseconds;
                    List<FileHit> names = null, contents = null;
                    if (r.WantsFiles)
                    {
                        names = engine.Files.SearchNames(q, max, CancellationToken.None);
                        foreach (var h in names.Take(max + 6)) FileSearcher.Enrich(h);
                        if (Settings.Current.SearchFileContents) contents = engine.Files.SearchContents(q, 6, CancellationToken.None);
                    }
                    var items = engine.Compose(r, names, contents);
                    sb.AppendLine();
                    sb.AppendLine("### \"" + q + "\"  (sync " + syncMs + " ms, total " + sw.ElapsedMilliseconds + " ms)");
                    int fileRows = 0;
                    foreach (var it in items)
                    {
                        if (it.IsHeader) { sb.AppendLine("  [" + it.Title + "]"); continue; }
                        bool isFile = it.Id.StartsWith("file:") && it.Category != Cat.Paths;
                        if (isFile && !o.Files) { fileRows++; sb.AppendLine("    <file/folder result>"); continue; }
                        sb.AppendLine("    " + it.Title + "  |  " + it.Subtitle + (it.Hint != null ? "  |  " + it.Hint : "") + "  |  " + it.Score.ToString("0.000"));
                    }
                    if (o.Exec)
                    {
                        var first = items.FirstOrDefault(i => !i.IsHeader);
                        if (first != null && first.Execute != null)
                        {
                            Shell.DryRunLog.Clear();
                            first.Execute(ExecMode.Primary);
                            // Wait for background work (e.g. Office automation) before reporting.
                            List<Thread> pending;
                            lock (Shell.StaThreads) pending = Shell.StaThreads.ToList();
                            foreach (var t in pending) t.Join(TimeSpan.FromSeconds(60));
                            sb.AppendLine("  => " + (Shell.DryRunLog.Count == 0 ? "(no external action)" : string.Join("  ;  ", Shell.DryRunLog)));
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("!! " + ex); }
            if (!string.IsNullOrEmpty(o.Out)) File.WriteAllText(o.Out, sb.ToString(), Encoding.UTF8);
            return 0;
        }
    }
}
