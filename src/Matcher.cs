using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Lumen
{
    /// <summary>A searchable string, pre-processed once (lower-case, word starts, initials).</summary>
    internal sealed class MatchTarget
    {
        public readonly string Text;
        public readonly string Lower;
        public readonly int[] WordStarts;
        public readonly bool[] IsStart;
        public readonly string Initials;

        public MatchTarget(string text)
        {
            Text = text ?? "";
            string plain = Matcher.StripDiacritics(Text);
            Lower = plain.ToLowerInvariant();
            IsStart = new bool[plain.Length];
            var starts = new List<int>();
            var initials = new StringBuilder();
            for (int i = 0; i < plain.Length; i++)
            {
                char c = plain[i];
                if (!char.IsLetterOrDigit(c)) continue;
                bool start = i == 0;
                if (!start)
                {
                    char p = plain[i - 1];
                    start = !char.IsLetterOrDigit(p)
                        || (char.IsUpper(c) && char.IsLower(p))
                        || (char.IsDigit(c) && char.IsLetter(p))
                        || (char.IsLetter(c) && char.IsDigit(p));
                }
                if (start) { starts.Add(i); IsStart[i] = true; initials.Append(Lower[i]); }
            }
            WordStarts = starts.ToArray();
            Initials = initials.ToString();
        }
    }

    /// <summary>Spotlight-style relevance: exact > prefix > word prefix > initials > substring > fuzzy.</summary>
    internal static class Matcher
    {
        public static string StripDiacritics(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            bool ascii = true;
            foreach (char c in s) if (c > 127) { ascii = false; break; }
            if (ascii) return s;
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(s.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string NormalizeQuery(string q)
        {
            if (q == null) return "";
            string s = StripDiacritics(q).ToLowerInvariant().Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        public static string[] Words(string normalizedQuery)
        {
            return normalizedQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Returns 0 (no match) .. 1 (exact). q must come from NormalizeQuery.</summary>
        public static double Score(string q, string[] words, MatchTarget t)
        {
            if (t == null || q.Length == 0) return 0;
            string s = t.Lower;
            if (s.Length == 0) return 0;
            if (s == q) return 1.0;
            if (s.StartsWith(q, StringComparison.Ordinal)) return 0.92 + 0.06 * q.Length / s.Length;

            for (int k = 0; k < t.WordStarts.Length; k++)
            {
                int ws = t.WordStarts[k];
                if (ws == 0) continue; // covered by the prefix test above
                if (ws + q.Length <= s.Length && string.CompareOrdinal(s, ws, q, 0, q.Length) == 0)
                    return 0.84 + 0.06 * q.Length / s.Length - Math.Min(0.04, 0.01 * k);
            }

            if (words.Length > 1)
            {
                double m = MultiWord(t, words);
                if (m > 0) return m;
            }

            if (q.Length >= 2 && t.Initials.Length >= 2)
            {
                if (t.Initials == q) return 0.83;
                if (t.Initials.StartsWith(q, StringComparison.Ordinal)) return 0.76;
            }

            int idx = s.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0) return q.Length >= 3 ? 0.62 + 0.1 * q.Length / s.Length : 0.42;

            if (q.Length >= 3 && words.Length == 1) return Fuzzy(t, q);
            return 0;
        }

        /// <summary>Best score over several targets; secondary targets (aliases, keywords) are discounted.</summary>
        public static double Best(string q, string[] words, MatchTarget primary, IList<MatchTarget> others, double otherWeight)
        {
            double best = Score(q, words, primary);
            if (best >= 1.0 || others == null) return best;
            for (int i = 0; i < others.Count; i++)
            {
                double s = Score(q, words, others[i]) * otherWeight;
                if (s > best) best = s;
            }
            return best;
        }

        static double MultiWord(MatchTarget t, string[] words)
        {
            var used = new bool[t.WordStarts.Length];
            int last = -1; bool inOrder = true;
            foreach (string w in words)
            {
                int found = -1;
                for (int k = 0; k < t.WordStarts.Length; k++)
                {
                    if (used[k]) continue;
                    int ws = t.WordStarts[k];
                    if (ws + w.Length <= t.Lower.Length && string.CompareOrdinal(t.Lower, ws, w, 0, w.Length) == 0) { found = k; break; }
                }
                if (found < 0) return 0;
                used[found] = true;
                if (found < last) inOrder = false;
                last = found;
            }
            return inOrder ? 0.8 : 0.74;
        }

        static double Fuzzy(MatchTarget t, string q)
        {
            string s = t.Lower;
            int start = -1;
            foreach (int ws in t.WordStarts) if (s[ws] == q[0]) { start = ws; break; }
            if (start < 0) return 0;
            int prev = start, consecutive = 0, wordHits = 1;
            for (int qi = 1; qi < q.Length; qi++)
            {
                int found = s.IndexOf(q[qi], prev + 1);
                if (found < 0) return 0;
                if (found == prev + 1) consecutive++;
                if (t.IsStart[found]) wordHits++;
                prev = found;
            }
            double spread = (double)(prev - start + 1) / s.Length;
            double quality = (consecutive + wordHits) / (2.0 * q.Length);
            return Math.Max(0.25, 0.3 + 0.25 * quality - 0.05 * spread);
        }
    }

    /// <summary>Learns what you pick for what you type (Spotlight-style), stored in usage.json.</summary>
    internal sealed class Usage
    {
        sealed class Entry { public int Count; public DateTime Last; }

        readonly object gate = new object();
        readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>();
        readonly Dictionary<string, Dictionary<string, int>> queries = new Dictionary<string, Dictionary<string, int>>();
        Timer saveTimer;

        public static string Key(string query)
        {
            string q = Matcher.NormalizeQuery(query);
            return q.Length > 40 ? q.Substring(0, 40) : q;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(Paths.UsageFile)) return;
                object j = Json.Parse(File.ReadAllText(Paths.UsageFile));
                var it = Json.Obj(j, "items");
                if (it != null)
                    foreach (var kv in it)
                    {
                        var e = new Entry { Count = (int)Json.Num(kv.Value, "count", 1) };
                        DateTime last;
                        e.Last = DateTime.TryParse(Json.Str(kv.Value, "last", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) ? last : DateTime.UtcNow;
                        items[kv.Key] = e;
                    }
                var qs = Json.Obj(j, "queries");
                if (qs != null)
                    foreach (var kv in qs)
                    {
                        var m = new Dictionary<string, int>();
                        var inner = kv.Value as Dictionary<string, object>;
                        if (inner == null) continue;
                        foreach (var x in inner) m[x.Key] = (int)Convert.ToDouble(x.Value, CultureInfo.InvariantCulture);
                        queries[kv.Key] = m;
                    }
            }
            catch (Exception ex) { Log.Error("Loading usage", ex); }
        }

        public void Record(string query, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (gate)
            {
                Entry e;
                if (!items.TryGetValue(id, out e)) items[id] = e = new Entry();
                e.Count++; e.Last = DateTime.UtcNow;
                string k = Key(query);
                if (k.Length > 0)
                {
                    Dictionary<string, int> m;
                    if (!queries.TryGetValue(k, out m)) queries[k] = m = new Dictionary<string, int>();
                    int c; m.TryGetValue(id, out c); m[id] = c + 1;
                    if (queries.Count > 600)
                    {
                        foreach (var dead in queries.OrderBy(x => x.Value.Values.Sum()).Take(100).Select(x => x.Key).ToList())
                            queries.Remove(dead);
                    }
                }
            }
            if (saveTimer == null) saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            saveTimer.Change(1500, Timeout.Infinite);
        }

        /// <summary>Per-item boosts for one query: exact query history counts most, then queries this one is a prefix of.</summary>
        public Dictionary<string, double> BoostsFor(string query)
        {
            var result = new Dictionary<string, double>();
            string k = Key(query);
            lock (gate)
            {
                foreach (var kv in items)
                {
                    double age = (DateTime.UtcNow - kv.Value.Last).TotalDays;
                    double decay = Math.Pow(0.5, Math.Max(0, age) / 14.0);
                    result[kv.Key] = Math.Min(0.08, 0.025 * Math.Log(1 + kv.Value.Count, 2)) * (0.4 + 0.6 * decay);
                }
                if (k.Length == 0) return result;
                foreach (var q in queries)
                {
                    bool exact = q.Key == k;
                    if (!exact && !q.Key.StartsWith(k, StringComparison.Ordinal)) continue;
                    foreach (var x in q.Value)
                    {
                        double add = exact ? Math.Min(0.3, 0.12 * x.Value) : Math.Min(0.12, 0.04 * x.Value);
                        double cur; result.TryGetValue(x.Key, out cur);
                        result[x.Key] = cur + add;
                    }
                }
            }
            return result;
        }

        public List<string> MostUsed(int n)
        {
            lock (gate)
            {
                return items.OrderByDescending(kv => kv.Value.Count * Math.Pow(0.5, (DateTime.UtcNow - kv.Value.Last).TotalDays / 14.0))
                            .Take(n).Select(kv => kv.Key).ToList();
            }
        }

        public void Save()
        {
            try
            {
                Dictionary<string, object> root;
                lock (gate)
                {
                    var it = new Dictionary<string, object>();
                    foreach (var kv in items)
                        it[kv.Key] = new Dictionary<string, object> { { "count", (double)kv.Value.Count }, { "last", kv.Value.Last.ToString("o") } };
                    var qs = new Dictionary<string, object>();
                    foreach (var kv in queries)
                    {
                        var m = new Dictionary<string, object>();
                        foreach (var x in kv.Value) m[x.Key] = (double)x.Value;
                        qs[kv.Key] = m;
                    }
                    root = new Dictionary<string, object> { { "items", it }, { "queries", qs } };
                }
                File.WriteAllText(Paths.UsageFile, Json.Serialize(root, false));
            }
            catch (Exception ex) { Log.Error("Saving usage", ex); }
        }
    }
}
