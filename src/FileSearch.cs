using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Lumen
{
    internal sealed class FileHit
    {
        public string Path, Name, TypeText;
        public long Size;
        public DateTime Modified;
        public bool IsFolder, ContentMatch;
    }

    /// <summary>
    /// File and folder search through the Windows Search index (the same index Start uses), which
    /// matches word prefixes in names like Spotlight and can also search inside documents.
    /// Falls back to scanning your user folders if the index is unavailable.
    /// </summary>
    internal sealed class FileSearcher
    {
        const string ConnString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";
        readonly object gate = new object();
        OleDbConnection conn;
        bool indexBroken;
        List<FileHit> fallback;
        int fallbackBuilding;

        static List<string> Terms(string query)
        {
            var terms = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in query + " ")
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { terms.Add(sb.ToString()); sb.Clear(); }
            }
            return terms.Take(6).ToList();
        }

        public List<FileHit> SearchNames(string query, int max, CancellationToken ct)
        {
            var terms = Terms(query);
            if (terms.Count == 0 || max <= 0) return new List<FileHit>();
            if (indexBroken) return SearchFallback(query, max);
            string where = string.Join(" AND ", terms.Select(t => "CONTAINS(System.FileName, '\"" + t + "*\"')"));
            var hits = Query(where, "System.DateModified DESC", max * 3, ct, false);
            if (hits == null) return SearchFallback(query, max);
            return hits;
        }

        public List<FileHit> SearchContents(string query, int max, CancellationToken ct)
        {
            var terms = Terms(query);
            if (terms.Count == 0 || indexBroken || query.Trim().Length < 3) return new List<FileHit>();
            string where = "CONTAINS('" + string.Join(" AND ", terms.Select(t => "\"" + t + "*\"")) + "')";
            return Query(where, "System.Search.Rank DESC", max * 2, ct, true) ?? new List<FileHit>();
        }

        List<FileHit> Query(string where, string order, int top, CancellationToken ct, bool content)
        {
            string sql = "SELECT TOP " + top + " System.ItemPathDisplay, System.ItemNameDisplay, System.ItemTypeText, System.FileAttributes "
                       + "FROM SystemIndex WHERE SCOPE='file:' AND " + where + " ORDER BY " + order;
            var result = new List<FileHit>();
            lock (gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (conn == null) { conn = new OleDbConnection(ConnString); conn.Open(); }
                        using (var cmd = new OleDbCommand(sql, conn))
                        using (ct.Register(() => { try { cmd.Cancel(); } catch { } }))
                        {
                            cmd.CommandTimeout = 5;
                            using (var r = cmd.ExecuteReader())
                            {
                                while (r.Read())
                                {
                                    if (ct.IsCancellationRequested) return result;
                                    string path = r.IsDBNull(0) ? null : r.GetValue(0) as string;
                                    if (string.IsNullOrEmpty(path) || Excluded(path)) continue;
                                    long attrs = 0;
                                    try { if (!r.IsDBNull(3)) attrs = Convert.ToInt64(r.GetValue(3)); } catch { }
                                    if ((attrs & 0x6) != 0) continue; // hidden or system
                                    result.Add(new FileHit
                                    {
                                        Path = path,
                                        Name = r.IsDBNull(1) ? System.IO.Path.GetFileName(path) : Convert.ToString(r.GetValue(1)),
                                        TypeText = r.IsDBNull(2) ? null : Convert.ToString(r.GetValue(2)),
                                        IsFolder = (attrs & 0x10) != 0,
                                        ContentMatch = content
                                    });
                                }
                            }
                        }
                        return result;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) return result;
                        Log.Error("Windows Search query failed (attempt " + (attempt + 1) + ")", ex);
                        try { if (conn != null) conn.Dispose(); } catch { }
                        conn = null;
                        if (attempt == 1) { indexBroken = true; return null; }
                    }
                }
            }
            return result;
        }

        static bool Excluded(string path)
        {
            // Start menu shortcuts are already listed as apps.
            if (path.IndexOf(@"\Microsoft\Windows\Start Menu\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var x in Settings.Current.FileExcludes)
                if (x.Length > 0 && path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Fills in size and dates from the file system for the few hits we actually show.</summary>
        public static void Enrich(FileHit h)
        {
            try
            {
                if (h.IsFolder || Directory.Exists(h.Path))
                {
                    h.IsFolder = true;
                    h.Modified = Directory.GetLastWriteTime(h.Path);
                }
                else
                {
                    var fi = new FileInfo(h.Path);
                    if (fi.Exists) { h.Size = fi.Length; h.Modified = fi.LastWriteTime; }
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- fallback scan
        List<FileHit> SearchFallback(string query, int max)
        {
            var list = fallback;
            if (list == null) { BuildFallback(); return new List<FileHit>(); }
            string q = Matcher.NormalizeQuery(query);
            var words = Matcher.Words(q);
            return list.Select(h => new { h, s = Matcher.Score(q, words, new MatchTarget(h.Name)) })
                       .Where(x => x.s >= 0.6).OrderByDescending(x => x.s).Take(max * 3).Select(x => x.h).ToList();
        }

        void BuildFallback()
        {
            if (Interlocked.Exchange(ref fallbackBuilding, 1) == 1) return;
            new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                var list = new List<FileHit>();
                foreach (var root in new[] { Paths.Desktop, Paths.Documents, Paths.Downloads, Paths.Pictures })
                    Walk(root, 0, list);
                fallback = list;
                Log.Info("Fallback file index: " + list.Count + " items in " + sw.ElapsedMilliseconds + " ms");
            }) { IsBackground = true, Name = "Lumen file scan" }.Start();
        }

        static void Walk(string dir, int depth, List<FileHit> list)
        {
            if (depth > 5 || list.Count > 40000 || Excluded(dir + "\\")) return;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith(".")) continue;
                    list.Add(new FileHit { Path = d, Name = name, IsFolder = true });
                    Walk(d, depth + 1, list);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                    list.Add(new FileHit { Path = f, Name = Path.GetFileName(f) });
            }
            catch { }
        }
    }
}
