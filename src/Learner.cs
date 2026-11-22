using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Lumen
{
    /// <summary>
    /// Notices newly installed apps and "learns" commands for them: Claude researches the app on the web
    /// (official command-line switches, URI schemes, keyboard shortcuts, common tasks) and returns
    /// command definitions, which are validated and saved to %APPDATA%\Lumen\commands.generated.json.
    /// </summary>
    internal sealed class AppLearner
    {
        static string KnownFile { get { return Path.Combine(Paths.DataDir, "apps.known.json"); } }
        public static string GeneratedFile { get { return Path.Combine(Paths.ConfigDir, "commands.generated.json"); } }

        readonly AppIndex apps;
        readonly object gate = new object();
        readonly HashSet<string> learning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AppLearner(AppIndex apps) { this.apps = apps; }

        public static string ApiKey
        {
            get
            {
                string k = Settings.Current.AnthropicApiKey;
                if (string.IsNullOrWhiteSpace(k)) k = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                return string.IsNullOrWhiteSpace(k) ? null : k.Trim();
            }
        }

        /// <summary>Called after every app re-index. Finds apps that weren't there last time.</summary>
        public void CheckForNewApps()
        {
            var current = apps.Apps;
            if (current.Count == 0) return;
            List<AppEntry> added;
            lock (gate)
            {
                bool firstRun = !File.Exists(KnownFile);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!firstRun)
                {
                    try
                    {
                        var l = Json.Parse(File.ReadAllText(KnownFile)) as List<object>;
                        if (l != null) foreach (var o in l) if (o != null) known.Add(o.ToString());
                    }
                    catch { }
                }
                added = firstRun ? new List<AppEntry>() : current.Where(a => !known.Contains(a.AppId)).ToList();
                foreach (var a in current) known.Add(a.AppId);
                try { File.WriteAllText(KnownFile, Json.Serialize(known.Cast<object>().ToList(), false)); } catch { }
            }
            if (added.Count == 0) return;
            Log.Info("New apps: " + string.Join(", ", added.Select(a => a.Name)));
            if (added.Count > 8) { Shell.Report("Lumen found " + added.Count + " new apps", "They're searchable now."); return; }
            foreach (var a in added)
            {
                bool learn = Settings.Current.AutoLearnNewApps && ApiKey != null && !HasCommands(a.Name);
                Shell.Report("New app: " + a.Name, learn ? "It's searchable now. Researching its common features to build commands…"
                                                         : "It's searchable now. Type “lumen - learn " + a.Name + "” to build commands for it.");
                if (learn) Learn(a, false);
            }
        }

        static bool HasCommands(string appName)
        {
            try
            {
                var reg = CommandRegistry.Load(Paths.CommandsFile);
                return reg.Sets.Any(s => s.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public void LearnByName(string name)
        {
            var a = apps.Apps.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                 ?? apps.Apps.FirstOrDefault(x => x.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                 ?? apps.Apps.FirstOrDefault(x => x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (a == null) { Shell.Report("Lumen", "No installed app matches “" + name + "”."); return; }
            if (ApiKey == null)
            {
                Shell.Report("Lumen needs a Claude API key to learn apps",
                    "Add \"anthropicApiKey\" in settings.json (lumen - settings) or set ANTHROPIC_API_KEY.");
                return;
            }
            Shell.Report("Learning " + a.Name, "Researching its most common features on the web…");
            Learn(a, true);
        }

        void Learn(AppEntry app, bool userAsked)
        {
            lock (gate) { if (!learning.Add(app.AppId)) return; }
            var t = new Thread(() =>
            {
                try
                {
                    var set = Research(app);
                    int n = set == null ? 0 : ((List<object>)set["commands"]).Count;
                    if (n == 0) { Shell.Report("Lumen couldn't find commands for " + app.Name, "You can add your own with “lumen - commands”."); return; }
                    Save(set);
                    string first = ((List<object>)set["match"])[0].ToString();
                    Shell.Report("Learned " + n + " commands for " + app.Name, "Type “" + first + " -” to see them.");
                }
                catch (Exception ex)
                {
                    Log.Error("Learning " + app.Name, ex);
                    if (userAsked || !(ex is WebException)) Shell.Report("Couldn't learn " + app.Name, ex.Message);
                }
                finally { lock (gate) learning.Remove(app.AppId); }
            });
            t.IsBackground = true; t.Name = "Lumen learn";
            t.Start();
        }

        // ------------------------------------------------------------------ Claude API (raw HTTP)
        const string Prompt =
@"You are building quick-launch commands for a Windows launcher. The user just installed this app:

  Name: {NAME}
  Executable: {EXE}
  App ID: {AUMID}

Use web search to find the app's most frequently used actions that can be triggered from outside the app,
using only documented mechanisms: official command-line switches, the app's registered URI scheme (e.g. spotify:, zoommtg:, ms-teams:),
web URLs of its online service, or its keyboard shortcuts (sent to its window). Prefer the official documentation.
Pick the 4-10 most useful commands (things like: new document/window/project, private window, open recent,
search inside the app, start a meeting, compose). Do not invent switches you can't find documented.

Reply with ONLY a JSON object, no prose, in exactly this shape:
{
  ""match"": [""short name people type"", ""other names""],
  ""commands"": [
    { ""name"": ""new"", ""aliases"": [""create""], ""description"": ""short description"",
      ""arg"": ""label for an optional typed argument, or omit"", ""argRequired"": false,
      ""action"": { ""type"": ""run"", ""args"": [""--new-window"", ""{arg}""] } }
  ]
}
Action types you may use:
  { ""type"": ""run"", ""args"": [...] }                       runs the app's own executable with these arguments
  { ""type"": ""open"", ""target"": ""scheme:or https://..."" }  opens a URI or web URL
  { ""type"": ""keys"", ""keys"": ""^n"" }                       sends keys to the app's window (.NET SendKeys syntax: ^ Ctrl, % Alt, + Shift)
Placeholders: {arg} is what the user typed after the command, {arg:url} is the URL-encoded version.
Command names are 1-2 lowercase words. If the app is a Store app with no executable, don't use ""run"".";

        Dictionary<string, object> Research(AppEntry app)
        {
            string exeName = app.ExePath == null ? null : Path.GetFileName(app.ExePath);
            string prompt = Prompt.Replace("{NAME}", app.Name).Replace("{EXE}", app.ExePath ?? "(Store app, no executable)").Replace("{AUMID}", app.AppId);
            var messages = new List<object> { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } };
            string text = null;
            for (int turn = 0; turn < 6; turn++)
            {
                var body = new Dictionary<string, object>
                {
                    { "model", string.IsNullOrWhiteSpace(Settings.Current.LearnModel) ? "claude-opus-5-5" : Settings.Current.LearnModel },
                    { "max_tokens", 16000.0 },
                    { "output_config", new Dictionary<string, object> { { "effort", "medium" } } },
                    { "fallbacks", "default" },
                    { "tools", new List<object> { new Dictionary<string, object> { { "type", "web_search_20260209" }, { "name", "web_search" }, { "max_uses", 6.0 } } } },
                    { "messages", messages }
                };
                var resp = Post(Json.Serialize(body, false));
                string stop = Json.Str(resp, "stop_reason", "");
                var content = Json.Arr(resp, "content") ?? new List<object>();
                if (stop == "refusal") throw new InvalidOperationException("Claude declined to research this app.");
                if (stop == "pause_turn")
                {
                    // Long-running server tool turn: send the partial turn back unchanged to let it continue.
                    messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", content } });
                    continue;
                }
                text = string.Concat(content.Where(b => Json.Str(b, "type", "") == "text").Select(b => Json.Str(b, "text", "")));
                break;
            }
            if (string.IsNullOrEmpty(text)) return null;
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            if (s < 0 || e <= s) return null;
            var parsed = Json.Parse(text.Substring(s, e - s + 1)) as Dictionary<string, object>;
            return Validate(parsed, app, exeName);
        }

        static Dictionary<string, object> Post(string json)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 / 1.3
            var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["x-api-key"] = ApiKey;
            req.Headers["anthropic-version"] = "2026-10-21";
            req.Headers["anthropic-beta"] = "server-side-fallback-2026-07-01";
            req.Timeout = 300000; req.ReadWriteTimeout = 300000;
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return (Dictionary<string, object>)Json.Parse(sr.ReadToEnd());
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) throw;
                string detail;
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) detail = sr.ReadToEnd();
                string msg = Json.Str(Json.Obj(Json.Parse(detail), "error"), "message", detail);
                int code = (int)r.StatusCode;
                throw new InvalidOperationException(code == 401 ? "The Claude API key was rejected." :
                    code == 429 ? "Claude API rate limit reached; try again in a minute." : "Claude API error " + code + ": " + msg);
            }
        }

        // ------------------------------------------------------------------ safety checks
        /// <summary>
        /// Internet-derived commands are restricted: "run" may only start this app's own exe (never as admin),
        /// "open" only web URLs or app URI schemes (no files, scripts, shell: or file: links), "keys" only go to this app.
        /// </summary>
        static Dictionary<string, object> Validate(Dictionary<string, object> raw, AppEntry app, string exeName)
        {
            if (raw == null) return null;
            string[] badSchemes = { "file", "shell", "javascript", "vbscript", "data", "ms-msdt", "search-ms", "cmd", "powershell" };
            var cmds = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Json.Arr(raw, "commands") ?? new List<object>())
            {
                string name = Json.Str(c, "name", "").Trim().ToLowerInvariant();
                if (name.Length == 0 || name.Length > 24 || !seen.Add(name)) continue;
                var a = Json.Obj(c, "action");
                string type = Json.Str(a, "type", "").ToLowerInvariant();
                var action = new Dictionary<string, object> { { "type", type } };
                if (type == "run")
                {
                    if (exeName == null) continue;
                    action["file"] = app.ExePath;
                    action["args"] = Json.Strings(a, "args").Where(x => x.Length < 300).Cast<object>().ToList();
                }
                else if (type == "open")
                {
                    string target = Json.Str(a, "target", "").Trim();
                    int colon = target.IndexOf(':');
                    if (colon < 2) continue;
                    string scheme = target.Substring(0, colon).ToLowerInvariant();
                    if (badSchemes.Contains(scheme) || !scheme.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' || ch == '+')) continue;
                    if (target.Contains("\\") || target.Contains("%")) { if (!scheme.StartsWith("http")) continue; }
                    action["target"] = target;
                }
                else if (type == "keys")
                {
                    if (exeName == null) continue;
                    string keys = Json.Str(a, "keys", "");
                    if (keys.Length == 0 || keys.Length > 40) continue;
                    action["process"] = Path.GetFileNameWithoutExtension(exeName);
                    action["launch"] = app.ExePath;
                    action["keys"] = keys;
                }
                else continue;

                var cmd = new Dictionary<string, object>
                {
                    { "name", name },
                    { "aliases", Json.Strings(c, "aliases").Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0 && x.Length <= 24).Take(4).Cast<object>().ToList() },
                    { "description", Trim(Json.Str(c, "description", ""), 90) },
                    { "action", action }
                };
                string arg = Json.Str(c, "arg", null);
                if (!string.IsNullOrWhiteSpace(arg)) cmd["arg"] = Trim(arg, 30);
                if (Json.Bool(c, "argRequired", false)) cmd["argRequired"] = true;
                cmds.Add(cmd);
                if (cmds.Count >= 12) break;
            }
            var match = Json.Strings(raw, "match").Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 1 && m.Length < 40 && !m.Contains("-")).Distinct().Take(5).ToList();
            string fallback = app.Name.ToLowerInvariant();
            if (!match.Contains(fallback)) match.Add(fallback);
            return new Dictionary<string, object>
            {
                { "name", app.Name }, { "match", match.Cast<object>().ToList() }, { "icon", app.Name },
                { "generated", true }, { "learnedOn", DateTime.Now.ToString("yyyy-MM-dd") }, { "commands", cmds }
            };
        }

        static string Trim(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static readonly object fileGate = new object();

        static void Save(Dictionary<string, object> set)
        {
            lock (fileGate)
            {
                Dictionary<string, object> root = null;
                try { if (File.Exists(GeneratedFile)) root = Json.Parse(File.ReadAllText(GeneratedFile)) as Dictionary<string, object>; } catch { }
                if (root == null) root = new Dictionary<string, object>();
                var list = Json.Arr(root, "apps") ?? new List<object>();
                list.RemoveAll(o => Json.Str(o, "name", "").Equals((string)set["name"], StringComparison.OrdinalIgnoreCase));
                list.Add(set);
                var ordered = new Dictionary<string, object>
                {
                    { "_about", "Commands Lumen learned automatically. Edit or delete freely; entries in commands.json with the same app name win." },
                    { "apps", list }
                };
                File.WriteAllText(GeneratedFile, Json.Serialize(ordered, true), new UTF8Encoding(false));
            }
        }
    }
}
