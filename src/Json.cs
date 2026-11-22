using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumen
{
    /// <summary>
    /// Small JSON reader/writer. Accepts JSONC: // and /* */ comments and trailing commas.
    /// Objects -> Dictionary&lt;string, object&gt; (case-insensitive keys), arrays -> List&lt;object&gt;,
    /// numbers -> double, plus string / bool / null.
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            var r = new Reader(text);
            r.Ws();
            object v = r.Value();
            r.Ws();
            if (r.Pos < text.Length) throw r.Error("Unexpected text after the end of the JSON value");
            return v;
        }

        sealed class Reader
        {
            readonly string s; public int Pos;
            public Reader(string text) { s = text ?? ""; }

            public FormatException Error(string msg)
            {
                int line = 1, col = 1;
                for (int i = 0; i < Pos && i < s.Length; i++) { if (s[i] == '\n') { line++; col = 1; } else col++; }
                return new FormatException(msg + " (line " + line + ", column " + col + ")");
            }

            public void Ws()
            {
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { Pos++; continue; }
                    if (c == '/' && Pos + 1 < s.Length && s[Pos + 1] == '/')
                    {
                        while (Pos < s.Length && s[Pos] != '\n') Pos++;
                        continue;
                    }
                    if (c == '/' && Pos + 1 < s.Length && s[Pos + 1] == '*')
                    {
                        int end = s.IndexOf("*/", Pos + 2, StringComparison.Ordinal);
                        Pos = end < 0 ? s.Length : end + 2;
                        continue;
                    }
                    break;
                }
            }

            public object Value()
            {
                if (Pos >= s.Length) throw Error("Unexpected end of JSON");
                char c = s[Pos];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                if (Lit("true")) return true;
                if (Lit("false")) return false;
                if (Lit("null")) return null;
                throw Error("Unexpected character '" + c + "'");
            }

            bool Lit(string w)
            {
                if (string.CompareOrdinal(s, Pos, w, 0, w.Length) == 0) { Pos += w.Length; return true; }
                return false;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Pos++; Ws();
                while (true)
                {
                    if (Pos >= s.Length) throw Error("Unclosed object");
                    if (s[Pos] == '}') { Pos++; return d; }
                    if (s[Pos] != '"') throw Error("Expected a \"quoted\" property name");
                    string key = Str(); Ws();
                    if (Pos >= s.Length || s[Pos] != ':') throw Error("Expected ':' after \"" + key + "\"");
                    Pos++; Ws();
                    d[key] = Value(); Ws();
                    if (Pos < s.Length && s[Pos] == ',') { Pos++; Ws(); continue; }
                    if (Pos < s.Length && s[Pos] == '}') { Pos++; return d; }
                    throw Error("Expected ',' or '}' (missing comma?)");
                }
            }

            List<object> Arr()
            {
                var l = new List<object>();
                Pos++; Ws();
                while (true)
                {
                    if (Pos >= s.Length) throw Error("Unclosed array");
                    if (s[Pos] == ']') { Pos++; return l; }
                    l.Add(Value()); Ws();
                    if (Pos < s.Length && s[Pos] == ',') { Pos++; Ws(); continue; }
                    if (Pos < s.Length && s[Pos] == ']') { Pos++; return l; }
                    throw Error("Expected ',' or ']' (missing comma?)");
                }
            }

            string Str()
            {
                var sb = new StringBuilder();
                Pos++;
                while (true)
                {
                    if (Pos >= s.Length) throw Error("Unclosed string");
                    char c = s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= s.Length) throw Error("Bad escape");
                    char e = s[Pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (Pos + 4 > s.Length) throw Error("Bad \\u escape");
                            sb.Append((char)int.Parse(s.Substring(Pos, 4), NumberStyles.HexNumber));
                            Pos += 4; break;
                        default: sb.Append(e); break; // \" \\ \/
                    }
                }
            }

            object Num()
            {
                int start = Pos;
                if (s[Pos] == '-') Pos++;
                while (Pos < s.Length && "0123456789.eE+-".IndexOf(s[Pos]) >= 0) Pos++;
                double v;
                if (!double.TryParse(s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw Error("Bad number");
                return v;
            }
        }

        // ------------------------------------------------------------ writing
        public static string Serialize(object o, bool indent)
        {
            var sb = new StringBuilder();
            Write(sb, o, indent, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, bool indent, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { Quote(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double || o is float || o is int || o is long || o is decimal)
            {
                sb.Append(Convert.ToDouble(o).ToString("R", CultureInfo.InvariantCulture)); return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indent) { sb.Append('\n').Append(' ', (depth + 1) * 2); }
                    Quote(sb, Convert.ToString(kv.Key)); sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, depth + 1);
                }
                if (indent && !first) sb.Append('\n').Append(' ', depth * 2);
                sb.Append('}'); return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('['); bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indent) { sb.Append('\n').Append(' ', (depth + 1) * 2); }
                    Write(sb, item, indent, depth + 1);
                }
                if (indent && !first) sb.Append('\n').Append(' ', depth * 2);
                sb.Append(']'); return;
            }
            Quote(sb, o.ToString());
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ typed access helpers
        public static Dictionary<string, object> Obj(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static string Str(object o, string key, string def)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(object o, string key, bool def)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            var s = v as string;
            if (s != null) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            return def;
        }

        public static double Num(object o, string key, double def)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            double r;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        /// <summary>Reads a value that may be a single string or an array of strings.</summary>
        public static List<string> Strings(object o, string key)
        {
            var result = new List<string>();
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return result;
            var l = v as List<object>;
            if (l == null) { result.Add(Convert.ToString(v, CultureInfo.InvariantCulture)); return result; }
            foreach (var x in l) if (x != null) result.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            return result;
        }
    }
}
