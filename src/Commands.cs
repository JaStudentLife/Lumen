using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Lumen
{
    internal sealed class AppCommand
    {
        public string Name, Description, ArgLabel, ArgDefault, Suggest, Glyph;
        public bool ArgRequired, Confirm;
        public List<string> Aliases = new List<string>();
        public Dictionary<string, object> Action;
        public CommandSet Set;
        public List<MatchTarget> NameTargets = new List<MatchTarget>();
        public MatchTarget DescriptionTarget;

        public IEnumerable<string> AllNames { get { yield return Name; foreach (var a in Aliases) yield return a; } }
        public string Id { get { return "cmd:" + Set.Name + ":" + Name; } }
        public string Usage { get { return Set.Canonical + " - " + Name + (ArgLabel == null ? "" : " <" + ArgLabel + ">"); } }
    }

    internal sealed class CommandSet
    {
        public string Name, Icon, Glyph;
        public bool Generated;
        public List<string> Match = new List<string>();
        public List<AppCommand> Commands = new List<AppCommand>();
        public List<MatchTarget> MatchTargets = new List<MatchTarget>();
        public string ResolvedIcon; public int ResolvedVersion = -1;
        public string Canonical { get { return Match.Count > 0 ? Match[0] : Name.ToLowerInvariant(); } }
    }

    internal sealed class WebShortcut { public string Keyword, Name, Url; }

    internal sealed class CommandQuery { public CommandSet Set; public double SetScore; public string AppText, Rest; }

    internal sealed class CommandMatch { public AppCommand Command; public string Arg; public double Score; public bool Exact; }

    internal sealed class Suggestion { public string Title, Subtitle, Value, IconPath, Hint; }

    /// <summary>
    /// App commands from commands.json: "excel - new", "code - code myproject", "chrome - incognito"...
    /// Handles parsing "&lt;app&gt; - &lt;command&gt; [argument]" and argument suggestions.
    /// </summary>
    internal sealed class CommandRegistry
    {
        public List<CommandSet> Sets = new List<CommandSet>();
        public List<WebShortcut> Web = new List<WebShortcut>();
        public Dictionary<string, List<string>> AppAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public string LoadError;

        public static CommandRegistry Load(string path)
        {
            var reg = new CommandRegistry();
            string text;
            try { text = File.Exists(path) ? File.ReadAllText(path) : Settings.Resource("Lumen.commands.json"); }
            catch (Exception ex) { reg.LoadError = ex.Message; return reg; }
            object root;
            try { root = Json.Parse(text); }
            catch (Exception ex)
            {
                reg.LoadError = "commands.json: " + ex.Message;
                Log.Error("Parsing commands.json", ex);
                try { root = Json.Parse(Settings.Resource("Lumen.commands.json")); } catch { return reg; }
            }

            var appList = new List<object>(Json.Arr(root, "apps") ?? new List<object>());
            // Commands learned from the web for newly installed apps (your own commands.json wins on name clashes).
            try
            {
                if (File.Exists(AppLearner.GeneratedFile))
                {
                    var gen = Json.Arr(Json.Parse(File.ReadAllText(AppLearner.GeneratedFile)), "apps");
                    if (gen != null)
                        foreach (var g in gen)
                            if (!appList.Any(o => Json.Str(o, "name", "").Equals(Json.Str(g, "name", ""), StringComparison.OrdinalIgnoreCase)))
                                appList.Add(g);
                }
            }
            catch (Exception ex) { Log.Error("Reading commands.generated.json", ex); }

            foreach (var o in appList)
            {
                var set = new CommandSet
                {
                    Name = Json.Str(o, "name", "App"),
                    Icon = Json.Str(o, "icon", null),
                    Glyph = Json.Str(o, "glyph", null),
                    Generated = Json.Bool(o, "generated", false),
                    Match = Json.Strings(o, "match").Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).ToList()
                };
                if (set.Match.Count == 0) set.Match.Add(set.Name.ToLowerInvariant());
                set.MatchTargets = set.Match.Select(m => new MatchTarget(m)).ToList();
                set.MatchTargets.Add(new MatchTarget(set.Name));
                foreach (var c in Json.Arr(o, "commands") ?? new List<object>())
                {
                    var cmd = new AppCommand
                    {
                        Set = set,
                        Name = Json.Str(c, "name", "run").Trim(),
                        Description = Json.Str(c, "description", ""),
                        ArgLabel = Json.Str(c, "arg", null),
                        ArgDefault = Json.Str(c, "argDefault", null),
                        ArgRequired = Json.Bool(c, "argRequired", false),
                        Suggest = Json.Str(c, "suggest", null),
                        Confirm = Json.Bool(c, "confirm", false),
                        Glyph = Json.Str(c, "glyph", null),
                        Aliases = Json.Strings(c, "aliases"),
                        Action = Json.Obj(c, "action") ?? new Dictionary<string, object>()
                    };
                    foreach (var n in cmd.AllNames) cmd.NameTargets.Add(new MatchTarget(n));
                    cmd.DescriptionTarget = new MatchTarget(cmd.Description);
                    set.Commands.Add(cmd);
                }
                if (set.Name.Equals("Lumen", StringComparison.OrdinalIgnoreCase)) AddLearnCommands(set);
                reg.Sets.Add(set);
            }
            foreach (var o in Json.Arr(root, "web") ?? new List<object>())
            {
                var w = new WebShortcut { Keyword = Json.Str(o, "keyword", "").Trim(), Name = Json.Str(o, "name", "Web"), Url = Json.Str(o, "url", "") };
                if (w.Keyword.Length > 0 && w.Url.Length > 0) reg.Web.Add(w);
            }
            var al = Json.Obj(root, "appAliases");
            if (al != null) foreach (var kv in al) reg.AppAliases[kv.Key] = Json.Strings(al, kv.Key);
            return reg;
        }

        /// <summary>"lumen - learn &lt;app&gt;" and "lumen - learned" exist even in commands.json files written before they did.</summary>
        static void AddLearnCommands(CommandSet set)
        {
            Action<string, string, string, bool, string, string[]> add = (name, desc, arg, req, builtin, aliases) =>
            {
                if (set.Commands.Any(c => c.Name == name)) return;
                var cmd = new AppCommand
                {
                    Set = set, Name = name, Description = desc, ArgLabel = arg, ArgRequired = req, Aliases = aliases.ToList(),
                    Suggest = arg != null ? "apps" : null,
                    Action = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "type", "builtin" }, { "name", builtin } }
                };
                foreach (var n in cmd.AllNames) cmd.NameTargets.Add(new MatchTarget(n));
                cmd.DescriptionTarget = new MatchTarget(desc);
                set.Commands.Insert(Math.Min(2, set.Commands.Count), cmd);
            };
            add("learned", "Edit the commands Lumen learned automatically", null, false, "lumen-learned", new[] { "generated" });
            add("learn", "Research an app on the web and build commands for it", "app", true, "lumen-learn", new[] { "teach", "research" });
        }

        /// <summary>Detects "excel - new", "excel -new", "exc - n", "excel-new" (exact app word only when there are no spaces).</summary>
        public CommandQuery Parse(string raw)
        {
            string q = (raw ?? "").TrimStart();
            for (int i = 1; i < q.Length; i++)
            {
                if (q[i] != '-') continue;
                string left = q.Substring(0, i).Trim();
                if (left.Length == 0) continue;
                bool loose = q[i - 1] == ' ' || (i + 1 < q.Length && q[i + 1] == ' ');
                string rest = i + 1 < q.Length ? q.Substring(i + 1).Trim() : "";
                double score;
                var set = BestSet(left, !loose || left.Length < 2, out score);
                if (set != null) return new CommandQuery { Set = set, SetScore = score, AppText = left, Rest = rest };
            }
            return null;
        }

        public CommandSet BestSet(string text, bool exactOnly, out double bestScore)
        {
            string l = Matcher.NormalizeQuery(text);
            var words = Matcher.Words(l);
            CommandSet best = null; bestScore = 0;
            foreach (var set in Sets)
            {
                double s = 0;
                foreach (var t in set.MatchTargets)
                    s = Math.Max(s, exactOnly ? (t.Lower == l ? 1 : 0) : Matcher.Score(l, words, t));
                if (s > bestScore) { bestScore = s; best = set; }
            }
            return bestScore >= (exactOnly ? 1 : 0.76) ? best : null;
        }

        public List<CommandMatch> MatchCommands(CommandSet set, string rest)
        {
            var result = new List<CommandMatch>();
            string r = (rest ?? "").Trim();
            if (r.Length == 0)
            {
                for (int i = 0; i < set.Commands.Count; i++)
                    result.Add(new CommandMatch { Command = set.Commands[i], Arg = "", Score = 1.0 - i * 0.001 });
                return result;
            }
            string rl = r.ToLowerInvariant();
            AppCommand exact = null; string exactArg = null; int exactLen = -1;
            foreach (var c in set.Commands)
                foreach (var n in c.AllNames)
                {
                    string nl = n.ToLowerInvariant();
                    if ((rl == nl || rl.StartsWith(nl + " ", StringComparison.Ordinal)) && nl.Length > exactLen)
                    { exact = c; exactLen = nl.Length; exactArg = r.Substring(nl.Length).Trim(); }
                }
            if (exact != null) result.Add(new CommandMatch { Command = exact, Arg = exactArg, Score = 1.0, Exact = true });

            string norm = Matcher.NormalizeQuery(r);
            var words = Matcher.Words(norm);
            string first = words.Length > 0 ? words[0] : norm;
            string remainder = r.IndexOf(' ') > 0 ? r.Substring(r.IndexOf(' ') + 1).Trim() : "";
            foreach (var c in set.Commands)
            {
                if (c == exact) continue;
                double whole = 0, head = 0;
                foreach (var t in c.NameTargets)
                {
                    whole = Math.Max(whole, Matcher.Score(norm, words, t));
                    head = Math.Max(head, Matcher.Score(first, new[] { first }, t));
                }
                // Once a command matched exactly, only show others whose names clearly match too.
                double desc = exact != null ? 0 : Matcher.Score(norm, words, c.DescriptionTarget) * 0.75;
                if (exact != null && Math.Max(whole, head) < 0.8) continue;
                if (whole >= head && whole >= desc && whole >= 0.4) result.Add(new CommandMatch { Command = c, Arg = "", Score = whole * 0.95 });
                else if (head >= desc && head >= 0.4) result.Add(new CommandMatch { Command = c, Arg = remainder, Score = head * 0.9 });
                else if (desc >= 0.45) result.Add(new CommandMatch { Command = c, Arg = "", Score = desc });
            }
            return result.OrderByDescending(m => m.Score).ToList();
        }

        public CommandSet SetForApp(AppEntry a)
        {
            if (a == null) return null;
            foreach (var s in Sets)
            {
                if (s.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) return s;
                if (s.Icon != null && a.ExeName != null && s.Icon.Equals(a.ExeName, StringComparison.OrdinalIgnoreCase)) return s;
                foreach (var m in s.Match) if (m.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        public string IconFor(CommandSet set, AppIndex apps, int appsVersion)
        {
            if (set.ResolvedVersion == appsVersion) return set.ResolvedIcon;
            string icon = null;
            if (!string.IsNullOrEmpty(set.Icon))
            {
                var app = apps.FindByExe(set.Icon) ?? apps.FindByName(set.Icon);
                icon = app != null ? app.IconPath : Shell.ResolveExe(set.Icon);
            }
            if (icon == null)
            {
                var byName = apps.FindByName(set.Name);
                if (byName != null) icon = byName.IconPath;
            }
            set.ResolvedIcon = icon; set.ResolvedVersion = appsVersion;
            return icon;
        }

        // ------------------------------------------------------------------ argument suggestions
        public static AppIndex AppsSource;

        public static List<Suggestion> Suggestions(string source)
        {
            var list = new List<Suggestion>();
            if (string.IsNullOrEmpty(source)) return list;
            int colon = source.IndexOf(':');
            string kind = (colon < 0 ? source : source.Substring(0, colon)).Trim().ToLowerInvariant();
            string param = colon < 0 ? "" : source.Substring(colon + 1).Trim();
            try
            {
                switch (kind)
                {
                    case "office-recent": return OfficeRecent(param);
                    case "folders": return Folders(Placeholders.Expand(param, ""));
                    case "files": return Files(Placeholders.Expand(param, ""));
                    case "vscode-recent": return VsCodeRecent();
                    case "apps":
                        return (AppsSource == null ? new List<AppEntry>() : AppsSource.Apps).OrderBy(a => a.Name)
                            .Select(a => new Suggestion { Title = a.Name, Subtitle = a.ExePath != null ? Paths.Pretty(a.ExePath) : "Store app", Value = a.Name, IconPath = a.IconPath })
                            .ToList();
                    case "bookmarks":
                        return Bookmarks.All()
                            .Where(b => param.Length == 0 || param == "all" || b.Browser.Equals(param, StringComparison.OrdinalIgnoreCase))
                            .Select(b => new Suggestion { Title = b.Name, Subtitle = b.Url, Value = b.Url }).ToList();
                }
            }
            catch (Exception ex) { Log.Error("Suggestions " + source, ex); }
            return list;
        }

        static List<Suggestion> OfficeRecent(string app)
        {
            var found = new List<Tuple<DateTime, string>>();
            string baseKey = @"Software\Microsoft\Office\16.0\" + (string.IsNullOrEmpty(app) ? "Excel" : app);
            var keys = new List<string> { baseKey + @"\File MRU" };
            using (var um = Registry.CurrentUser.OpenSubKey(baseKey + @"\User MRU"))
                if (um != null) foreach (var sub in um.GetSubKeyNames()) keys.Add(baseKey + @"\User MRU\" + sub + @"\File MRU");
            foreach (var k in keys)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(k))
                {
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames().Where(n => n.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)))
                    {
                        string v = key.GetValue(name) as string;
                        if (string.IsNullOrEmpty(v)) continue;
                        int star = v.IndexOf('*');
                        if (star < 0) continue;
                        string path = v.Substring(star + 1);
                        DateTime when = DateTime.MinValue;
                        int t = v.IndexOf("[T", StringComparison.Ordinal);
                        long ft;
                        if (t >= 0 && v.Length > t + 18 && long.TryParse(v.Substring(t + 2, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ft))
                            try { when = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { }
                        bool isUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                        if (!isUrl && !File.Exists(path)) continue;
                        found.Add(Tuple.Create(when, path));
                    }
                }
            }
            return found.GroupBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.Item1).First())
                .OrderByDescending(f => f.Item1).Take(40)
                .Select(f => new Suggestion
                {
                    Title = Uri.UnescapeDataString(Path.GetFileName(f.Item2.TrimEnd('/'))),
                    Subtitle = f.Item2.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? f.Item2 : Paths.Pretty(Path.GetDirectoryName(f.Item2)),
                    Value = f.Item2,
                    IconPath = f.Item2.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : f.Item2,
                    Hint = f.Item1 == DateTime.MinValue ? null : Format.When(f.Item1)
                }).ToList();
        }

        static List<Suggestion> Folders(string dir)
        {
            if (!Directory.Exists(dir)) return new List<Suggestion>();
            return new DirectoryInfo(dir).GetDirectories()
                .Where(d => (d.Attributes & FileAttributes.Hidden) == 0 && !d.Name.StartsWith("."))
                .OrderByDescending(d => d.LastWriteTime).Take(60)
                .Select(d => new Suggestion { Title = d.Name, Subtitle = Paths.Pretty(d.FullName), Value = d.FullName, IconPath = d.FullName, Hint = Format.When(d.LastWriteTime) })
                .ToList();
        }

        static List<Suggestion> Files(string dir)
        {
            if (!Directory.Exists(dir)) return new List<Suggestion>();
            return new DirectoryInfo(dir).GetFiles()
                .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                .OrderByDescending(f => f.LastWriteTime).Take(60)
                .Select(f => new Suggestion { Title = f.Name, Subtitle = Paths.Pretty(f.DirectoryName), Value = f.FullName, IconPath = f.FullName, Hint = Format.When(f.LastWriteTime) })
                .ToList();
        }

        static List<Suggestion> VsCodeRecent()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Code\User\globalStorage\storage.json");
            try
            {
                if (File.Exists(storage))
                {
                    object j = Json.Parse(File.ReadAllText(storage));
                    var ws = Json.Obj(Json.Obj(j, "profileAssociations"), "workspaces");
                    if (ws != null) foreach (var key in ws.Keys) AddUri(paths, key);
                    var win = Json.Obj(j, "windowsState");
                    var last = Json.Obj(win, "lastActiveWindow");
                    AddUri(paths, Json.Str(last, "folder", null));
                    foreach (var o in Json.Arr(win, "openedWindows") ?? new List<object>()) AddUri(paths, Json.Str(o, "folder", null));
                }
            }
            catch (Exception ex) { Log.Error("Reading VS Code storage.json", ex); }
            string projects = Settings.Current.ResolvedProjectsFolder;
            if (Directory.Exists(projects))
                foreach (var d in Directory.GetDirectories(projects)) if (!Path.GetFileName(d).StartsWith(".")) paths.Add(d);
            return paths.Where(Directory.Exists).Select(p => new DirectoryInfo(p)).OrderByDescending(d => d.LastWriteTime).Take(60)
                .Select(d => new Suggestion { Title = d.Name, Subtitle = Paths.Pretty(d.FullName), Value = d.FullName, IconPath = d.FullName, Hint = Format.When(d.LastWriteTime) })
                .ToList();
        }

        static void AddUri(HashSet<string> set, string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return;
            Uri u;
            if (Uri.TryCreate(uri, UriKind.Absolute, out u) && u.IsFile) set.Add(u.LocalPath.TrimEnd('\\'));
        }
    }

    internal static class Format
    {
        public static string When(DateTime t)
        {
            if (t == DateTime.MinValue || t.Year < 1990) return null;
            var now = DateTime.Now;
            if (t.Date == now.Date) return t.ToString("h:mm tt", CultureInfo.CurrentCulture);
            if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
            if ((now - t).TotalDays < 7) return t.ToString("dddd", CultureInfo.CurrentCulture);
            if (t.Year == now.Year) return t.ToString("MMM d", CultureInfo.CurrentCulture);
            return t.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        public static string Size(long bytes)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? bytes + " bytes" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + u[i];
        }
    }
}
