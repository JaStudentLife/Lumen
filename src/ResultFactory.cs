using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Lumen
{
    /// <summary>Turns provider data (apps, commands, files...) into result rows.</summary>
    internal sealed partial class SearchEngine
    {
        ResultItem AppItem(AppEntry a, double score)
        {
            var set = Commands.SetForApp(a);
            var it = new ResultItem
            {
                Id = a.Id, Title = a.Name, Category = Cat.Apps, Score = score, IconPath = a.IconPath,
                Subtitle = set != null ? "Application  ·  Tab for commands" : "Application",
                ActionName = "Open", Kind = "Application"
            };
            if (a.ExePath != null)
            {
                it.AltActionName = "Open file location"; it.SupportsAdmin = true; it.FilePath = a.ExePath; it.CopyText = a.ExePath;
                it.AddDetail("Location", Paths.Pretty(Path.GetDirectoryName(a.ExePath)));
            }
            it.Execute = m => AppIndex.Launch(a, m);
            if (set != null)
            {
                it.AutoComplete = set.Canonical + " - ";
                it.Note = "Commands — type “" + set.Canonical + " -”\n" + string.Join("\n", set.Commands.Take(8).Select(c => "  " + c.Name + (string.IsNullOrEmpty(c.Description) ? "" : "  ·  " + c.Description)));
            }
            return it;
        }

        public ResultItem CommandItem(AppCommand c, string arg, double score)
        {
            arg = arg ?? "";
            string icon = Commands.IconFor(c.Set, Apps, AppsVersion);
            string effective = arg.Length > 0 ? arg : Placeholders.Expand(c.ArgDefault ?? "", "");
            var it = new ResultItem
            {
                Id = c.Id, Category = Cat.Commands, Score = score, IconPath = icon,
                Glyph = c.Glyph ?? c.Set.Glyph ?? Glyphs.Bolt, GlyphBrush = Glyphs.Indigo,
                Title = c.Set.Name + "  ›  " + c.Name + (arg.Length > 0 ? "  " + arg : ""),
                Subtitle = c.Description, ActionName = "Run", Kind = c.Set.Generated ? "Command · learned from the web" : "Command", Confirm = c.Confirm,
                AutoComplete = c.Set.Canonical + " - " + c.Name + " ",
                Hint = c.ArgLabel != null && arg.Length == 0 ? c.ArgLabel : null
            };
            // Say exactly what will happen where we can.
            string type = Json.Str(c.Action, "type", "run");
            if (type == "builtin" && Json.Str(c.Action, "name", "") == "office-new")
            {
                string path = Builtins.OfficeTargetPath(Json.Str(c.Action, "app", "Excel"), arg);
                if (path != null) it.Subtitle = (File.Exists(path) ? "Opens " : "Creates ") + Paths.Pretty(path);
            }
            else if (Json.Str(c.Action, "createFolder", "").Contains("{arg") && effective.Length > 0)
            {
                string folder = Placeholders.Expand(Json.Str(c.Action, "createFolder", ""), effective);
                it.Subtitle = (Directory.Exists(folder) ? "Opens " : "Creates ") + Paths.Pretty(folder);
            }
            else if (c.ArgRequired && arg.Length == 0)
                it.Subtitle = c.Description + (c.Suggest != null ? "  ·  pick below or keep typing" : "  ·  type the " + (c.ArgLabel ?? "argument"));

            bool needsArg = c.ArgRequired && arg.Length == 0;
            if (needsArg) it.KeepOpen = true;
            it.Execute = m =>
            {
                if (needsArg) Host.SetQuery(c.Set.Canonical + " - " + c.Name + " ");
                else ActionRunner.Run(c, arg, Host);
            };
            it.Note = c.Description;
            it.AddDetail("Usage", c.Usage);
            if (c.Aliases.Count > 0) it.AddDetail("Also", string.Join(", ", c.Aliases));
            it.AddDetail("Does", DescribeAction(c));
            return it;
        }

        static string DescribeAction(AppCommand c)
        {
            var a = c.Action;
            switch (Json.Str(a, "type", "run"))
            {
                case "run":
                    var args = Json.Arr(a, "args") != null ? string.Join(" ", Json.Strings(a, "args")) : Json.Str(a, "args", "");
                    return "Runs " + Json.Str(a, "file", "") + (args.Length > 0 ? " " + args : "") + (Json.Bool(a, "admin", false) ? " as admin" : "");
                case "open": case "url": return "Opens " + Json.Str(a, "target", Json.Str(a, "url", ""));
                case "powershell": return "Runs a PowerShell script";
                case "keys": return "Sends " + Json.Str(a, "keys", "") + " to " + Json.Str(a, "process", "the app");
                case "media": return "Presses the " + Json.Str(a, "key", "") + " media key";
                case "builtin": return "Built-in: " + Json.Str(a, "name", "");
            }
            return null;
        }

        ResultItem SuggestionItem(AppCommand c, Suggestion s, double score)
        {
            bool local = s.Value != null && (File.Exists(s.Value) || Directory.Exists(s.Value));
            var it = new ResultItem
            {
                Id = "sug:" + c.Id + ":" + s.Value, Category = Cat.Suggestions, Group = c.Set.Name + " › " + c.Name,
                Title = s.Title, Subtitle = s.Subtitle, Hint = s.Hint, Score = score,
                IconPath = s.IconPath ?? Commands.IconFor(c.Set, Apps, AppsVersion), Glyph = Glyphs.Bolt, GlyphBrush = Glyphs.Indigo,
                ActionName = c.Name.Substring(0, 1).ToUpperInvariant() + c.Name.Substring(1), Kind = c.Set.Name + " · " + c.Name,
                AutoComplete = c.Set.Canonical + " - " + c.Name + " " + s.Title, CopyText = s.Value,
                FilePath = local ? s.Value : null, AltActionName = local ? "Show in folder" : null, PreviewImagePath = local ? s.Value : null
            };
            it.AddDetail("Where", s.Subtitle);
            it.Execute = m =>
            {
                if (m == ExecMode.Alternate && local) Shell.ShowInFolder(s.Value);
                else ActionRunner.Run(c, s.Value, Host);
            };
            return it;
        }

        static ResultItem CatalogItem(CatalogEntry e, double score)
        {
            var it = new ResultItem
            {
                Id = e.Id, Title = e.Name, Subtitle = e.Subtitle, Category = e.Category, Score = score,
                Glyph = e.Glyph, GlyphBrush = e.Tile, Confirm = e.Confirm, ActionName = e.Category == Cat.System ? "Run" : "Open",
                Kind = e.Category == Cat.System ? "System action" : e.Subtitle
            };
            it.Execute = m =>
            {
                if (Shell.DryRun && e.Category == Cat.System) Shell.DryRunLog.Add("system " + e.Name);
                else e.Run();
            };
            return it;
        }

        static ResultItem WindowItem(OpenWindow w, double score)
        {
            var it = new ResultItem
            {
                Id = "win:" + w.ProcessName, Title = w.Title, Subtitle = w.ProcessName + "  ·  switch to this window",
                Category = Cat.Windows, Score = score, IconPath = w.ExePath, Glyph = Glyphs.Window, GlyphBrush = Glyphs.Gray,
                ActionName = "Switch", Kind = "Open window"
            };
            it.AddDetail("App", w.ProcessName);
            it.Execute = m => { if (Shell.DryRun) Shell.DryRunLog.Add("switch-to " + w.ProcessName); else Native.ForceForeground(w.Handle); };
            return it;
        }

        public static ResultItem FileItem(FileHit h, double score, string query)
        {
            string dir = Path.GetDirectoryName(h.Path) ?? h.Path;
            var it = new ResultItem
            {
                Id = "file:" + h.Path, Title = h.Name, Category = h.IsFolder ? Cat.Folders : Cat.Files, Score = score,
                Subtitle = (h.ContentMatch ? "Contains “" + query.Trim() + "”  ·  " : "") + Paths.Pretty(dir),
                Hint = Format.When(h.Modified), IconPath = h.Path, FilePath = h.Path, CopyText = h.Path,
                ActionName = "Open", AltActionName = "Show in folder", Kind = h.TypeText ?? (h.IsFolder ? "Folder" : "File"),
                PreviewImagePath = h.Path, AutoComplete = h.IsFolder ? h.Path.TrimEnd('\\') + "\\" : null
            };
            it.AddDetail("Where", Paths.Pretty(dir));
            if (!h.IsFolder && h.Size > 0) it.AddDetail("Size", Format.Size(h.Size));
            if (h.Modified != DateTime.MinValue) it.AddDetail("Modified", h.Modified.ToString("f"));
            it.Execute = m => { if (m == ExecMode.Alternate) Shell.ShowInFolder(h.Path); else Shell.Open(h.Path); };
            return it;
        }

        ResultItem BookmarkItem(Bookmark b, double score)
        {
            var it = new ResultItem
            {
                Id = "bm:" + b.Url, Title = b.Name, Subtitle = Bookmarks.HostOf(b.Url) + "  ·  " + b.Browser + " bookmark",
                Category = Cat.Bookmarks, Score = score, IconPath = Shell.ResolveExe(b.BrowserExe), Glyph = Glyphs.Star, GlyphBrush = Glyphs.Orange,
                CopyText = b.Url, ActionName = "Open", Kind = b.Browser + " bookmark"
            };
            it.AddDetail("URL", b.Url);
            it.Execute = m => Shell.Run(b.BrowserExe, new List<string> { b.Url }, null, false, false);
            return it;
        }

        static ResultItem WebItem(string name, string url, string query, double score, string id)
        {
            var it = new ResultItem
            {
                Id = id, Title = "Search " + name + " for “" + query + "”", Subtitle = Bookmarks.HostOf(url),
                Category = Cat.Web, Score = score, Glyph = Glyphs.Globe, GlyphBrush = Glyphs.Blue, ActionName = "Search",
                Kind = "Web search", CopyText = url
            };
            it.AddDetail("URL", url);
            it.Execute = m => Shell.Open(url);
            return it;
        }

        static ResultItem Simple(string id, string title, string subtitle, string cat, string glyph, System.Windows.Media.Brush tile, double score, Action<ExecMode> run)
        {
            return new ResultItem { Id = id, Title = title, Subtitle = subtitle, Category = cat, Glyph = glyph, GlyphBrush = tile, Score = score, Execute = run, ActionName = "Open", Kind = cat };
        }

        static void RunInTerminal(string command, bool admin)
        {
            string shell = Settings.Current.TerminalShell;
            ProcessStartInfo psi;
            if (shell == "cmd") psi = new ProcessStartInfo("cmd.exe", "/k " + command);
            else psi = new ProcessStartInfo(shell == "pwsh" ? (Shell.ResolveExe("pwsh") ?? "pwsh.exe") : "powershell.exe", "-NoExit -Command " + command);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Paths.Home;
            if (admin) psi.Verb = "runas";
            Shell.Start(psi);
        }
    }
}
