using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Lumen
{
    internal static class Paths
    {
        public static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");
        public static string SettingsFile { get { return Path.Combine(ConfigDir, "settings.json"); } }
        public static string CommandsFile { get { return Path.Combine(ConfigDir, "commands.json"); } }
        public static string AppCacheFile { get { return Path.Combine(DataDir, "apps.cache.json"); } }
        public static string UsageFile { get { return Path.Combine(DataDir, "usage.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "lumen.log"); } }
        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

        public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        public static string Documents { get { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } }
        public static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }
        public static string Pictures { get { return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); } }
        public static string Downloads { get { return Native.KnownFolder(Native.FOLDERID_Downloads, Path.Combine(Home, "Downloads")); } }

        public static void Ensure()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>Shortens C:\Users\me\... to ~\... for display.</summary>
        public static string Pretty(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string home = Home;
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return "~" + path.Substring(home.Length);
            return path;
        }
    }

    internal static class Log
    {
        static readonly object gate = new object();

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", msg + (ex == null ? "" : ": " + ex)); }

        static void Write(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>User settings from %APPDATA%\Lumen\settings.json.</summary>
    internal sealed class Settings
    {
        public string Hotkey = "Ctrl+Space";
        public string Theme = "auto";
        public string Accent = "auto";
        public double Width = 760;
        public bool ShowPreview = true;
        public bool SearchFiles = true;
        public bool SearchFileContents = true;
        public int MaxFileResults = 12;
        public List<string> FileExcludes = new List<string> { "\\AppData\\", "\\node_modules\\", "\\.git\\", "\\$Recycle.Bin\\", "\\.vscode\\extensions\\" };
        public string WebSearchName = "Google";
        public string WebSearchUrl = "https://www.google.com/search?q={query}";
        public string ProjectsFolder = "";
        public string OfficeSaveFolder = "";
        public List<string> PassThroughApps = new List<string>();
        public bool HideOnFocusLoss = true;
        public bool RememberLastQuery = true;
        public bool ShowRecentWhenEmpty = false;
        public string TerminalShell = "powershell";
        public string Editor = "";
        public string AnthropicApiKey = "";
        public bool AutoLearnNewApps = true;
        public string LearnModel = "claude-opus-5-5";

        public static Settings Current = new Settings();
        public static string LastError;

        public static Settings Load()
        {
            var s = new Settings();
            LastError = null;
            try
            {
                if (!File.Exists(Paths.SettingsFile)) return s;
                object j = Json.Parse(File.ReadAllText(Paths.SettingsFile));
                s.Hotkey = Json.Str(j, "hotkey", s.Hotkey);
                s.Theme = Json.Str(j, "theme", s.Theme).ToLowerInvariant();
                s.Accent = Json.Str(j, "accentColor", s.Accent);
                s.Width = Math.Max(560, Math.Min(1400, Json.Num(j, "width", s.Width)));
                s.ShowPreview = Json.Bool(j, "showPreview", s.ShowPreview);
                s.SearchFiles = Json.Bool(j, "searchFiles", s.SearchFiles);
                s.SearchFileContents = Json.Bool(j, "searchFileContents", s.SearchFileContents);
                s.MaxFileResults = (int)Math.Max(0, Math.Min(50, Json.Num(j, "maxFileResults", s.MaxFileResults)));
                if (Json.Arr(j, "fileSearchExclude") != null) s.FileExcludes = Json.Strings(j, "fileSearchExclude");
                s.WebSearchName = Json.Str(j, "webSearchName", s.WebSearchName);
                s.WebSearchUrl = Json.Str(j, "webSearchUrl", s.WebSearchUrl);
                s.ProjectsFolder = Json.Str(j, "projectsFolder", s.ProjectsFolder);
                s.OfficeSaveFolder = Json.Str(j, "officeSaveFolder", s.OfficeSaveFolder);
                s.PassThroughApps = Json.Strings(j, "passThroughApps");
                s.HideOnFocusLoss = Json.Bool(j, "hideOnFocusLoss", s.HideOnFocusLoss);
                s.RememberLastQuery = Json.Bool(j, "rememberLastQuery", s.RememberLastQuery);
                s.ShowRecentWhenEmpty = Json.Bool(j, "showRecentWhenEmpty", s.ShowRecentWhenEmpty);
                s.TerminalShell = Json.Str(j, "terminalShell", s.TerminalShell).ToLowerInvariant();
                s.Editor = Json.Str(j, "editor", s.Editor);
                s.AnthropicApiKey = Json.Str(j, "anthropicApiKey", s.AnthropicApiKey);
                s.AutoLearnNewApps = Json.Bool(j, "autoLearnNewApps", s.AutoLearnNewApps);
                s.LearnModel = Json.Str(j, "learnModel", s.LearnModel);
            }
            catch (Exception ex)
            {
                LastError = "settings.json: " + ex.Message;
                Log.Error("Loading settings", ex);
            }
            return s;
        }

        /// <summary>Where "code - code" puts new sessions. Auto-detects common folders.</summary>
        public string ResolvedProjectsFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ProjectsFolder)) return Environment.ExpandEnvironmentVariables(ProjectsFolder.Trim());
                string home = Paths.Home;
                string[] candidates =
                {
                    Path.Combine(home, "Projects"), Path.Combine(home, "source", "repos"), Path.Combine(home, "Code"),
                    Path.Combine(home, "dev"), Path.Combine(Paths.Documents, "GitHub"), Path.Combine(Paths.Documents, "Projects")
                };
                foreach (var c in candidates) if (Directory.Exists(c)) return c;
                return candidates[0];
            }
        }

        public string ResolvedOfficeFolder
        {
            get
            {
                return string.IsNullOrWhiteSpace(OfficeSaveFolder) ? Paths.Documents
                    : Environment.ExpandEnvironmentVariables(OfficeSaveFolder.Trim());
            }
        }

        /// <summary>Writes the default settings.json / commands.json the first time Lumen runs.</summary>
        public static bool EnsureDefaultFiles()
        {
            bool firstRun = !File.Exists(Paths.SettingsFile);
            if (firstRun) File.WriteAllText(Paths.SettingsFile, Resource("Lumen.settings.json"), new UTF8Encoding(false));
            if (!File.Exists(Paths.CommandsFile)) File.WriteAllText(Paths.CommandsFile, Resource("Lumen.commands.json"), new UTF8Encoding(false));
            return firstRun;
        }

        public static string Resource(string name)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (st == null) return "{}";
                using (var r = new StreamReader(st, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }

    /// <summary>Expands {placeholders} used by commands and web shortcuts.</summary>
    internal static class Placeholders
    {
        static readonly char[] BadFileChars = Path.GetInvalidFileNameChars();

        public static string Expand(string template, string arg)
        {
            if (string.IsNullOrEmpty(template)) return template ?? "";
            arg = arg ?? "";
            // Environment variables are expanded in the template only, never inside what the user typed.
            string t = Environment.ExpandEnvironmentVariables(template);
            var sb = new StringBuilder();
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '{')
                {
                    int end = t.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        string key = t.Substring(i + 1, end - i - 1);
                        string val = Lookup(key, arg);
                        if (val != null) { sb.Append(val); i = end + 1; continue; }
                    }
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        static string Lookup(string key, string arg)
        {
            switch (key.ToLowerInvariant())
            {
                case "arg": return arg;
                case "arg:url": case "query": return Uri.EscapeDataString(arg);
                case "arg:file": return SafeFileName(arg);
                case "projects": return Settings.Current.ResolvedProjectsFolder;
                case "documents": return Paths.Documents;
                case "desktop": return Paths.Desktop;
                case "downloads": return Paths.Downloads;
                case "pictures": return Paths.Pictures;
                case "home": return Paths.Home;
                case "officefolder": return Settings.Current.ResolvedOfficeFolder;
                case "date": return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "time": return DateTime.Now.ToString("HHmm", CultureInfo.InvariantCulture);
                case "datetime": return DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
                case "clipboard":
                    try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                    catch { return ""; }
                default: return null; // unknown: leave the text alone
            }
        }

        public static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim()) sb.Append(BadFileChars.Contains(c) ? '-' : c);
            return sb.ToString().TrimEnd('.', ' ');
        }

        /// <summary>Quotes one argument using the Windows command-line rules.</summary>
        public static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes); slashes = 0; sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
