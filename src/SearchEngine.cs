using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lumen
{
    internal sealed class SearchResult
    {
        public string Query = "";
        public List<ResultItem> Items = new List<ResultItem>();
        public bool WantsFiles;
        public bool CommandMode;
        public CommandSet ModeSet;
        public Dictionary<string, double> Boosts = new Dictionary<string, double>();
    }

    /// <summary>Runs every provider for a query and assembles the Spotlight-style grouped list.</summary>
    internal sealed partial class SearchEngine
    {
        public readonly AppIndex Apps = new AppIndex();
        public readonly FileSearcher Files = new FileSearcher();
        public readonly Usage Usage = new Usage();
        public CommandRegistry Commands = new CommandRegistry();
        public ILumenHost Host;
        public List<OpenWindow> Windows = new List<OpenWindow>();
        public int AppsVersion;

        static readonly Regex UrlRx = new Regex(@"^(https?://\S+|(localhost|[\w-]+(\.[\w-]+)*\.[a-z]{2,24})(:\d{1,5})?(/\S*)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly string[] Order =
        {
            Cat.TopHit, Cat.Help, Cat.Commands, Cat.Suggestions, Cat.Calculator, Cat.Paths, Cat.Apps, Cat.Windows,
            Cat.Settings, Cat.System, Cat.Recent, Cat.Bookmarks, Cat.Folders, Cat.Files, Cat.Web
        };

        static int Cap(string cat, bool commandMode)
        {
            switch (cat)
            {
                case Cat.Apps: return 6;
                case Cat.Commands: return commandMode ? 40 : 4;
                case Cat.Suggestions: return 30;
                case Cat.Windows: return 4;
                case Cat.Settings: return 5;
                case Cat.System: return 4;
                case Cat.Bookmarks: return 4;
                case Cat.Folders: return 4;
                case Cat.Files: return Math.Max(1, Settings.Current.MaxFileResults);
                case Cat.Paths: return 20;
                case Cat.Web: return 2;
                default: return 30;
            }
        }

        public SearchResult Search(string raw)
        {
            var r = new SearchResult { Query = raw ?? "" };
            string q = r.Query.Trim();
            if (q.Length == 0)
            {
                if (Settings.Current.ShowRecentWhenEmpty) r.Items.AddRange(RecentItems());
                return r;
            }
            string norm = Matcher.NormalizeQuery(q);
            var words = Matcher.Words(norm);
            r.Boosts = Usage.BoostsFor(q);
            var items = r.Items;

            if (q == "?" || q.Equals("help", StringComparison.OrdinalIgnoreCase)) { items.AddRange(HelpItems()); if (q == "?") return r; }

            // > command  -> run in a terminal
            if (q.StartsWith(">"))
            {
                string cmd = q.Substring(1).Trim();
                if (cmd.Length > 0)
                {
                    var it = Simple("shell", "Run  " + cmd, "In a new " + (Settings.Current.TerminalShell == "cmd" ? "Command Prompt" : "PowerShell") + " window  ·  Ctrl+Shift+Enter as admin",
                        Cat.TopHit, Glyphs.Terminal, Glyphs.Gray, 2, m => RunInTerminal(cmd, m == ExecMode.Admin));
                    it.SupportsAdmin = true; it.ActionName = "Run"; it.Kind = "Terminal command";
                    items.Add(it);
                }
                return r;
            }

            // app - command [argument]
            var cq = Commands.Parse(q);
            if (cq != null)
            {
                var matches = Commands.MatchCommands(cq.Set, cq.Rest);
                if (matches.Count > 0)
                {
                    r.CommandMode = true; r.ModeSet = cq.Set;
                    foreach (var m in matches)
                    {
                        var it = CommandItem(m.Command, m.Arg, 3 + m.Score);
                        it.Group = cq.Set.Name + " Commands";
                        items.Add(it);
                    }
                    var exact = matches.FirstOrDefault(m => m.Exact);
                    if (exact != null && !string.IsNullOrEmpty(exact.Command.Suggest)) items.AddRange(SuggestionItems(exact.Command, exact.Arg));
                    return r;
                }
            }

            // web shortcuts: "g cats", "yt lofi"
            int sp = q.IndexOf(' ');
            if (sp > 0)
            {
                string kw = q.Substring(0, sp);
                string rest = q.Substring(sp + 1).Trim();
                var w = Commands.Web.FirstOrDefault(x => x.Keyword.Equals(kw, StringComparison.OrdinalIgnoreCase));
                if (w != null && rest.Length > 0)
                {
                    var it = WebItem(w.Name, Placeholders.Expand(w.Url, rest), rest, 2.5, "web:" + w.Keyword);
                    it.Category = Cat.TopHit;
                    items.Add(it);
                }
            }

            // calculator and unit conversion
            double value; string disp, plain, expr;
            if (Calculator.TryConvert(q, out disp, out plain, out expr))
            {
                var it = Simple("calc", disp, expr + "  ·  Enter to copy", Cat.Calculator, Glyphs.Calc, Glyphs.Orange, 2.2, m => Shell.CopyText(plain));
                it.CopyText = plain; it.ActionName = "Copy"; it.Kind = "Conversion"; it.Note = expr + " " + disp;
                items.Add(it);
            }
            else if (Calculator.TryEvaluate(q, out value))
            {
                string p = Calculator.Plain(value);
                var it = Simple("calc", Calculator.Format(value), q.TrimStart('=').Trim() + " =  ·  Enter to copy", Cat.Calculator, Glyphs.Calc, Glyphs.Orange, 2.2, m => Shell.CopyText(p));
                it.CopyText = p; it.ActionName = "Copy"; it.Kind = "Calculation"; it.Note = q.TrimStart('=').Trim() + " = " + Calculator.Format(value);
                if (value == Math.Floor(value) && Math.Abs(value) < 9e15 && value >= 0) { it.AddDetail("Hex", "0x" + ((long)value).ToString("X")); it.AddDetail("Binary", Convert.ToString((long)value, 2)); }
                items.Add(it);
            }

            // URLs and paths
            if (UrlRx.IsMatch(q) && !q.Contains(" ") && (q.Contains("/") || q.Contains(".") || q.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) && !Regex.IsMatch(q, @"^[\d.]+$"))
            {
                string url = q.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? q : "https://" + q;
                if (Path.GetExtension(q).Length < 2 || Regex.IsMatch(q, @"\.(com|org|net|io|dev|app|ai|co|edu|gov|me|tv|us|uk|ca|de)(/|$)", RegexOptions.IgnoreCase) || q.Contains("/"))
                {
                    var it = Simple("url", "Open " + q, "Website", Cat.TopHit, Glyphs.Link, Glyphs.Blue, 1.6, m => Shell.Open(url));
                    it.CopyText = url; it.Kind = "Website";
                    items.Add(it);
                }
            }
            items.AddRange(PathItems(q));

            // applications
            var apps = Apps.Apps;
            var appHits = new List<Tuple<AppEntry, double>>();
            foreach (var a in apps)
            {
                double s = Matcher.Best(norm, words, a.NameTarget, a.AliasTargets, 0.95);
                if (s >= 0.4) appHits.Add(Tuple.Create(a, s + Boost(r, a.Id)));
            }
            foreach (var h in appHits.OrderByDescending(x => x.Item2).Take(8)) items.Add(AppItem(h.Item1, h.Item2));

            // commands in normal search: "excel new", "new excel", or an app name that has commands
            items.AddRange(LooseCommandItems(q, words, r));

            // settings, admin tools, system actions
            foreach (var e in SystemCatalog.Entries)
            {
                double s = Matcher.Best(norm, words, e.NameTarget, e.Keywords, 0.8) * (e.Category == Cat.System ? 0.92 : 0.9);
                if (s >= 0.55) items.Add(CatalogItem(e, s + Boost(r, e.Id)));
            }
            string runnable = SystemCatalog.FindRunnable(q);
            if (runnable != null && !items.Any(i => i.Category == Cat.Apps && i.Score >= 0.9))
            {
                var it = Simple("run:" + runnable, "Run " + Path.GetFileName(runnable), Paths.Pretty(runnable), Cat.System, Glyphs.Terminal, Glyphs.Gray, 0.86,
                    m => Shell.Run(runnable, new List<string>(), null, m == ExecMode.Admin, true));
                it.IconPath = runnable; it.SupportsAdmin = true; it.ActionName = "Run";
                items.Add(it);
            }

            // open windows
            foreach (var w in Windows)
            {
                double s = Math.Max(Matcher.Score(norm, words, w.TitleTarget) * 0.82, Matcher.Score(norm, words, w.ProcessTarget) * 0.84);
                if (s >= 0.5) items.Add(WindowItem(w, s));
            }

            // bookmarks
            var bms = new List<Tuple<Bookmark, double>>();
            foreach (var b in Bookmarks.All())
            {
                double s = Math.Max(Matcher.Score(norm, words, b.NameTarget) * 0.8, Matcher.Score(norm, words, b.UrlTarget) * 0.76);
                if (s >= 0.55) bms.Add(Tuple.Create(b, s + Boost(r, "bm:" + b.Url)));
            }
            foreach (var b in bms.OrderByDescending(x => x.Item2).Take(4)) items.Add(BookmarkItem(b.Item1, b.Item2));

            // web fallback (always last)
            if (!items.Any(i => i.Category == Cat.Web || i.Id.StartsWith("web:")))
                items.Add(WebItem(Settings.Current.WebSearchName, Placeholders.Expand(Settings.Current.WebSearchUrl, q), q, 0, "web:default"));

            r.WantsFiles = Settings.Current.SearchFiles && q.Length >= 2 && !q.StartsWith("=");
            return r;
        }

        double Boost(SearchResult r, string id)
        {
            double b;
            return r.Boosts.TryGetValue(id, out b) ? b : 0;
        }

        IEnumerable<ResultItem> LooseCommandItems(string q, string[] words, SearchResult r)
        {
            var seen = new HashSet<string>();
            var list = new List<ResultItem>();
            // A query that is just an app name with commands: show its first few commands.
            double setScore;
            var only = Commands.BestSet(q, false, out setScore);
            if (only != null && setScore >= 0.9 && q.Length >= 2)
                foreach (var c in only.Commands.Take(3))
                    if (seen.Add(c.Id)) list.Add(CommandItem(c, "", 0.5 - list.Count * 0.01 + Boost(r, c.Id)));
            // "excel new budget" / "new excel"
            if (words.Length >= 2)
            {
                for (int split = 1; split < words.Length; split++)
                {
                    string left = string.Join(" ", words.Take(split));
                    string right = string.Join(" ", words.Skip(split));
                    double s1, s2;
                    var setA = Commands.BestSet(left, false, out s1);
                    if (setA != null)
                        foreach (var m in Commands.MatchCommands(setA, right).Where(m => m.Score >= 0.85).Take(3))
                            if (seen.Add(m.Command.Id)) list.Add(CommandItem(m.Command, m.Arg, 0.8 * m.Score * s1 + Boost(r, m.Command.Id)));
                    var setB = Commands.BestSet(right, false, out s2);
                    if (setB != null && split == 1)
                        foreach (var m in Commands.MatchCommands(setB, left).Where(m => m.Score >= 0.9 && m.Arg.Length == 0).Take(2))
                            if (seen.Add(m.Command.Id)) list.Add(CommandItem(m.Command, "", 0.75 * m.Score * s2 + Boost(r, m.Command.Id)));
                }
            }
            return list;
        }

        IEnumerable<ResultItem> SuggestionItems(AppCommand c, string arg)
        {
            var all = CommandRegistry.Suggestions(c.Suggest);
            string norm = Matcher.NormalizeQuery(arg ?? "");
            var words = Matcher.Words(norm);
            var list = new List<ResultItem>();
            int i = 0;
            foreach (var s in all)
            {
                double score;
                if (norm.Length == 0) score = 1 - i * 0.001;
                else
                {
                    score = Math.Max(Matcher.Score(norm, words, new MatchTarget(s.Title)), Matcher.Score(norm, words, new MatchTarget(s.Subtitle ?? "")) * 0.8);
                    if (score < 0.4) { i++; continue; }
                }
                list.Add(SuggestionItem(c, s, 2 + score));
                i++;
            }
            return list.OrderByDescending(x => x.Score).Take(30);
        }

        IEnumerable<ResultItem> PathItems(string q)
        {
            var list = new List<ResultItem>();
            string p = q.Trim('"');
            if (p.StartsWith("~")) p = Paths.Home + p.Substring(1);
            p = Environment.ExpandEnvironmentVariables(p);
            bool looksLikePath = Regex.IsMatch(p, @"^([a-zA-Z]:[\\/]|\\\\[^\\]+\\)") || (p.StartsWith(Paths.Home) && q.StartsWith("~"));
            if (!looksLikePath) return list;
            try
            {
                p = p.Replace('/', '\\');
                if (File.Exists(p) || (Directory.Exists(p) && !p.EndsWith("\\")))
                {
                    var it = FileItem(new FileHit { Path = p, Name = Path.GetFileName(p.TrimEnd('\\')).Length > 0 ? Path.GetFileName(p.TrimEnd('\\')) : p, IsFolder = Directory.Exists(p) }, 1.7, q);
                    it.Category = Cat.TopHit; it.Subtitle = "Open " + Paths.Pretty(p);
                    list.Add(it);
                }
                string dir = Directory.Exists(p) && p.EndsWith("\\") ? p : Path.GetDirectoryName(p);
                string prefix = Directory.Exists(p) && p.EndsWith("\\") ? "" : Path.GetFileName(p);
                if (dir == null || !Directory.Exists(dir)) return list;
                var di = new DirectoryInfo(dir);
                var entries = di.EnumerateDirectories().Cast<FileSystemInfo>().Concat(di.EnumerateFiles())
                    .Where(e => (e.Attributes & FileAttributes.Hidden) == 0 && e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Take(20);
                foreach (var e in entries)
                {
                    bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                    var it = FileItem(new FileHit { Path = e.FullName, Name = e.Name, IsFolder = isDir, Modified = e.LastWriteTime }, 1.2, q);
                    it.Category = Cat.Paths;
                    list.Add(it);
                }
            }
            catch { }
            return list;
        }

        IEnumerable<ResultItem> HelpItems()
        {
            var list = new List<ResultItem>();
            foreach (var s in Commands.Sets)
            {
                var it = new ResultItem
                {
                    Id = "help:" + s.Name, Title = s.Canonical + " -", Category = Cat.Help, Group = "App commands  ·  Enter to browse",
                    Subtitle = s.Name + ":  " + string.Join(", ", s.Commands.Take(7).Select(c => c.Name)),
                    IconPath = Commands.IconFor(s, Apps, AppsVersion), Glyph = s.Glyph ?? Glyphs.Bolt, GlyphBrush = Glyphs.Indigo,
                    AutoComplete = s.Canonical + " - ", KeepOpen = true, ActionName = "Browse", Kind = "Command set"
                };
                var set = s;
                it.Execute = m => Host.SetQuery(set.Canonical + " - ");
                it.Note = string.Join("\n", set.Commands.Select(c => c.Name + (string.IsNullOrEmpty(c.Description) ? "" : "  ·  " + c.Description)));
                list.Add(it);
            }
            Action<string, string, string> tip = (title, sub, fill) =>
            {
                var it = Simple("tip:" + title, title, sub, Cat.Help, Glyphs.Info, Glyphs.Gray, 0, null);
                it.Group = "Tips"; it.KeepOpen = true; it.AutoComplete = fill; it.ActionName = "Try it";
                it.Execute = m => Host.SetQuery(fill);
                list.Add(it);
            };
            tip("> ipconfig", "Run a command in a terminal (Ctrl+Shift+Enter: as admin)", "> ");
            tip("= 2^10 / 4", "Calculator — also “15% of 80”, “5 km to mi”, “72 f to c”", "= ");
            tip("g / yt / gh / wiki …", "Web shortcuts — e.g. “yt lofi”, edit them in commands.json", "g ");
            tip("C:\\ or ~\\", "Browse folders by typing a path (Tab completes)", "~\\");
            tip("lumen - commands", "Add your own app commands", "lumen - ");
            return list;
        }

        public IEnumerable<ResultItem> RecentItems()
        {
            var list = new List<ResultItem>();
            var apps = Apps.Apps;
            foreach (var id in Usage.MostUsed(12))
            {
                ResultItem it = null;
                if (id.StartsWith("app:"))
                {
                    var a = apps.FirstOrDefault(x => x.Id == id);
                    if (a != null) it = AppItem(a, 0);
                }
                else if (id.StartsWith("file:"))
                {
                    string path = id.Substring(5);
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        var h = new FileHit { Path = path, Name = Path.GetFileName(path), IsFolder = Directory.Exists(path) };
                        FileSearcher.Enrich(h);
                        it = FileItem(h, 0, "");
                    }
                }
                else
                {
                    var e = SystemCatalog.Entries.FirstOrDefault(x => x.Id == id);
                    if (e != null) it = CatalogItem(e, 0);
                }
                if (it != null) { it.Category = Cat.Recent; list.Add(it); }
                if (list.Count >= 8) break;
            }
            return list;
        }

        /// <summary>Builds the final list: Top Hit, then sections in a fixed order, with headers.</summary>
        public List<ResultItem> Compose(SearchResult r, List<FileHit> names, List<FileHit> contents)
        {
            var all = new List<ResultItem>(r.Items);
            if (names != null || contents != null)
            {
                string norm = Matcher.NormalizeQuery(r.Query);
                var words = Matcher.Words(norm);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in (names ?? new List<FileHit>()).Concat(contents ?? new List<FileHit>()))
                {
                    if (!seen.Add(h.Path)) continue;
                    double s = h.ContentMatch ? 0.35 : Math.Max(0.45, Matcher.Score(norm, words, new MatchTarget(Path.GetFileNameWithoutExtension(h.Name))));
                    if (!h.ContentMatch && h.Modified > DateTime.Now.AddDays(-7)) s += 0.03;
                    s = s * 0.8 + Boost(r, "file:" + h.Path);
                    all.Add(FileItem(h, s, r.Query));
                }
            }

            ResultItem top = null;
            if (!r.CommandMode)
            {
                top = all.Where(i => i.Category == Cat.TopHit).OrderByDescending(i => i.Score).FirstOrDefault()
                   ?? all.Where(i => i.Category == Cat.Calculator).FirstOrDefault()
                   ?? all.Where(i => i.Category != Cat.Web && i.Category != Cat.Help && i.Category != Cat.Recent && i.Score >= 0.55)
                         .OrderByDescending(i => i.Score).FirstOrDefault();
            }

            var output = new List<ResultItem>();
            if (top != null)
            {
                output.Add(ResultItem.Header(Cat.TopHit));
                output.Add(top);
            }
            foreach (var cat in Order)
            {
                if (cat == Cat.TopHit) continue;
                var inCat = all.Where(i => i != top && i.Category == cat);
                // Within each section keep insertion order for commands/help, otherwise sort by score.
                var sorted = (cat == Cat.Help || cat == Cat.Recent ? inCat : inCat.OrderByDescending(i => i.Score)).Take(Cap(cat, r.CommandMode)).ToList();
                if (sorted.Count == 0) continue;
                string group = null;
                foreach (var it in sorted)
                {
                    string g = it.Group ?? cat;
                    if (g != group) { output.Add(ResultItem.Header(g)); group = g; }
                    output.Add(it);
                }
            }
            // Leftover TopHit-category items that weren't chosen (e.g. two web shortcuts)
            foreach (var extra in all.Where(i => i.Category == Cat.TopHit && i != top))
                output.Add(extra);
            return output;
        }
    }
}
