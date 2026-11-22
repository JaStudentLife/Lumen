using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Lumen
{
    internal sealed class AppEntry
    {
        public string Name;
        public string AppId;    // shell:AppsFolder parsing name (AUMID for Store apps)
        public string ExePath;  // target exe for desktop apps, if known
        public MatchTarget NameTarget;
        public List<MatchTarget> AliasTargets = new List<MatchTarget>();

        public string Id { get { return "app:" + AppId; } }
        public string IconPath { get { return "shell:AppsFolder\\" + AppId; } }
        public string ExeName { get { return string.IsNullOrEmpty(ExePath) ? null : Path.GetFileName(ExePath); } }
    }

    /// <summary>
    /// Every app in Start's "All apps" list (desktop and Store apps), read from shell:AppsFolder.
    /// Cached to disk so search is instant at startup, refreshed in the background.
    /// </summary>
    internal sealed class AppIndex
    {
        static readonly string[] DocExtensions = { ".chm", ".pdf", ".txt", ".htm", ".html", ".url", ".rtf", ".md", ".hlp", ".ico" };

        volatile List<AppEntry> apps = new List<AppEntry>();
        Dictionary<string, List<string>> aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        DateTime lastRefresh = DateTime.MinValue;
        int refreshing;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        Timer debounce;

        public event Action Changed;
        public List<AppEntry> Apps { get { return apps; } }

        public void SetAliases(Dictionary<string, List<string>> map)
        {
            aliases = map ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var current = apps;
            foreach (var a in current) BuildTargets(a);
        }

        public void LoadCache()
        {
            try
            {
                if (!File.Exists(Paths.AppCacheFile)) return;
                var list = Json.Parse(File.ReadAllText(Paths.AppCacheFile)) as List<object>;
                if (list == null) return;
                var result = new List<AppEntry>();
                foreach (var o in list)
                {
                    var a = new AppEntry { Name = Json.Str(o, "name", null), AppId = Json.Str(o, "id", null), ExePath = Json.Str(o, "exe", null) };
                    if (a.Name == null || a.AppId == null) continue;
                    BuildTargets(a); result.Add(a);
                }
                apps = result;
            }
            catch (Exception ex) { Log.Error("Reading app cache", ex); }
        }

        /// <summary>Synchronous re-index (STA thread only); used by the command-line test mode.</summary>
        public void RefreshNow()
        {
            var fresh = Enumerate();
            if (fresh.Count == 0) return;
            apps = fresh; lastRefresh = DateTime.UtcNow; SaveCache(fresh);
        }

        public void RefreshIfStale(TimeSpan maxAge)
        {
            if (DateTime.UtcNow - lastRefresh > maxAge) RefreshAsync();
        }

        public void RefreshAsync()
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    var fresh = Enumerate();
                    if (fresh.Count > 0)
                    {
                        apps = fresh;
                        lastRefresh = DateTime.UtcNow;
                        SaveCache(fresh);
                        var h = Changed; if (h != null) h();
                    }
                }
                catch (Exception ex) { Log.Error("Indexing apps", ex); }
                finally { Interlocked.Exchange(ref refreshing, 0); }
            });
            t.IsBackground = true; t.Name = "Lumen app index";
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// <summary>Must run on an STA thread.</summary>
        public List<AppEntry> Enumerate()
        {
            var sw = Stopwatch.StartNew();
            var result = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string name = item.Name;
                    string id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    string exe = null;
                    try { exe = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                    if (IsJunk(name, id, exe)) continue;
                    if (!seen.Add(name + "|" + (exe ?? id))) continue;
                    var a = new AppEntry { Name = name, AppId = id, ExePath = string.IsNullOrEmpty(exe) || exe.StartsWith("::") ? null : exe };
                    BuildTargets(a);
                    result.Add(a);
                }
                catch { }
            }
            Log.Info("Indexed " + result.Count + " apps in " + sw.ElapsedMilliseconds + " ms");
            return result;
        }

        static bool IsJunk(string name, string id, string exe)
        {
            string n = name.ToLowerInvariant();
            if (n.StartsWith("uninstall") || n.Contains(" uninstall") || n.EndsWith(" uninstaller")) return true;
            if (id.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || id.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return true;
            string target = exe ?? id;
            string ext = Path.GetExtension(target.Trim('"')).ToLowerInvariant();
            return DocExtensions.Contains(ext);
        }

        void BuildTargets(AppEntry a)
        {
            a.NameTarget = new MatchTarget(a.Name);
            var list = new List<MatchTarget>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Name };
            string exeName = a.ExeName;
            if (exeName != null)
            {
                string stem = Path.GetFileNameWithoutExtension(exeName);
                if (names.Add(stem)) list.Add(new MatchTarget(stem));
            }
            // "Outlook (classic)" should also match "outlook classic"
            string plain = a.Name.Replace("(", " ").Replace(")", " ").Trim();
            if (names.Add(plain)) list.Add(new MatchTarget(plain));
            List<string> extra;
            if (aliases.TryGetValue(a.Name, out extra))
                foreach (var al in extra) if (names.Add(al)) list.Add(new MatchTarget(al));
            a.AliasTargets = list;
        }

        void SaveCache(List<AppEntry> list)
        {
            try
            {
                var arr = list.Select(a => (object)new Dictionary<string, object> { { "name", a.Name }, { "id", a.AppId }, { "exe", a.ExePath } }).ToList();
                File.WriteAllText(Paths.AppCacheFile, Json.Serialize(arr, false));
            }
            catch (Exception ex) { Log.Error("Writing app cache", ex); }
        }

        public void WatchStartMenu()
        {
            string[] dirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            };
            debounce = new Timer(_ => RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    var w = new FileSystemWatcher(d) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                    FileSystemEventHandler h = (s, e) => debounce.Change(4000, Timeout.Infinite);
                    w.Created += h; w.Deleted += h; w.Renamed += (s, e) => debounce.Change(4000, Timeout.Infinite);
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception ex) { Log.Error("Watching " + d, ex); }
            }
        }

        public AppEntry FindByExe(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return null;
            string want = Path.GetFileName(exeName);
            if (!want.Contains(".")) want += ".exe";
            return apps.FirstOrDefault(a => a.ExeName != null && a.ExeName.Equals(want, StringComparison.OrdinalIgnoreCase));
        }

        public AppEntry FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? apps.FirstOrDefault(a => a.AppId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static void Launch(AppEntry a, ExecMode mode)
        {
            bool hasExe = !string.IsNullOrEmpty(a.ExePath) && File.Exists(a.ExePath);
            if (mode == ExecMode.Alternate && hasExe) { Shell.ShowInFolder(a.ExePath); return; }
            if (mode == ExecMode.Admin && hasExe)
            {
                Shell.Start(new ProcessStartInfo(a.ExePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(a.ExePath) });
                return;
            }
            Shell.Start(new ProcessStartInfo("explorer.exe", Placeholders.QuoteArg("shell:AppsFolder\\" + a.AppId)) { UseShellExecute = true });
        }
    }
}
