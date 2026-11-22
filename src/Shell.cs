using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace Lumen
{
    /// <summary>Launching things, resolving executables, clipboard, and user-visible error reporting.</summary>
    internal static class Shell
    {
        /// <summary>Raised to show a tray notification (title, message).</summary>
        public static event Action<string, string> Notify;
        /// <summary>When true, nothing is launched; actions are only described (used by --dry-run tests).</summary>
        public static bool DryRun;
        public static readonly List<string> DryRunLog = new List<string>();

        public static void Report(string title, string message)
        {
            Log.Info(title + ": " + message);
            var h = Notify;
            if (h != null) h(title, message);
        }

        public static Process Start(ProcessStartInfo psi)
        {
            if (DryRun)
            {
                DryRunLog.Add("start " + psi.FileName + " " + psi.Arguments + (psi.Verb == "runas" ? " [admin]" : "")
                    + (string.IsNullOrEmpty(psi.WorkingDirectory) ? "" : " [cwd " + psi.WorkingDirectory + "]"));
                return null;
            }
            // Let whatever we launch take the foreground even though our window is about to hide.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            try { return Process.Start(psi); }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return null; // user cancelled the UAC prompt
                Report("Couldn't start " + Path.GetFileName(psi.FileName), ex.Message);
                return null;
            }
        }

        public static void Open(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        public static void ShowInFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Directory.Exists(path) && !File.Exists(path) && path.Length <= 3) { Open(path); return; }
            Start(new ProcessStartInfo("explorer.exe", "/select," + Placeholders.QuoteArg(path)) { UseShellExecute = true });
        }

        /// <summary>Finds an executable the way the Run dialog does: full path, PATH + PATHEXT, then App Paths.</summary>
        public static string ResolveExe(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
            try
            {
                if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
                if (file.IndexOfAny(new[] { '\\', '/' }) >= 0) return File.Exists(Path.GetFullPath(file)) ? Path.GetFullPath(file) : null;

                bool hasExt = Path.HasExtension(file);
                var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToArray();
                var dirs = new List<string>();
                dirs.Add(Environment.SystemDirectory);
                dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim().Length > 0));
                foreach (var d in dirs)
                {
                    string dir = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
                    if (hasExt) { string p = Path.Combine(dir, file); if (File.Exists(p)) return p; }
                    else foreach (var e in exts) { string p = Path.Combine(dir, file + e); if (File.Exists(p)) return p; }
                }

                string name = hasExt ? file : file + ".exe";
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                    {
                        if (k == null) continue;
                        string p = (k.GetValue("") as string ?? "").Trim('"');
                        p = Environment.ExpandEnvironmentVariables(p);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Runs a program. Batch/cmd launchers (like VS Code's "code") run without a console window.</summary>
        public static void Run(string file, IList<string> args, string cwd, bool admin, bool showWindow)
        {
            string resolved = ResolveExe(file) ?? file;
            string argLine = string.Join(" ", args.Where(a => !string.IsNullOrEmpty(a)).Select(Placeholders.QuoteArg));
            string ext = Path.GetExtension(resolved).ToLowerInvariant();
            var psi = new ProcessStartInfo(resolved, argLine);
            if ((ext == ".cmd" || ext == ".bat") && !admin)
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = !showWindow;
            }
            else
            {
                psi.UseShellExecute = true;
                if (admin) psi.Verb = "runas";
            }
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            Start(psi);
        }

        public static void CopyText(string text)
        {
            if (text == null) return;
            if (DryRun) { DryRunLog.Add("copy " + text); return; }
            for (int i = 0; i < 5; i++)
            {
                try { System.Windows.Clipboard.SetText(text); return; }
                catch { Thread.Sleep(40); }
            }
        }

        public static void CopyFile(string path)
        {
            if (DryRun) { DryRunLog.Add("copy-file " + path); return; }
            try { System.Windows.Clipboard.SetFileDropList(new StringCollection { path }); }
            catch (Exception ex) { Report("Couldn't copy the file", ex.Message); }
        }

        /// <summary>Opens a text file in the configured editor, VS Code if installed, else Notepad.</summary>
        public static void Edit(string path)
        {
            string editor = Settings.Current.Editor;
            if (string.IsNullOrWhiteSpace(editor)) editor = ResolveExe("code") != null ? "code" : "notepad.exe";
            Run(editor, new List<string> { path }, null, false, false);
        }

        /// <summary>Runs work on a background STA thread (needed for Office/COM automation), reporting errors.</summary>
        public static readonly List<Thread> StaThreads = new List<Thread>();

        public static void RunSta(string what, Action work)
        {
            if (DryRun) { DryRunLog.Add("sta " + what); return; }
            var t = new Thread(() =>
            {
                try { work(); DryRunLog.Add("done: " + what); }
                catch (Exception ex)
                {
                    Log.Error(what, ex);
                    var inner = ex.InnerException ?? ex;
                    Report("Couldn't " + what, inner.Message);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            lock (StaThreads) { StaThreads.RemoveAll(x => !x.IsAlive); StaThreads.Add(t); }
            t.Start();
        }
    }
}
