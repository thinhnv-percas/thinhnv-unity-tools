using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A generic decoder and encoder for the kiwi binary schema format, the format of the two chunks
    /// inside <c>canvas.fig</c>. Every file carries its own schema, so the message is decoded into a
    /// <see cref="KiwiValue"/> tree keyed by the schema's fields and encoded back with the same
    /// schema — fields this tool knows nothing about survive a round trip untouched.
    ///
    /// Ported from figcraft's <c>crates/format/src/fig/kiwi.rs</c> (MIT / Apache-2.0).
    /// </summary>
    public enum KiwiDefKind
    {
        Enum = 0,
        Struct = 1,
        Message = 2,
    }

    /// <summary>The built-in types, in their schema order (encoded as <c>~index</c>), plus <see cref="Def"/>.</summary>
    public enum KiwiType
    {
        Bool = 0,
        Byte = 1,
        Int = 2,
        Uint = 3,
        Float = 4,
        String = 5,
        Int64 = 6,
        Uint64 = 7,
        Def = 8,
    }

    public sealed class KiwiField
    {
        public string Name;
        public KiwiType Type;

        /// <summary>The referenced definition when <see cref="Type"/> is <see cref="KiwiType.Def"/>.</summary>
        public KiwiDef TypeDef;

        public bool IsArray;

        /// <summary>The field id (messages) or the value (enums).</summary>
        public uint Value;

        public string TypeName
        {
            get
            {
                string baseName = Type == KiwiType.Def ? TypeDef?.Name ?? "?" : KiwiSchema.BuiltinNames[(int)Type];
                return IsArray ? baseName + "[]" : baseName;
            }
        }
    }

    public sealed class KiwiDef
    {
        public string Name;
        public KiwiDefKind Kind;
        public int Index;
        public readonly List<KiwiField> Fields = new();

        private KiwiField[] byId;
        private Dictionary<string, KiwiField> byName;
        private Dictionary<uint, KiwiField> enumByValue;

        internal void BuildLookups()
        {
            byName = new Dictionary<string, KiwiField>(Fields.Count);
            foreach (KiwiField f in Fields)
            {
                byName[f.Name] = f;
            }

            if (Kind == KiwiDefKind.Enum)
            {
                enumByValue = new Dictionary<uint, KiwiField>(Fields.Count);
                foreach (KiwiField f in Fields)
                {
                    enumByValue[f.Value] = f;
                }

                return;
            }

            uint max = 0;
            foreach (KiwiField f in Fields)
            {
                max = Math.Max(max, f.Value);
            }

            // Field ids are small in practice; fall back to the dictionary for sparse ones.
            if (max < 4096)
            {
                byId = new KiwiField[max + 1];
                foreach (KiwiField f in Fields)
                {
                    byId[f.Value] = f;
                }
            }
        }

        public KiwiField FieldById(uint id)
        {
            if (byId != null)
            {
                return id < byId.Length ? byId[id] : null;
            }

            foreach (KiwiField f in Fields)
            {
                if (f.Value == id)
                {
                    return f;
                }
            }

            return null;
        }

        public KiwiField FieldByName(string name)
        {
            return name != null && byName.TryGetValue(name, out KiwiField f) ? f : null;
        }

        /// <summary>The name of enum value <paramref name="value"/>, or null when the enum lacks it.</summary>
        public string EnumName(uint value)
        {
            return enumByValue != null && enumByValue.TryGetValue(value, out KiwiField f) ? f.Name : null;
        }
    }

    public sealed class KiwiSchema
    {
        public static readonly string[] BuiltinNames = { "bool", "byte", "int", "uint", "float", "string", "int64", "uint64" };

        private const int MaxDefs = 10000;
        private const int MaxFields = 10000;
        private const int MaxDepth = 64;

        public readonly List<KiwiDef> Defs = new();
        private readonly Dictionary<string, KiwiDef> byName = new();

        public KiwiDef Find(string name)
        {
            return byName.TryGetValue(name, out KiwiDef d) ? d : null;
        }

        // ─── Schema ──────────────────────────────────────────────────────────────

        public static KiwiSchema Decode(byte[] data)
        {
            var r = new KiwiReader(data);
            uint count = r.VarUint();
            if (count > MaxDefs || count > r.Left)
            {
                throw new FormatException("kiwi schema: too many definitions");
            }

            var schema = new KiwiSchema();
            var rawTypes = new List<int>();
            for (int i = 0; i < count; i++)
            {
                var def = new KiwiDef { Name = r.String(), Index = i };
                int kind = r.Byte();
                if (kind > 2)
                {
                    throw new FormatException($"kiwi schema: unknown definition kind {kind}");
                }

                def.Kind = (KiwiDefKind)kind;
                uint fieldCount = r.VarUint();
                if (fieldCount > MaxFields || fieldCount > r.Left)
                {
                    throw new FormatException("kiwi schema: too many fields");
                }

                for (int f = 0; f < fieldCount; f++)
                {
                    var field = new KiwiField { Name = r.String() };
                    rawTypes.Add(r.VarInt());
                    field.IsArray = (r.Byte() & 1) != 0;
                    field.Value = r.VarUint();
                    def.Fields.Add(field);
                }

                schema.Defs.Add(def);
            }

            int t = 0;
            foreach (KiwiDef def in schema.Defs)
            {
                foreach (KiwiField field in def.Fields)
                {
                    int raw = rawTypes[t++];
                    if (def.Kind == KiwiDefKind.Enum)
                    {
                        continue;
                    }

                    if (raw < 0)
                    {
                        int builtin = ~raw;
                        if (builtin >= BuiltinNames.Length)
                        {
                            throw new FormatException($"kiwi schema: unknown type {raw}");
                        }

                        field.Type = (KiwiType)builtin;
                    }
                    else
                    {
                        if (raw >= schema.Defs.Count)
                        {
                            throw new FormatException($"kiwi schema: field {field.Name} names missing definition {raw}");
                        }

                        field.Type = KiwiType.Def;
                        field.TypeDef = schema.Defs[raw];
                    }
                }
            }

            foreach (KiwiDef def in schema.Defs)
            {
                def.BuildLookups();
                schema.byName[def.Name] = def;
            }

            return schema;
        }

        /// <summary>
        /// Parse the text form of a schema, one definition per line: <c>enum Name A=0 B=1</c>,
        /// <c>struct Name type name, type name</c> (fields numbered in order) or
        /// <c>message Name type name=id, …</c>; <c>type[]</c> is an array.
        /// </summary>
        public static KiwiSchema Parse(string text)
        {
            var schema = new KiwiSchema();
            var pending = new List<KeyValuePair<KiwiField, string>>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                int space = line.IndexOf(' ');
                int nameEnd = line.IndexOf(' ', space + 1);
                string kind = line.Substring(0, space);
                string name = nameEnd < 0 ? line.Substring(space + 1) : line.Substring(space + 1, nameEnd - space - 1);
                string rest = nameEnd < 0 ? "" : line.Substring(nameEnd + 1).Trim();
                if (kind != "enum" && kind != "struct" && kind != "message")
                {
                    throw new FormatException("kiwi schema text: unknown definition kind " + kind);
                }

                var def = new KiwiDef
                {
                    Name = name,
                    Index = schema.Defs.Count,
                    Kind = kind == "enum" ? KiwiDefKind.Enum : kind == "struct" ? KiwiDefKind.Struct : KiwiDefKind.Message,
                };

                if (def.Kind == KiwiDefKind.Enum)
                {
                    foreach (string item in rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = item.IndexOf('=');
                        def.Fields.Add(new KiwiField { Name = item.Substring(0, eq), Value = uint.Parse(item.Substring(eq + 1), CultureInfo.InvariantCulture) });
                    }
                }
                else if (rest.Length > 0)
                {
                    uint next = 1;
                    foreach (string item in rest.Split(','))
                    {
                        string[] parts = item.Trim().Split(' ');
                        if (parts.Length != 2)
                        {
                            throw new FormatException($"kiwi schema text: bad field '{item}' in {name}");
                        }

                        string fieldName = parts[1];
                        uint value = next++;
                        int eq = fieldName.IndexOf('=');
                        if (eq >= 0)
                        {
                            value = uint.Parse(fieldName.Substring(eq + 1), CultureInfo.InvariantCulture);
                            fieldName = fieldName.Substring(0, eq);
                        }

                        string type = parts[0];
                        var field = new KiwiField { Name = fieldName, Value = value, IsArray = type.EndsWith("[]", StringComparison.Ordinal) };
                        pending.Add(new KeyValuePair<KiwiField, string>(field, field.IsArray ? type.Substring(0, type.Length - 2) : type));
                        def.Fields.Add(field);
                    }
                }

                schema.Defs.Add(def);
                schema.byName[def.Name] = def;
            }

            foreach (KeyValuePair<KiwiField, string> p in pending)
            {
                int builtin = Array.IndexOf(BuiltinNames, p.Value);
                if (builtin >= 0)
                {
                    p.Key.Type = (KiwiType)builtin;
                }
                else
                {
                    p.Key.Type = KiwiType.Def;
                    p.Key.TypeDef = schema.Find(p.Value);
                    if (p.Key.TypeDef == null)
                    {
                        throw new FormatException("kiwi schema text: unknown type " + p.Value);
                    }
                }
            }

            foreach (KiwiDef def in schema.Defs)
            {
                def.BuildLookups();
            }

            return schema;
        }

        public byte[] Encode()
        {
            var w = new KiwiWriter(4096);
            w.VarUint((uint)Defs.Count);
            foreach (KiwiDef def in Defs)
            {
                w.String(def.Name);
                w.Byte((byte)def.Kind);
                w.VarUint((uint)def.Fields.Count);
                foreach (KiwiField f in def.Fields)
                {
                    w.String(f.Name);
                    int type = def.Kind == KiwiDefKind.Enum ? 0 : f.Type == KiwiType.Def ? f.TypeDef.Index : ~(int)f.Type;
                    w.VarInt(type);
                    w.Byte(f.IsArray ? (byte)1 : (byte)0);
                    w.VarUint(f.Value);
                }
            }

            return w.ToArray();
        }

        // ─── Message decoding ────────────────────────────────────────────────────

        public KiwiValue DecodeMessage(KiwiDef root, byte[] data)
        {
            var r = new KiwiReader(data);
            return DecodeDef(root, ref r, 0);
        }

        private KiwiValue DecodeDef(KiwiDef def, ref KiwiReader r, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("kiwi message: nested too deeply");
            }

            switch (def.Kind)
            {
                case KiwiDefKind.Enum:
                    return KiwiValue.FromEnum(def, r.VarUint());
                case KiwiDefKind.Struct:
                {
                    var members = new List<KiwiMember>(def.Fields.Count);
                    foreach (KiwiField f in def.Fields)
                    {
                        members.Add(new KiwiMember(f, DecodeField(f, ref r, depth)));
                    }

                    return KiwiValue.FromObject(def, members);
                }
                default:
                {
                    var members = new List<KiwiMember>();
                    while (true)
                    {
                        uint id = r.VarUint();
                        if (id == 0)
                        {
                            members.TrimExcess();
                            return KiwiValue.FromObject(def, members);
                        }

                        KiwiField f = def.FieldById(id) ?? throw new FormatException($"kiwi message: {def.Name} has no field {id}");
                        members.Add(new KiwiMember(f, DecodeField(f, ref r, depth)));
                    }
                }
            }
        }

        private KiwiValue DecodeField(KiwiField f, ref KiwiReader r, int depth)
        {
            if (!f.IsArray)
            {
                return DecodeValue(f, ref r, depth);
            }

            uint n = r.VarUint();
            if (n > r.Left)
            {
                throw new FormatException("kiwi message: array longer than the data");
            }

            if (f.Type == KiwiType.Byte)
            {
                return KiwiValue.FromBytes(r.Take((int)n));
            }

            var items = new List<KiwiValue>((int)n);
            for (int i = 0; i < n; i++)
            {
                items.Add(DecodeValue(f, ref r, depth));
            }

            return KiwiValue.FromArray(items);
        }

        private KiwiValue DecodeValue(KiwiField f, ref KiwiReader r, int depth)
        {
            switch (f.Type)
            {
                case KiwiType.Bool:
                    return KiwiValue.FromBool(r.Byte() != 0);
                case KiwiType.Byte:
                    return KiwiValue.FromUint(r.Byte());
                case KiwiType.Int:
                    return KiwiValue.FromInt(r.VarInt());
                case KiwiType.Uint:
                    return KiwiValue.FromUint(r.VarUint());
                case KiwiType.Float:
                    return KiwiValue.FromFloat(r.VarFloat());
                case KiwiType.String:
                    return KiwiValue.FromString(r.String());
                case KiwiType.Int64:
                    return KiwiValue.FromInt(r.VarInt64());
                case KiwiType.Uint64:
                    return KiwiValue.FromUint(r.VarUint64());
                default:
                    return DecodeDef(f.TypeDef, ref r, depth + 1);
            }
        }

        // ─── Message encoding ────────────────────────────────────────────────────

        public byte[] EncodeMessage(KiwiDef root, KiwiValue value)
        {
            var w = new KiwiWriter(1 << 20);
            EncodeDef(root, value, w, 0);
            return w.ToArray();
        }

        private void EncodeDef(KiwiDef def, KiwiValue v, KiwiWriter w, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("kiwi encode: nested too deeply");
            }

            switch (def.Kind)
            {
                case KiwiDefKind.Enum:
                    if (v.Kind != KiwiValueKind.Enum)
                    {
                        throw new FormatException($"kiwi encode: {def.Name} needs an enum value");
                    }

                    w.VarUint(v.EnumValue);
                    break;
                case KiwiDefKind.Struct:
                    foreach (KiwiField f in def.Fields)
                    {
                        KiwiValue fv = v.Get(f.Name) ?? throw new FormatException($"kiwi encode: {def.Name} needs field {f.Name}");
                        EncodeField(f, fv, w, depth);
                    }

                    break;
                default:
                    if (v.Kind != KiwiValueKind.Object)
                    {
                        throw new FormatException($"kiwi encode: {def.Name} needs an object");
                    }

                    foreach (KiwiMember m in v.Members)
                    {
                        w.VarUint(m.Field.Value);
                        EncodeField(m.Field, m.Value, w, depth);
                    }

                    w.VarUint(0);
                    break;
            }
        }

        private void EncodeField(KiwiField f, KiwiValue v, KiwiWriter w, int depth)
        {
            if (!f.IsArray)
            {
                EncodeValue(f, v, w, depth);
                return;
            }

            if (f.Type == KiwiType.Byte && v.Kind == KiwiValueKind.Bytes)
            {
                w.VarUint((uint)v.Bytes.Length);
                w.Bytes(v.Bytes);
                return;
            }

            if (v.Kind != KiwiValueKind.Array)
            {
                throw new FormatException($"kiwi encode: {f.Name} needs a list");
            }

            w.VarUint((uint)v.Items.Count);
            foreach (KiwiValue item in v.Items)
            {
                EncodeValue(f, item, w, depth);
            }
        }

        private void EncodeValue(KiwiField f, KiwiValue v, KiwiWriter w, int depth)
        {
            switch (f.Type)
            {
                case KiwiType.Bool:
                    w.Byte(v.AsBool ? (byte)1 : (byte)0);
                    break;
                case KiwiType.Byte:
                    w.Byte((byte)Clamp(v.AsLong, 0, 255));
                    break;
                case KiwiType.Int:
                    w.VarInt((int)Clamp(v.AsLong, int.MinValue, int.MaxValue));
                    break;
                case KiwiType.Uint:
                    w.VarUint((uint)Clamp(v.AsLong, 0, uint.MaxValue));
                    break;
                case KiwiType.Float:
                    w.VarFloat(v.AsFloat);
                    break;
                case KiwiType.String:
                    w.String(v.String ?? "");
                    break;
                case KiwiType.Int64:
                    w.VarInt64(v.AsLong);
                    break;
                case KiwiType.Uint64:
                    w.VarUint64(v.Kind == KiwiValueKind.Uint ? v.Uint : (ulong)Math.Max(0, v.AsLong));
                    break;
                default:
                    EncodeDef(f.TypeDef, v, w, depth + 1);
                    break;
            }
        }

        private static long Clamp(long v, long min, long max)
        {
            return v < min ? min : v > max ? max : v;
        }

        // ─── Defaults ────────────────────────────────────────────────────────────

        /// <summary>A fresh value for one element (or the whole value, for non-arrays) of field <paramref name="f"/>.</summary>
        public static KiwiValue DefaultElement(KiwiField f, int depth = 0)
        {
            switch (f.Type)
            {
                case KiwiType.Bool:
                    return KiwiValue.FromBool(false);
                case KiwiType.Byte:
                case KiwiType.Uint:
                case KiwiType.Uint64:
                    return KiwiValue.FromUint(0);
                case KiwiType.Int:
                case KiwiType.Int64:
                    return KiwiValue.FromInt(0);
                case KiwiType.Float:
                    return KiwiValue.FromFloat(0f);
                case KiwiType.String:
                    return KiwiValue.FromString("");
                default:
                    return DefaultDef(f.TypeDef, depth);
            }
        }

        /// <summary>A fresh value for field <paramref name="f"/> (an empty list for arrays).</summary>
        public static KiwiValue DefaultField(KiwiField f)
        {
            if (!f.IsArray)
            {
                return DefaultElement(f);
            }

            return f.Type == KiwiType.Byte ? KiwiValue.FromBytes(Array.Empty<byte>()) : KiwiValue.FromArray(new List<KiwiValue>());
        }

        private static KiwiValue DefaultDef(KiwiDef def, int depth)
        {
            switch (def.Kind)
            {
                case KiwiDefKind.Enum:
                    return KiwiValue.FromEnum(def, def.Fields.Count > 0 ? def.Fields[0].Value : 0);
                case KiwiDefKind.Struct:
                {
                    if (depth > MaxDepth)
                    {
                        throw new FormatException($"kiwi: {def.Name} nests too deeply for a default value");
                    }

                    var members = new List<KiwiMember>(def.Fields.Count);
                    foreach (KiwiField f in def.Fields)
                    {
                        members.Add(new KiwiMember(f, f.IsArray ? DefaultField(f) : DefaultElement(f, depth + 1)));
                    }

                    return KiwiValue.FromObject(def, members);
                }
                default:
                    return KiwiValue.FromObject(def, new List<KiwiMember>());
            }
        }
    }

    public enum KiwiValueKind : byte
    {
        Bool,
        Int,
        Uint,
        Float,
        String,
        Bytes,
        Enum,
        Array,
        Object,
    }

    public struct KiwiMember
    {
        public KiwiField Field;
        public KiwiValue Value;

        public KiwiMember(KiwiField field, KiwiValue value)
        {
            Field = field;
            Value = value;
        }
    }

    /// <summary>
    /// A decoded value. Kept compact (one number slot, one reference slot) because large design
    /// files decode into millions of them.
    /// </summary>
    public sealed class KiwiValue
    {
        public KiwiValueKind Kind;

        /// <summary>Bool (0/1), Int, Uint (as bits), or the raw value of an Enum.</summary>
        public long Number;

        public float FloatValue;

        /// <summary>string, byte[], List&lt;KiwiValue&gt; or List&lt;KiwiMember&gt;, by <see cref="Kind"/>.</summary>
        public object Ref;

        /// <summary>The definition of an Enum or Object value.</summary>
        public KiwiDef Def;

        public static KiwiValue FromBool(bool b) => new() { Kind = KiwiValueKind.Bool, Number = b ? 1 : 0 };
        public static KiwiValue FromInt(long i) => new() { Kind = KiwiValueKind.Int, Number = i };
        public static KiwiValue FromUint(ulong u) => new() { Kind = KiwiValueKind.Uint, Number = unchecked((long)u) };
        public static KiwiValue FromFloat(float f) => new() { Kind = KiwiValueKind.Float, FloatValue = f };
        public static KiwiValue FromString(string s) => new() { Kind = KiwiValueKind.String, Ref = s };
        public static KiwiValue FromBytes(byte[] b) => new() { Kind = KiwiValueKind.Bytes, Ref = b };
        public static KiwiValue FromEnum(KiwiDef def, uint value) => new() { Kind = KiwiValueKind.Enum, Def = def, Number = value };
        public static KiwiValue FromArray(List<KiwiValue> items) => new() { Kind = KiwiValueKind.Array, Ref = items };
        public static KiwiValue FromObject(KiwiDef def, List<KiwiMember> members) => new() { Kind = KiwiValueKind.Object, Def = def, Ref = members };

        public bool AsBool => Kind == KiwiValueKind.Float ? FloatValue != 0 : Number != 0;

        public long AsLong => Kind == KiwiValueKind.Float ? (long)Math.Round(FloatValue) : Number;

        public ulong Uint => unchecked((ulong)Number);

        public float AsFloat => Kind == KiwiValueKind.Float ? FloatValue : Kind == KiwiValueKind.Uint ? (float)Uint : (float)Number;

        public string String
        {
            get => Kind == KiwiValueKind.Enum ? EnumName : Ref as string;
            set => Ref = value;
        }

        public byte[] Bytes => Ref as byte[];

        public List<KiwiValue> Items => Ref as List<KiwiValue>;

        public List<KiwiMember> Members => Ref as List<KiwiMember>;

        public uint EnumValue => unchecked((uint)Number);

        public string EnumName => Def?.EnumName(EnumValue) ?? EnumValue.ToString(CultureInfo.InvariantCulture);

        /// <summary>Field <paramref name="name"/> of an object, or null.</summary>
        public KiwiValue Get(string name)
        {
            List<KiwiMember> members = Members;
            if (members == null)
            {
                return null;
            }

            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Field.Name == name)
                {
                    return members[i].Value;
                }
            }

            return null;
        }

        public int IndexOf(string name)
        {
            List<KiwiMember> members = Members;
            if (members == null)
            {
                return -1;
            }

            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Field.Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Set (or add) message field <paramref name="name"/>; returns false when the schema has no such field.</summary>
        public bool Set(string name, KiwiValue value)
        {
            if (Kind != KiwiValueKind.Object)
            {
                return false;
            }

            int index = IndexOf(name);
            if (index >= 0)
            {
                Members[index] = new KiwiMember(Members[index].Field, value);
                return true;
            }

            KiwiField field = Def.FieldByName(name);
            if (field == null)
            {
                return false;
            }

            Members.Add(new KiwiMember(field, value));
            return true;
        }

        public bool Remove(string name)
        {
            int index = IndexOf(name);
            if (index < 0 || Def.Kind != KiwiDefKind.Message)
            {
                return false;
            }

            Members.RemoveAt(index);
            return true;
        }

        /// <summary>Follow a dotted path of field names (e.g. <c>textData.characters</c>).</summary>
        public KiwiValue Path(string path)
        {
            KiwiValue v = this;
            foreach (string part in path.Split('.'))
            {
                v = v?.Get(part);
            }

            return v;
        }

        public KiwiValue Clone()
        {
            var copy = new KiwiValue { Kind = Kind, Number = Number, FloatValue = FloatValue, Def = Def };
            switch (Kind)
            {
                case KiwiValueKind.Bytes:
                    copy.Ref = (byte[])Bytes.Clone();
                    break;
                case KiwiValueKind.Array:
                {
                    var items = new List<KiwiValue>(Items.Count);
                    foreach (KiwiValue item in Items)
                    {
                        items.Add(item.Clone());
                    }

                    copy.Ref = items;
                    break;
                }
                case KiwiValueKind.Object:
                {
                    var members = new List<KiwiMember>(Members.Count);
                    foreach (KiwiMember m in Members)
                    {
                        members.Add(new KiwiMember(m.Field, m.Value.Clone()));
                    }

                    copy.Ref = members;
                    break;
                }
                default:
                    copy.Ref = Ref;
                    break;
            }

            return copy;
        }

        /// <summary>A one-line summary for the inspector and the tree.</summary>
        public string Summary(int maxLength = 80)
        {
            string s;
            switch (Kind)
            {
                case KiwiValueKind.Bool:
                    s = AsBool ? "true" : "false";
                    break;
                case KiwiValueKind.Int:
                    s = Number.ToString(CultureInfo.InvariantCulture);
                    break;
                case KiwiValueKind.Uint:
                    s = Uint.ToString(CultureInfo.InvariantCulture);
                    break;
                case KiwiValueKind.Float:
                    s = FloatValue.ToString("R", CultureInfo.InvariantCulture);
                    break;
                case KiwiValueKind.String:
                    s = "\"" + (String ?? "").Replace("\n", "\\n") + "\"";
                    break;
                case KiwiValueKind.Bytes:
                    s = Bytes.Length <= 32 ? FigUtil.Hex(Bytes) : $"<{Bytes.Length} bytes>";
                    break;
                case KiwiValueKind.Enum:
                    s = EnumName;
                    break;
                case KiwiValueKind.Array:
                    s = $"[{Items.Count}]";
                    break;
                default:
                    s = SummarizeObject();
                    break;
            }

            return s.Length > maxLength ? s.Substring(0, maxLength - 1) + "…" : s;
        }

        private string SummarizeObject()
        {
            switch (Def?.Name)
            {
                case "GUID":
                    return FigUtil.GuidString(this) ?? "{}";
                case "Color":
                    return string.Format(CultureInfo.InvariantCulture, "rgba({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})",
                        Get("r")?.AsFloat ?? 0, Get("g")?.AsFloat ?? 0, Get("b")?.AsFloat ?? 0, Get("a")?.AsFloat ?? 0);
                case "Vector":
                    return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", Get("x")?.AsFloat ?? 0, Get("y")?.AsFloat ?? 0);
            }

            var sb = new StringBuilder("{");
            int shown = 0;
            foreach (KiwiMember m in Members)
            {
                if (shown == 3)
                {
                    sb.Append(", …");
                    break;
                }

                if (shown > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(m.Field.Name).Append(": ").Append(m.Value.Summary(24));
                shown++;
            }

            return sb.Append('}').ToString();
        }

        /// <summary>
        /// This value as JSON, for inspection. Byte arrays of up to 64 bytes are written as hex,
        /// longer ones as their length. <paramref name="budget"/> caps the output on huge trees.
        /// </summary>
        public void WriteJson(StringBuilder sb, int indent, ref int budget)
        {
            if (--budget < 0)
            {
                sb.Append("\"…\"");
                return;
            }

            switch (Kind)
            {
                case KiwiValueKind.Bool:
                    sb.Append(AsBool ? "true" : "false");
                    break;
                case KiwiValueKind.Int:
                    sb.Append(Number.ToString(CultureInfo.InvariantCulture));
                    break;
                case KiwiValueKind.Uint:
                    sb.Append(Uint.ToString(CultureInfo.InvariantCulture));
                    break;
                case KiwiValueKind.Float:
                    if (float.IsNaN(FloatValue) || float.IsInfinity(FloatValue))
                    {
                        sb.Append('"').Append(FloatValue.ToString(CultureInfo.InvariantCulture)).Append('"');
                    }
                    else
                    {
                        sb.Append(FloatValue.ToString("R", CultureInfo.InvariantCulture));
                    }

                    break;
                case KiwiValueKind.String:
                case KiwiValueKind.Enum:
                    FigUtil.JsonString(sb, String);
                    break;
                case KiwiValueKind.Bytes:
                    sb.Append('"').Append(Bytes.Length <= 64 ? FigUtil.Hex(Bytes) : $"<{Bytes.Length} bytes>").Append('"');
                    break;
                case KiwiValueKind.Array:
                    if (Items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }

                    sb.Append('[');
                    for (int i = 0; i < Items.Count; i++)
                    {
                        sb.Append(i == 0 ? "\n" : ",\n").Append(' ', (indent + 1) * 2);
                        Items[i].WriteJson(sb, indent + 1, ref budget);
                    }

                    sb.Append('\n').Append(' ', indent * 2).Append(']');
                    break;
                default:
                    if (Members.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }

                    sb.Append('{');
                    for (int i = 0; i < Members.Count; i++)
                    {
                        sb.Append(i == 0 ? "\n" : ",\n").Append(' ', (indent + 1) * 2);
                        FigUtil.JsonString(sb, Members[i].Field.Name);
                        sb.Append(": ");
                        Members[i].Value.WriteJson(sb, indent + 1, ref budget);
                    }

                    sb.Append('\n').Append(' ', indent * 2).Append('}');
                    break;
            }
        }
    }

    // ─── Byte-level reader / writer ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint Bits;
    }

    internal struct KiwiReader
    {
        private readonly byte[] data;
        private int pos;

        public KiwiReader(byte[] data)
        {
            this.data = data;
            pos = 0;
        }

        public int Left => data.Length - pos;

        public byte Byte()
        {
            if (pos >= data.Length)
            {
                throw new FormatException("kiwi: unexpected end of data");
            }

            return data[pos++];
        }

        public byte[] Take(int n)
        {
            if (n < 0 || n > Left)
            {
                throw new FormatException("kiwi: unexpected end of data");
            }

            var result = new byte[n];
            Buffer.BlockCopy(data, pos, result, 0, n);
            pos += n;
            return result;
        }

        public uint VarUint()
        {
            uint value = 0;
            int shift = 0;
            while (true)
            {
                byte b = Byte();
                if (shift < 32)
                {
                    value |= (uint)(b & 127) << shift;
                }

                shift += 7;
                if ((b & 128) == 0 || shift >= 35)
                {
                    return value;
                }
            }
        }

        public int VarInt()
        {
            uint v = VarUint();
            return (v & 1) != 0 ? ~(int)(v >> 1) : (int)(v >> 1);
        }

        public ulong VarUint64()
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                byte b = Byte();
                if (shift == 56)
                {
                    // The ninth byte carries a full eight bits.
                    return value | ((ulong)b << 56);
                }

                value |= (ulong)(b & 127) << shift;
                shift += 7;
                if ((b & 128) == 0)
                {
                    return value;
                }
            }
        }

        public long VarInt64()
        {
            ulong v = VarUint64();
            return (v & 1) != 0 ? ~(long)(v >> 1) : (long)(v >> 1);
        }

        public float VarFloat()
        {
            if (pos < data.Length && data[pos] == 0)
            {
                pos++;
                return 0f;
            }

            if (Left < 4)
            {
                throw new FormatException("kiwi: unexpected end of data");
            }

            uint bits = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16)) | ((uint)data[pos + 3] << 24);
            pos += 4;
            // The exponent is stored first so zero fits one byte; move it back into place.
            bits = (bits << 23) | (bits >> 9);
            return new FloatBits { Bits = bits }.Float;
        }

        public string String()
        {
            int end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0)
            {
                throw new FormatException("kiwi: unterminated string");
            }

            string s = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;
            return s;
        }
    }

    internal sealed class KiwiWriter
    {
        private byte[] buffer;
        private int length;

        public KiwiWriter(int capacity)
        {
            buffer = new byte[Math.Max(16, capacity)];
        }

        private void Ensure(int extra)
        {
            if (length + extra <= buffer.Length)
            {
                return;
            }

            long size = Math.Max((long)length + extra, (long)buffer.Length * 2);
            if (size > int.MaxValue - 64)
            {
                size = (long)length + extra;
                if (size > int.MaxValue - 64)
                {
                    throw new FormatException("kiwi encode: message too large");
                }
            }

            Array.Resize(ref buffer, (int)size);
        }

        public void Byte(byte b)
        {
            Ensure(1);
            buffer[length++] = b;
        }

        public void Bytes(byte[] bytes)
        {
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, buffer, length, bytes.Length);
            length += bytes.Length;
        }

        public void VarUint(uint v)
        {
            Ensure(5);
            while (true)
            {
                byte b = (byte)(v & 127);
                v >>= 7;
                if (v == 0)
                {
                    buffer[length++] = b;
                    return;
                }

                buffer[length++] = (byte)(b | 128);
            }
        }

        public void VarInt(int v)
        {
            VarUint((uint)((v << 1) ^ (v >> 31)));
        }

        public void VarUint64(ulong v)
        {
            Ensure(9);
            for (int i = 0; i < 8; i++)
            {
                byte b = (byte)(v & 127);
                v >>= 7;
                if (v == 0)
                {
                    buffer[length++] = b;
                    return;
                }

                buffer[length++] = (byte)(b | 128);
            }

            buffer[length++] = (byte)v;
        }

        public void VarInt64(long v)
        {
            VarUint64((ulong)((v << 1) ^ (v >> 63)));
        }

        public void VarFloat(float f)
        {
            uint bits = new FloatBits { Float = f }.Bits;
            bits = (bits >> 23) | (bits << 9);
            if ((bits & 255) == 0)
            {
                Byte(0);
                return;
            }

            Ensure(4);
            buffer[length++] = (byte)bits;
            buffer[length++] = (byte)(bits >> 8);
            buffer[length++] = (byte)(bits >> 16);
            buffer[length++] = (byte)(bits >> 24);
        }

        /// <summary>A NUL-terminated string (NULs inside are dropped: they would end it early).</summary>
        public void String(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            Ensure(bytes.Length + 1);
            foreach (byte b in bytes)
            {
                if (b != 0)
                {
                    buffer[length++] = b;
                }
            }

            buffer[length++] = 0;
        }

        public byte[] ToArray()
        {
            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
    }
}
