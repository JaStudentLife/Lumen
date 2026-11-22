using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lumen
{
    /// <summary>Inline calculator ("2^10/4", "sqrt(2)*pi", "15% of 80", "0xff + 1") and unit conversion ("5 km to mi").</summary>
    internal static class Calculator
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool TryEvaluate(string input, out double value)
        {
            value = 0;
            string s = (input ?? "").Trim();
            if (s.StartsWith("=")) s = s.Substring(1).Trim();
            if (s.Length == 0 || !LooksLikeMath(input)) return false;
            try
            {
                value = new ExprParser(s).ParseAll();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        static bool LooksLikeMath(string input)
        {
            string s = input.Trim();
            if (s.StartsWith("=")) return true;
            if (!s.Any(char.IsDigit) && !Regex.IsMatch(s, @"\b(pi|e)\b", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(s, @"^[-+]?[\d.]+$")) return false;                       // a bare number isn't a calculation
            if (Regex.IsMatch(s, @"^\d{1,4}([-/.])\d{1,2}\1\d{1,4}$")) return false;     // looks like a date
            return Regex.IsMatch(s, @"[-+*/^%!()×÷]|\b(sqrt|cbrt|abs|sin|cos|tan|asin|acos|atan|ln|log|log2|exp|floor|ceil|round|min|max|pow|mod|of)\b|0x|0b", RegexOptions.IgnoreCase);
        }

        public static string Format(double v)
        {
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return v.ToString("#,##0", Inv);
            if (Math.Abs(v) >= 1e15 || Math.Abs(v) < 1e-6) return v.ToString("G12", Inv);
            return Math.Round(v, 10).ToString("#,##0.##########", Inv);
        }

        public static string Plain(double v)
        {
            if (Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-6)) return v.ToString("G15", Inv);
            return Math.Round(v, 10).ToString("0.##########", Inv);
        }

        sealed class ExprParser
        {
            readonly string s; int p;
            public ExprParser(string text) { s = text.Replace("**", "^"); }

            public double ParseAll()
            {
                double v = Expr();
                Ws();
                if (p < s.Length) throw new FormatException("Unexpected '" + s[p] + "'");
                return v;
            }

            void Ws() { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }
            bool Eat(char c) { Ws(); if (p < s.Length && s[p] == c) { p++; return true; } return false; }

            bool EatWord(string w)
            {
                Ws();
                if (p + w.Length <= s.Length && string.Compare(s, p, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (p + w.Length == s.Length || !char.IsLetter(s[p + w.Length])))
                { p += w.Length; return true; }
                return false;
            }

            double Expr()
            {
                double v = Term();
                while (true)
                {
                    if (Eat('+')) v += Term();
                    else if (Eat('-') || Eat('−')) v -= Term();
                    else return v;
                }
            }

            double Term()
            {
                double v = Unary();
                while (true)
                {
                    if (Eat('*') || Eat('×')) v *= Unary();
                    else if (Eat('/') || Eat('÷')) v /= Unary();
                    else if (EatWord("mod")) v = Mod(v, Unary());
                    else if (EatWord("of")) v *= Unary();               // "15% of 80"
                    else if (PeekModulo()) { p++; v = Mod(v, Unary()); }
                    else if (StartsOperand()) v *= Unary();            // implicit: 2pi, 3(4+1)
                    else return v;
                }
            }

            static double Mod(double a, double b) { return a - b * Math.Floor(a / b); }

            bool PeekModulo()
            {
                Ws();
                if (p >= s.Length || s[p] != '%') return false;
                int q = p + 1;
                while (q < s.Length && char.IsWhiteSpace(s[q])) q++;
                if (q >= s.Length) return false;
                if (string.Compare(s, q, "of", 0, 2, StringComparison.OrdinalIgnoreCase) == 0) return false;
                char c = s[q];
                return char.IsDigit(c) || c == '.' || c == '(' || char.IsLetter(c);
            }

            bool StartsOperand()
            {
                Ws();
                if (p >= s.Length) return false;
                char c = s[p];
                if (c == '(') return true;
                if (char.IsLetter(c) || c == 'π')
                {
                    foreach (var w in new[] { "mod", "of", "to", "in" })
                        if (p + w.Length <= s.Length && string.Compare(s, p, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                            && (p + w.Length == s.Length || !char.IsLetter(s[p + w.Length]))) return false;
                    return true;
                }
                return false;
            }

            double Unary()
            {
                if (Eat('-') || Eat('−')) return -Unary();
                if (Eat('+')) return Unary();
                return Power();
            }

            double Power()
            {
                double b = Postfix();
                if (Eat('^')) return Math.Pow(b, Unary());
                return b;
            }

            double Postfix()
            {
                double v = Primary();
                while (true)
                {
                    Ws();
                    if (p < s.Length && s[p] == '!') { p++; v = Factorial(v); }
                    else if (p < s.Length && s[p] == '%' && !PeekModulo()) { p++; v /= 100.0; }
                    else return v;
                }
            }

            static double Factorial(double n)
            {
                if (n < 0 || n != Math.Floor(n) || n > 170) throw new FormatException("factorial");
                double r = 1; for (int i = 2; i <= (int)n; i++) r *= i; return r;
            }

            double Primary()
            {
                Ws();
                if (p >= s.Length) throw new FormatException("end");
                char c = s[p];
                if (c == '(')
                {
                    p++;
                    double v = Expr();
                    if (!Eat(')')) { Ws(); if (p < s.Length) throw new FormatException(")"); } // tolerate a missing ) while typing
                    return v;
                }
                if (char.IsDigit(c) || c == '.') return Number();
                if (c == 'π') { p++; return Math.PI; }
                if (char.IsLetter(c))
                {
                    int start = p;
                    while (p < s.Length && (char.IsLetterOrDigit(s[p]))) p++;
                    string id = s.Substring(start, p - start).ToLowerInvariant();
                    switch (id)
                    {
                        case "pi": return Math.PI;
                        case "e": return Math.E;
                        case "tau": return 2 * Math.PI;
                    }
                    var args = new List<double>();
                    if (Eat('('))
                    {
                        Ws();
                        if (!Eat(')'))
                        {
                            args.Add(Expr());
                            while (Eat(',') || Eat(';')) args.Add(Expr());
                            if (!Eat(')')) { Ws(); if (p < s.Length) throw new FormatException(")"); }
                        }
                    }
                    else args.Add(Power());
                    return Call(id, args);
                }
                throw new FormatException("Unexpected '" + c + "'");
            }

            static double Call(string f, List<double> a)
            {
                Func<double> x = () => { if (a.Count < 1) throw new FormatException(f); return a[0]; };
                switch (f)
                {
                    case "sqrt": return Math.Sqrt(x());
                    case "cbrt": return Math.Pow(x(), 1.0 / 3);
                    case "abs": return Math.Abs(x());
                    case "sin": return Math.Sin(x());
                    case "cos": return Math.Cos(x());
                    case "tan": return Math.Tan(x());
                    case "asin": return Math.Asin(x());
                    case "acos": return Math.Acos(x());
                    case "atan": return Math.Atan(x());
                    case "ln": return Math.Log(x());
                    case "log": return a.Count > 1 ? Math.Log(a[0], a[1]) : Math.Log10(x());
                    case "log2": return Math.Log(x(), 2);
                    case "exp": return Math.Exp(x());
                    case "floor": return Math.Floor(x());
                    case "ceil": return Math.Ceiling(x());
                    case "round": return a.Count > 1 ? Math.Round(a[0], (int)a[1], MidpointRounding.AwayFromZero) : Math.Round(x(), MidpointRounding.AwayFromZero);
                    case "min": if (a.Count == 0) throw new FormatException(f); return a.Min();
                    case "max": if (a.Count == 0) throw new FormatException(f); return a.Max();
                    case "pow": if (a.Count != 2) throw new FormatException(f); return Math.Pow(a[0], a[1]);
                    default: throw new FormatException("Unknown function " + f);
                }
            }

            double Number()
            {
                int start = p;
                if (s[p] == '0' && p + 1 < s.Length && (s[p + 1] == 'x' || s[p + 1] == 'X'))
                {
                    p += 2; int h = p;
                    while (p < s.Length && Uri.IsHexDigit(s[p])) p++;
                    return (double)long.Parse(s.Substring(h, p - h), NumberStyles.HexNumber, Inv);
                }
                if (s[p] == '0' && p + 1 < s.Length && (s[p + 1] == 'b' || s[p + 1] == 'B') && p + 2 < s.Length && (s[p + 2] == '0' || s[p + 2] == '1'))
                {
                    p += 2; int b = p;
                    while (p < s.Length && (s[p] == '0' || s[p] == '1')) p++;
                    return (double)Convert.ToInt64(s.Substring(b, p - b), 2);
                }
                while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
                if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
                {
                    int q = p + 1;
                    if (q < s.Length && (s[q] == '+' || s[q] == '-')) q++;
                    if (q < s.Length && char.IsDigit(s[q])) { p = q; while (p < s.Length && char.IsDigit(s[p])) p++; }
                }
                return double.Parse(s.Substring(start, p - start), NumberStyles.Float, Inv);
            }
        }

        // ------------------------------------------------------------------ unit conversion
        sealed class Unit { public string Kind; public double Factor; public string Symbol; }

        static readonly Dictionary<string, Unit> units = BuildUnits();
        static readonly Regex convRx = new Regex(
            @"^\s*(?<n>-?(\d+(\.\d+)?|\.\d+))\s*(?<from>[a-zµ°""'/²³][a-zµ°""'/²³ .]*?)\s+(to|in|as|into|->|=)\s+(?<to>[a-zµ°""'/²³][a-zµ°""'/²³ .]*?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static Dictionary<string, Unit> BuildUnits()
        {
            var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
            Action<string, double, string, string> add = (kind, factor, symbol, names) =>
            {
                var u = new Unit { Kind = kind, Factor = factor, Symbol = symbol };
                foreach (var n in names.Split('|')) d[n] = u;
            };
            add("length", 0.001, "mm", "mm|millimeter|millimeters|millimetre|millimetres");
            add("length", 0.01, "cm", "cm|centimeter|centimeters|centimetre|centimetres");
            add("length", 1, "m", "m|meter|meters|metre|metres");
            add("length", 1000, "km", "km|kilometer|kilometers|kilometre|kilometres");
            add("length", 0.0254, "in", "in|inch|inches|\"");
            add("length", 0.3048, "ft", "ft|foot|feet|'");
            add("length", 0.9144, "yd", "yd|yard|yards");
            add("length", 1609.344, "mi", "mi|mile|miles");
            add("length", 1852, "nmi", "nmi|nautical mile|nautical miles");
            add("mass", 0.000001, "mg", "mg|milligram|milligrams");
            add("mass", 0.001, "g", "g|gram|grams");
            add("mass", 1, "kg", "kg|kilo|kilos|kilogram|kilograms");
            add("mass", 1000, "t", "t|tonne|tonnes|metric ton|metric tons");
            add("mass", 0.028349523125, "oz", "oz|ounce|ounces");
            add("mass", 0.45359237, "lb", "lb|lbs|pound|pounds");
            add("mass", 6.35029318, "st", "st|stone|stones");
            add("volume", 0.001, "ml", "ml|milliliter|milliliters|millilitre|millilitres");
            add("volume", 1, "L", "l|liter|liters|litre|litres");
            add("volume", 3.785411784, "gal", "gal|gallon|gallons");
            add("volume", 0.946352946, "qt", "qt|quart|quarts");
            add("volume", 0.473176473, "pt", "pt|pint|pints");
            add("volume", 0.2365882365, "cup", "cup|cups");
            add("volume", 0.0295735295625, "fl oz", "floz|fl oz|fluid ounce|fluid ounces");
            add("volume", 0.01478676478125, "tbsp", "tbsp|tablespoon|tablespoons");
            add("volume", 0.00492892159375, "tsp", "tsp|teaspoon|teaspoons");
            add("data", 0.125, "bit", "bit|bits");
            add("data", 1, "B", "b|byte|bytes");
            add("data", 1024, "KB", "kb|kib|kilobyte|kilobytes");
            add("data", 1048576, "MB", "mb|mib|megabyte|megabytes");
            add("data", 1073741824, "GB", "gb|gib|gigabyte|gigabytes");
            add("data", 1099511627776, "TB", "tb|tib|terabyte|terabytes");
            add("time", 0.001, "ms", "ms|millisecond|milliseconds");
            add("time", 1, "s", "s|sec|secs|second|seconds");
            add("time", 60, "min", "min|mins|minute|minutes");
            add("time", 3600, "h", "h|hr|hrs|hour|hours");
            add("time", 86400, "days", "d|day|days");
            add("time", 604800, "weeks", "wk|week|weeks");
            add("time", 2629746, "months", "month|months");
            add("time", 31556952, "years", "y|yr|yrs|year|years");
            add("speed", 1, "m/s", "m/s|mps");
            add("speed", 1 / 3.6, "km/h", "km/h|kph|kmh|kmph");
            add("speed", 0.44704, "mph", "mph|mi/h");
            add("speed", 0.514444, "kn", "kn|kt|knot|knots");
            add("area", 1, "m²", "m2|m²|sqm|sq m|square meter|square meters");
            add("area", 1000000, "km²", "km2|km²|sqkm|sq km");
            add("area", 0.09290304, "ft²", "ft2|ft²|sqft|sq ft|square foot|square feet");
            add("area", 4046.8564224, "acres", "acre|acres|ac");
            add("area", 10000, "ha", "ha|hectare|hectares");
            add("temp", 1, "°C", "c|°c|celsius|centigrade");
            add("temp", 1, "°F", "f|°f|fahrenheit");
            add("temp", 1, "K", "k|kelvin");
            return d;
        }

        public static bool TryConvert(string input, out string display, out string plain, out string expression)
        {
            display = plain = expression = null;
            var m = convRx.Match(input ?? "");
            if (!m.Success) return false;
            Unit from, to;
            if (!units.TryGetValue(m.Groups["from"].Value.Trim(), out from) || !units.TryGetValue(m.Groups["to"].Value.Trim(), out to)) return false;
            if (from.Kind != to.Kind) return false;
            double n = double.Parse(m.Groups["n"].Value, Inv), r;
            if (from.Kind == "temp")
            {
                double c = from.Symbol == "°C" ? n : from.Symbol == "°F" ? (n - 32) * 5 / 9 : n - 273.15;
                r = to.Symbol == "°C" ? c : to.Symbol == "°F" ? c * 9 / 5 + 32 : c + 273.15;
            }
            else r = n * from.Factor / to.Factor;
            r = Math.Round(r, Math.Abs(r) < 1 ? 8 : 4);
            plain = Plain(r);
            display = Format(r) + " " + to.Symbol;
            expression = Format(n) + " " + from.Symbol + " =";
            return true;
        }
    }
}
