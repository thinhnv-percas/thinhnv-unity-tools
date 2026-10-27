using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A small JSON reader for Figma's REST responses (Unity's JsonUtility can't read arbitrary
    /// objects): objects become <c>Dictionary&lt;string, object&gt;</c>, arrays <c>List&lt;object&gt;</c>,
    /// numbers <c>double</c>, plus <c>string</c>, <c>bool</c> and null. Thread-safe (no Unity API).
    /// </summary>
    public static class FigJson
    {
        private const int MaxDepth = 512;

        public static object Parse(string json)
        {
            int pos = 0;
            object value = Value(json, ref pos, 0);
            Skip(json, ref pos);
            if (pos != json.Length)
            {
                throw Error(json, pos, "unexpected text after the value");
            }

            return value;
        }

        // ─── Reading helpers ─────────────────────────────────────────────────────

        public static Dictionary<string, object> Obj(object o, string key)
        {
            return o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            return o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v as List<object> : null;
        }

        public static string Str(object o, string key)
        {
            return o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v as string : null;
        }

        public static double? Num(object o, string key)
        {
            return o is Dictionary<string, object> d && d.TryGetValue(key, out object v) && v is double n ? n : (double?)null;
        }

        public static float Float(object o, string key, float fallback)
        {
            double? n = Num(o, key);
            return n.HasValue && !double.IsNaN(n.Value) && !double.IsInfinity(n.Value) ? (float)n.Value : fallback;
        }

        public static bool? Bool(object o, string key)
        {
            return o is Dictionary<string, object> d && d.TryGetValue(key, out object v) && v is bool b ? b : (bool?)null;
        }

        public static bool Has(object o, string key)
        {
            return o is Dictionary<string, object> d && d.ContainsKey(key);
        }

        // ─── Parser ──────────────────────────────────────────────────────────────

        private static object Value(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error(s, pos, "nested too deeply");
            }

            Skip(s, ref pos);
            if (pos >= s.Length)
            {
                throw Error(s, pos, "unexpected end");
            }

            char c = s[pos];
            switch (c)
            {
                case '{':
                {
                    pos++;
                    var obj = new Dictionary<string, object>();
                    Skip(s, ref pos);
                    if (pos < s.Length && s[pos] == '}')
                    {
                        pos++;
                        return obj;
                    }

                    while (true)
                    {
                        Skip(s, ref pos);
                        if (pos >= s.Length || s[pos] != '"')
                        {
                            throw Error(s, pos, "expected a key");
                        }

                        string key = String(s, ref pos);
                        Skip(s, ref pos);
                        if (pos >= s.Length || s[pos] != ':')
                        {
                            throw Error(s, pos, "expected ':'");
                        }

                        pos++;
                        obj[key] = Value(s, ref pos, depth + 1);
                        Skip(s, ref pos);
                        if (pos < s.Length && s[pos] == ',')
                        {
                            pos++;
                            continue;
                        }

                        if (pos < s.Length && s[pos] == '}')
                        {
                            pos++;
                            return obj;
                        }

                        throw Error(s, pos, "expected ',' or '}'");
                    }
                }
                case '[':
                {
                    pos++;
                    var list = new List<object>();
                    Skip(s, ref pos);
                    if (pos < s.Length && s[pos] == ']')
                    {
                        pos++;
                        return list;
                    }

                    while (true)
                    {
                        list.Add(Value(s, ref pos, depth + 1));
                        Skip(s, ref pos);
                        if (pos < s.Length && s[pos] == ',')
                        {
                            pos++;
                            continue;
                        }

                        if (pos < s.Length && s[pos] == ']')
                        {
                            pos++;
                            return list;
                        }

                        throw Error(s, pos, "expected ',' or ']'");
                    }
                }
                case '"':
                    return String(s, ref pos);
                case 't':
                    Literal(s, ref pos, "true");
                    return true;
                case 'f':
                    Literal(s, ref pos, "false");
                    return false;
                case 'n':
                    Literal(s, ref pos, "null");
                    return null;
                default:
                    return Number(s, ref pos);
            }
        }

        private static void Skip(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\n' || s[pos] == '\r' || s[pos] == '\t'))
            {
                pos++;
            }
        }

        private static void Literal(string s, ref int pos, string word)
        {
            if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0)
            {
                throw Error(s, pos, "unexpected text");
            }

            pos += word.Length;
        }

        private static double Number(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0)
            {
                pos++;
            }

            if (pos == start || !double.TryParse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                throw Error(s, start, "bad number");
            }

            return v;
        }

        private static string String(string s, ref int pos)
        {
            pos++;
            int start = pos;
            // Fast path: no escapes.
            while (pos < s.Length && s[pos] != '"' && s[pos] != '\\')
            {
                pos++;
            }

            if (pos < s.Length && s[pos] == '"')
            {
                return s.Substring(start, pos++ - start);
            }

            var sb = new StringBuilder(s, start, pos - start, pos - start + 16);
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (pos >= s.Length)
                {
                    break;
                }

                char e = s[pos++];
                switch (e)
                {
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'u':
                        if (pos + 4 > s.Length)
                        {
                            throw Error(s, pos, "bad escape");
                        }

                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default:
                        sb.Append(e);
                        break;
                }
            }

            throw Error(s, pos, "unterminated string");
        }

        private static FormatException Error(string s, int pos, string what)
        {
            return new FormatException($"JSON: {what} at character {pos}");
        }
    }
}
