using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Lumen
{
    internal interface ILumenHost
    {
        void Reindex();
        void ToggleStartup();
        void Quit();
        void SetQuery(string text);
        void ResetCommands();
        void Learn(string appName);
    }

    /// <summary>Executes the "action" object of an app command (run / open / powershell / keys / media / builtin).</summary>
    internal static class ActionRunner
    {
        public static void Run(AppCommand cmd, string typedArg, ILumenHost host)
        {
            string arg = string.IsNullOrWhiteSpace(typedArg) ? Placeholders.Expand(cmd.ArgDefault ?? "", "") : typedArg.Trim();
            var a = cmd.Action;
            string type = Json.Str(a, "type", "run").ToLowerInvariant();
            Log.Info("Command " + cmd.Usage + " arg='" + arg + "' type=" + type);
            switch (type)
            {
                case "run":
                    {
                        string folder = Json.Str(a, "createFolder", null);
                        if (!string.IsNullOrEmpty(folder))
                        {
                            folder = Placeholders.Expand(folder, arg);
                            if (Shell.DryRun) Shell.DryRunLog.Add("mkdir " + folder);
                            else Directory.CreateDirectory(folder);
                        }
                        string file = Placeholders.Expand(Json.Str(a, "file", ""), arg);
                        var args = new List<string>();
                        if (Json.Arr(a, "args") != null)
                            args.AddRange(Json.Strings(a, "args").Select(x => Placeholders.Expand(x, arg)).Where(x => x.Length > 0));
                        string cwd = Placeholders.Expand(Json.Str(a, "cwd", ""), arg);
                        string rawArgs = Json.Arr(a, "args") == null ? Placeholders.Expand(Json.Str(a, "args", ""), arg) : null;
                        if (rawArgs != null)
                        {
                            // A single string: passed through as typed (you handle the quoting).
                            string resolved = Shell.ResolveExe(file) ?? file;
                            var psi = new ProcessStartInfo(resolved, rawArgs) { UseShellExecute = true };
                            if (Json.Bool(a, "admin", false)) psi.Verb = "runas";
                            if (cwd.Length > 0) psi.WorkingDirectory = cwd;
                            Shell.Start(psi);
                        }
                        else Shell.Run(file, args, cwd, Json.Bool(a, "admin", false), Json.Bool(a, "showWindow", false));
                        break;
                    }
                case "open":
                case "url":
                    Shell.Open(Placeholders.Expand(Json.Str(a, "target", Json.Str(a, "url", "")), arg));
                    break;
                case "powershell":
                    {
                        string script = Json.Arr(a, "script") != null ? string.Join("\n", Json.Strings(a, "script")) : Json.Str(a, "script", "");
                        RunPowerShell(script, arg, Json.Bool(a, "showWindow", false));
                        break;
                    }
                case "keys":
                    SendKeysTo(Json.Str(a, "process", ""), Json.Str(a, "launch", null), Json.Str(a, "keys", ""), Json.Bool(a, "sendAfterLaunch", false));
                    break;
                case "media":
                    Media(Json.Str(a, "key", "playpause"));
                    break;
                case "builtin":
                    Builtins.Run(Json.Str(a, "name", ""), a, arg, host);
                    break;
                default:
                    Shell.Report("Unknown action type", "\"" + type + "\" in command " + cmd.Usage);
                    break;
            }
        }

        static void RunPowerShell(string script, string arg, bool show)
        {
            if (Shell.DryRun) { Shell.DryRunLog.Add("powershell " + script.Replace("\n", "; ") + " [LUMEN_ARG=" + arg + "]"); return; }
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe",
                (show ? "-NoExit " : "-WindowStyle Hidden ") + "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            { UseShellExecute = false, CreateNoWindow = !show };
            psi.EnvironmentVariables["LUMEN_ARG"] = arg ?? "";
            psi.EnvironmentVariables["LUMEN_PROJECTS"] = Settings.Current.ResolvedProjectsFolder;
            psi.EnvironmentVariables["LUMEN_DOCUMENTS"] = Paths.Documents;
            psi.EnvironmentVariables["LUMEN_DESKTOP"] = Paths.Desktop;
            psi.EnvironmentVariables["LUMEN_DOWNLOADS"] = Paths.Downloads;
            psi.EnvironmentVariables["LUMEN_OFFICE_FOLDER"] = Settings.Current.ResolvedOfficeFolder;
            Shell.Start(psi);
        }

        static void Media(string key)
        {
            var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
            {
                { "playpause", 0xB3 }, { "play", 0xB3 }, { "pause", 0xB3 }, { "next", 0xB0 }, { "previous", 0xB1 }, { "prev", 0xB1 },
                { "stop", 0xB2 }, { "mute", 0xAD }, { "volumedown", 0xAE }, { "volumeup", 0xAF }
            };
            byte vk;
            if (!map.TryGetValue(key.Replace(" ", ""), out vk)) { Shell.Report("Unknown media key", key); return; }
            if (Shell.DryRun) { Shell.DryRunLog.Add("media " + key); return; }
            Native.TapKey(vk);
        }

        static void SendKeysTo(string process, string launch, string keys, bool sendAfterLaunch)
        {
            if (Shell.DryRun) { Shell.DryRunLog.Add("keys " + keys + " -> " + process); return; }
            Shell.RunSta("send keys to " + process, () =>
            {
                IntPtr hwnd = MainWindowOf(process);
                if (hwnd == IntPtr.Zero)
                {
                    if (string.IsNullOrEmpty(launch)) { Shell.Report(process + " isn't running", "Start it first."); return; }
                    Shell.Run(launch, new List<string>(), null, false, false);
                    if (!sendAfterLaunch) return;
                    for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(200); hwnd = MainWindowOf(process); }
                    if (hwnd == IntPtr.Zero) return;
                    Thread.Sleep(400);
                }
                Native.ForceForeground(hwnd);
                Thread.Sleep(180);
                System.Windows.Forms.SendKeys.SendWait(keys);
            });
        }

        static IntPtr MainWindowOf(string process)
        {
            string name = Path.GetFileNameWithoutExtension(process ?? "");
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; }
                catch { }
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>Built-in actions that need real code: Office automation and Lumen's own commands.</summary>
    internal static class Builtins
    {
        public static void Run(string name, Dictionary<string, object> action, string arg, ILumenHost host)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "office-new": OfficeNew(Json.Str(action, "app", "Excel"), arg); break;
                case "excel-add-sheet": ExcelAddSheet(); break;
                case "lumen-settings": Shell.Edit(Paths.SettingsFile); break;
                case "lumen-commands": Shell.Edit(Paths.CommandsFile); break;
                case "lumen-folder": Shell.Open(Paths.ConfigDir); break;
                case "lumen-log": Shell.Edit(Paths.LogFile); break;
                case "lumen-reindex": host.Reindex(); break;
                case "lumen-startup": host.ToggleStartup(); break;
                case "lumen-reset-commands": host.ResetCommands(); break;
                case "lumen-help": host.SetQuery("?"); break;
                case "lumen-learn": host.Learn(arg); break;
                case "lumen-learned":
                    if (!File.Exists(AppLearner.GeneratedFile)) Shell.Report("Lumen", "Nothing learned yet. Try “lumen - learn <app>”.");
                    else Shell.Edit(AppLearner.GeneratedFile);
                    break;
                case "lumen-quit": host.Quit(); break;
                default: Shell.Report("Unknown builtin", "\"" + name + "\""); break;
            }
        }

        // ---------------------------------------------------------------- Office
        public static string OfficeTargetPath(string app, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string ext = Ext(app);
            string n = name.Trim().Trim('"');
            if (Path.IsPathRooted(n)) return n.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? n : n + ext;
            string file = Placeholders.SafeFileName(n);
            if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) file += ext;
            return Path.Combine(Settings.Current.ResolvedOfficeFolder, file);
        }

        static string Ext(string app)
        {
            switch (app.ToLowerInvariant()) { case "word": return ".docx"; case "powerpoint": return ".pptx"; default: return ".xlsx"; }
        }

        static void OfficeNew(string app, string name)
        {
            string lower = app.ToLowerInvariant();
            string path = OfficeTargetPath(app, name);
            string progId = lower == "word" ? "Word.Application" : lower == "powerpoint" ? "PowerPoint.Application" : "Excel.Application";
            Shell.RunSta("create a new " + app + " " + (lower == "excel" ? "workbook" : lower == "word" ? "document" : "presentation"), () =>
            {
                bool created;
                dynamic o = GetOrCreate(progId, out created);
                if (path != null) Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (lower == "word") WordNew(o, path);
                else if (lower == "powerpoint") PowerPointNew(o, path);
                else ExcelNew(o, created, path);
            });
        }

        static dynamic GetOrCreate(string progId, out bool created)
        {
            created = false;
            try { return Marshal.GetActiveObject(progId); }
            catch (COMException) { }
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) throw new InvalidOperationException(progId.Split('.')[0] + " doesn't seem to be installed.");
            created = true;
            return Activator.CreateInstance(t);
        }

        static void ExcelNew(dynamic xl, bool created, string path)
        {
            dynamic wb;
            bool exists = path != null && File.Exists(path);
            if (exists) wb = Retry(() => xl.Workbooks.Open(path));
            else wb = Retry(() => xl.Workbooks.Add());
            RetryDo(() => { xl.Visible = true; });
            if (created) RetryDo(() => { xl.UserControl = true; });
            if (path != null && !exists) RetryDo(() => { wb.SaveAs(path, 51); });
            RetryDo(() => { wb.Activate(); });
            try { if ((int)xl.WindowState == -4140) xl.WindowState = -4143; } catch { }
            try { Native.ForceForeground(new IntPtr((int)xl.Hwnd)); } catch { }
        }

        static void WordNew(dynamic w, string path)
        {
            dynamic doc;
            bool exists = path != null && File.Exists(path);
            if (exists) doc = Retry(() => w.Documents.Open(path));
            else doc = Retry(() => w.Documents.Add());
            if (path != null && !exists) RetryDo(() => { doc.SaveAs2(path, 16); });
            RetryDo(() => { w.Visible = true; });
            RetryDo(() => { doc.Activate(); });
            try { if ((int)w.WindowState == 2) w.WindowState = 0; } catch { }
            try { Native.ForceForeground(new IntPtr((int)doc.ActiveWindow.Hwnd)); } catch { }
        }

        static void PowerPointNew(dynamic pp, string path)
        {
            dynamic pres;
            bool exists = path != null && File.Exists(path);
            if (exists) pres = Retry(() => pp.Presentations.Open(path));
            else
            {
                pres = Retry(() => pp.Presentations.Add(-1));
                try { RetryDo(() => { pres.Slides.AddSlide(1, pres.SlideMaster.CustomLayouts.Item(1)); }); } catch { }
                if (path != null) RetryDo(() => { pres.SaveAs(path, 24); });
            }
            try { RetryDo(() => { pres.Windows.Item(1).Activate(); }); } catch { }
            try { Native.ForceForeground(new IntPtr((int)pp.HWND)); } catch { }
        }

        static void ExcelAddSheet()
        {
            Shell.RunSta("add a worksheet", () =>
            {
                dynamic xl;
                try { xl = Marshal.GetActiveObject("Excel.Application"); }
                catch (COMException) { OfficeNewSync(); return; }
                dynamic wb = xl.ActiveWorkbook;
                if (wb == null) wb = Retry(() => xl.Workbooks.Add());
                dynamic sheets = wb.Worksheets;
                dynamic last = sheets.Item(sheets.Count);
                dynamic ws = Retry(() => sheets.Add(Type.Missing, last));
                RetryDo(() => { ws.Activate(); });
                try { Native.ForceForeground(new IntPtr((int)xl.Hwnd)); } catch { }
            });
        }

        static void OfficeNewSync()
        {
            bool created;
            dynamic xl = GetOrCreate("Excel.Application", out created);
            ExcelNew(xl, created, null);
        }

        // Office rejects calls while it's busy starting up; retry those for a few seconds.
        static object Retry(Func<object> f)
        {
            for (int i = 0; ; i++)
            {
                try { return f(); }
                catch (Exception ex)
                {
                    if (i >= 40 || !IsBusy(ex)) throw;
                    Thread.Sleep(250);
                }
            }
        }

        static void RetryDo(Action f) { Retry(() => { f(); return null; }); }

        static bool IsBusy(Exception ex)
        {
            var tie = ex as TargetInvocationException;
            if (tie != null && tie.InnerException != null) ex = tie.InnerException;
            var com = ex as COMException;
            if (com == null) return false;
            uint code = (uint)com.ErrorCode;
            return code == 0x80010001 || code == 0x8001010A || code == 0x800AC472;
        }
    }
}
