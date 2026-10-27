using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>One layer of the document: the latest node change of its GUID.</summary>
    public sealed class FigNode
    {
        public string Id;
        public KiwiValue Change;
        public FigNode Parent;
        public readonly List<FigNode> Children = new();
        public int Depth;

        public string Name => Change.Get("name")?.String ?? "";

        public string Type => Change.Get("type")?.EnumName ?? "?";

        public string Position => Change.Get("parentIndex")?.Get("position")?.String ?? "";

        public string ParentId => FigUtil.GuidString(Change.Get("parentIndex")?.Get("guid"));

        public string Label => string.IsNullOrEmpty(Name) ? Type : Name;
    }

    /// <summary>
    /// A <c>.fig</c> file in memory. The file is a zip holding <c>canvas.fig</c>, <c>meta.json</c>, a
    /// thumbnail and <c>images/&lt;sha1&gt;</c>; <c>canvas.fig</c> (also accepted on its own) is an
    /// 8-byte <c>fig-…</c> magic, a little-endian <c>u32</c> version and length-prefixed chunks, each
    /// deflated or zstd-compressed: a kiwi schema, then a message encoded with it. The message holds
    /// <c>nodeChanges</c> — one entry per layer, each naming its GUID, its parent and its position
    /// among the siblings — and <c>blobs</c> (vector data, embedded images).
    ///
    /// Saving re-encodes the message with the file's own schema, so everything this tool doesn't
    /// understand is written back as it was read. Chunks are written raw-deflated (what figcraft's
    /// exporter writes and Figma reads); other zip members are kept byte for byte.
    ///
    /// Format knowledge from figcraft (<c>crates/format/src/fig.rs</c>, MIT / Apache-2.0).
    /// </summary>
    public sealed class FigFile
    {
        public const string CanvasMember = "canvas.fig";
        public const string MetaMember = "meta.json";
        public const string ThumbnailMember = "thumbnail.png";
        public const string ImagePrefix = "images/";

        private const long MaxChunk = 1L << 30;
        private const int MaxChunks = 16;

        public string SourcePath;

        /// <summary>False when the file was a bare <c>canvas.fig</c> (what Figma's clipboard carries).</summary>
        public bool IsArchive;

        /// <summary>Every zip member except <c>canvas.fig</c>, in file order.</summary>
        public readonly List<FigZipEntry> Members = new();

        public byte[] Magic;
        public uint Version;
        public string SchemaCompression;
        public string MessageCompression;
        public int CanvasSize;

        /// <summary>Chunks after the schema and the message, kept compressed and written back as they were.</summary>
        public readonly List<byte[]> ExtraChunks = new();

        public KiwiSchema Schema;
        public KiwiDef MessageDef;
        public KiwiValue Message;

        public readonly List<FigNode> Roots = new();
        public readonly Dictionary<string, FigNode> Nodes = new();
        public readonly List<string> Warnings = new();

        public string MagicString => Encoding.ASCII.GetString(Magic ?? Array.Empty<byte>()).TrimEnd('\0');

        public List<KiwiValue> NodeChanges => Message.Get("nodeChanges")?.Items;

        // ─── Loading ─────────────────────────────────────────────────────────────

        public static FigFile Load(string path)
        {
            FigFile file = Parse(File.ReadAllBytes(path));
            file.SourcePath = path;
            return file;
        }

        public static FigFile Parse(byte[] bytes)
        {
            var file = new FigFile();
            byte[] canvas;
            if (IsCanvas(bytes))
            {
                canvas = bytes;
            }
            else if (FigZip.IsZip(bytes))
            {
                file.IsArchive = true;
                canvas = null;
                foreach (FigZipEntry entry in FigZip.Read(bytes))
                {
                    if (entry.Name == CanvasMember && canvas == null)
                    {
                        canvas = entry.Data;
                    }
                    else
                    {
                        file.Members.Add(entry);
                    }
                }

                if (canvas == null)
                {
                    throw new FormatException("This zip has no canvas.fig: it is not a .fig file.");
                }
            }
            else
            {
                throw new FormatException("Not a .fig file (expected a zip with canvas.fig, or a fig-kiwi canvas).");
            }

            file.ReadCanvas(canvas);
            file.RebuildIndex();
            return file;
        }

        private static bool IsCanvas(byte[] b)
        {
            return b.Length >= 4 && b[0] == 'f' && b[1] == 'i' && b[2] == 'g' && b[3] == '-';
        }

        private void ReadCanvas(byte[] canvas)
        {
            if (canvas.Length < 12 || !IsCanvas(canvas))
            {
                throw new FormatException("canvas.fig has no fig- header.");
            }

            CanvasSize = canvas.Length;
            Magic = new byte[8];
            Buffer.BlockCopy(canvas, 0, Magic, 0, 8);
            Version = BitConverter.ToUInt32(canvas, 8);

            var chunks = new List<byte[]>();
            int pos = 12;
            while (pos < canvas.Length && chunks.Count < MaxChunks)
            {
                if (pos + 4 > canvas.Length)
                {
                    throw new FormatException("canvas.fig: truncated chunk header.");
                }

                long length = BitConverter.ToUInt32(canvas, pos);
                pos += 4;
                if (pos + length > canvas.Length)
                {
                    throw new FormatException("canvas.fig: truncated chunk.");
                }

                var chunk = new byte[length];
                Buffer.BlockCopy(canvas, pos, chunk, 0, (int)length);
                chunks.Add(chunk);
                pos += (int)length;
            }

            if (chunks.Count < 2)
            {
                throw new FormatException("canvas.fig needs a schema chunk and a message chunk.");
            }

            byte[] schemaBytes = Decompress(chunks[0], out SchemaCompression);
            byte[] messageBytes = Decompress(chunks[1], out MessageCompression);
            for (int i = 2; i < chunks.Count; i++)
            {
                ExtraChunks.Add(chunks[i]);
            }

            Schema = KiwiSchema.Decode(schemaBytes);
            MessageDef = Schema.Find("Message") ?? throw new FormatException("The schema has no Message definition.");
            Message = Schema.DecodeMessage(MessageDef, messageBytes);
        }

        /// <summary>Decompress a chunk: zstd (by its magic number), else raw deflate, else zlib.</summary>
        private static byte[] Decompress(byte[] chunk, out string method)
        {
            if (FigZstd.IsZstd(chunk))
            {
                method = "zstd";
                return FigZstd.Decompress(chunk, MaxChunk);
            }

            try
            {
                method = "deflate";
                return FigUtil.Inflate(chunk, 0, chunk.Length, MaxChunk);
            }
            catch (Exception) when (chunk.Length > 2 && (chunk[0] & 0x0F) == 8)
            {
                // A zlib stream: a two-byte header, then raw deflate.
                method = "zlib";
                return FigUtil.Inflate(chunk, 2, chunk.Length - 2, MaxChunk);
            }
        }

        // ─── Node index ──────────────────────────────────────────────────────────

        /// <summary>Rebuild the layer tree from <c>nodeChanges</c> (the last change of a GUID wins; REMOVED deletes).</summary>
        public void RebuildIndex()
        {
            Nodes.Clear();
            Roots.Clear();
            Warnings.Clear();

            List<KiwiValue> changes = NodeChanges;
            if (changes == null)
            {
                Warnings.Add("The message has no nodeChanges.");
                return;
            }

            foreach (KiwiValue change in changes)
            {
                string id = FigUtil.GuidString(change.Get("guid"));
                if (id == null)
                {
                    continue;
                }

                if (change.Get("phase")?.EnumName == "REMOVED")
                {
                    Nodes.Remove(id);
                    continue;
                }

                if (Nodes.TryGetValue(id, out FigNode existing))
                {
                    existing.Change = change;
                }
                else
                {
                    Nodes[id] = new FigNode { Id = id, Change = change };
                }
            }

            foreach (FigNode node in Nodes.Values)
            {
                string parentId = node.ParentId;
                if (parentId != null && parentId != node.Id && Nodes.TryGetValue(parentId, out FigNode parent))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else
                {
                    Roots.Add(node);
                }
            }

            Comparison<FigNode> byPosition = (a, b) =>
            {
                int c = string.CompareOrdinal(a.Position, b.Position);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            };

            Roots.Sort(byPosition);
            var stack = new Stack<FigNode>(Roots);
            var visited = new HashSet<FigNode>();
            while (stack.Count > 0)
            {
                FigNode node = stack.Pop();
                if (!visited.Add(node))
                {
                    continue;
                }

                node.Children.Sort(byPosition);
                foreach (FigNode child in node.Children)
                {
                    child.Depth = node.Depth + 1;
                    stack.Push(child);
                }
            }

            if (visited.Count < Nodes.Count)
            {
                // Parent cycles: the nodes never reached from a root are shown at the top level.
                foreach (FigNode node in Nodes.Values)
                {
                    if (!visited.Contains(node))
                    {
                        node.Parent?.Children.Remove(node);
                        node.Parent = null;
                        node.Depth = 0;
                        Roots.Add(node);
                    }
                }

                Warnings.Add("Some layers form a parent cycle; they are listed at the top level.");
            }
        }

        public FigNode Find(string id)
        {
            return id != null && Nodes.TryGetValue(id, out FigNode n) ? n : null;
        }

        // ─── Members ─────────────────────────────────────────────────────────────

        public FigZipEntry Member(string name)
        {
            foreach (FigZipEntry e in Members)
            {
                if (e.Name == name)
                {
                    return e;
                }
            }

            return null;
        }

        public IEnumerable<FigZipEntry> Images
        {
            get
            {
                foreach (FigZipEntry e in Members)
                {
                    if (e.Name.StartsWith(ImagePrefix, StringComparison.Ordinal))
                    {
                        yield return e;
                    }
                }
            }
        }

        /// <summary>The bytes of image <paramref name="hash"/>: an <c>images/</c> member, else a message blob.</summary>
        public byte[] ImageBytes(byte[] hash)
        {
            if (hash == null || hash.Length == 0)
            {
                return null;
            }

            FigZipEntry member = Member(ImagePrefix + FigUtil.Hex(hash));
            return member?.Data;
        }

        /// <summary>
        /// Replace image <paramref name="oldHash"/> with <paramref name="bytes"/>. Figma names images by
        /// the SHA-1 of their bytes, so the new image gets its own hash and every reference to the old
        /// one (any byte array equal to it) is pointed at the new one. Returns how many were changed.
        /// </summary>
        public int ReplaceImage(string oldHash, byte[] bytes)
        {
            byte[] newHash = FigUtil.Sha1(bytes);
            string newHex = FigUtil.Hex(newHash);
            byte[] old = FigUtil.FromHex(oldHash);

            FigZipEntry member = Member(ImagePrefix + oldHash);
            if (member != null)
            {
                member.Name = ImagePrefix + newHex;
                member.Data = bytes;
                FigZipEntry duplicate = null;
                foreach (FigZipEntry e in Members)
                {
                    if (e != member && e.Name == member.Name)
                    {
                        duplicate = e;
                    }
                }

                if (duplicate != null)
                {
                    Members.Remove(duplicate);
                }
            }
            else
            {
                Members.Add(new FigZipEntry(ImagePrefix + newHex, bytes));
            }

            return old == null ? 0 : ReplaceBytes(Message, old, newHash);
        }

        /// <summary>Add <paramref name="bytes"/> as an image member; returns its hash.</summary>
        public byte[] AddImage(byte[] bytes)
        {
            byte[] hash = FigUtil.Sha1(bytes);
            string name = ImagePrefix + FigUtil.Hex(hash);
            if (Member(name) == null)
            {
                Members.Add(new FigZipEntry(name, bytes));
            }

            return hash;
        }

        private static int ReplaceBytes(KiwiValue root, byte[] from, byte[] to)
        {
            int changed = 0;
            var pending = new Stack<KiwiValue>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                KiwiValue v = pending.Pop();
                switch (v.Kind)
                {
                    case KiwiValueKind.Bytes:
                        if (SameBytes(v.Bytes, from))
                        {
                            v.Ref = (byte[])to.Clone();
                            changed++;
                        }

                        break;
                    case KiwiValueKind.Array:
                        foreach (KiwiValue item in v.Items)
                        {
                            pending.Push(item);
                        }

                        break;
                    case KiwiValueKind.Object:
                        foreach (KiwiMember m in v.Members)
                        {
                            // Blobs hold vector data and embedded images, never a hash.
                            if (m.Field.Name != "blobs")
                            {
                                pending.Push(m.Value);
                            }
                        }

                        break;
                }
            }

            return changed;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ─── Layer edits ─────────────────────────────────────────────────────────

        /// <summary>
        /// Set a text layer's characters, keeping <c>textData</c> consistent: one
        /// <c>characterStyleIDs</c> entry per UTF-16 unit and one <c>lines</c> entry per paragraph.
        /// The cached layout (<c>derivedTextData</c>) is dropped so the reader lays the text out again.
        /// </summary>
        public static void SetText(FigNode node, string text)
        {
            KiwiValue textData = node.Change.Get("textData");
            if (textData == null)
            {
                return;
            }

            text ??= "";
            textData.Set("characters", KiwiValue.FromString(text));

            List<KiwiValue> ids = textData.Get("characterStyleIDs")?.Items;
            if (ids != null)
            {
                Resize(ids, text.Length, ids.Count > 0 ? ids[ids.Count - 1] : null, () => KiwiValue.FromUint(0));
            }

            List<KiwiValue> lines = textData.Get("lines")?.Items;
            if (lines != null && lines.Count > 0)
            {
                int paragraphs = 1;
                foreach (char c in text)
                {
                    if (c == '\n')
                    {
                        paragraphs++;
                    }
                }

                Resize(lines, paragraphs, lines[lines.Count - 1], null);
            }

            node.Change.Remove("derivedTextData");
        }

        private static void Resize(List<KiwiValue> list, int count, KiwiValue template, Func<KiwiValue> fallback)
        {
            if (list.Count > count)
            {
                list.RemoveRange(count, list.Count - count);
                return;
            }

            while (list.Count < count)
            {
                KiwiValue item = template?.Clone() ?? fallback?.Invoke();
                if (item == null)
                {
                    return;
                }

                list.Add(item);
            }
        }

        /// <summary>Remove <paramref name="node"/> and its descendants. Returns how many layers went.</summary>
        public int Delete(FigNode node)
        {
            var doomed = new HashSet<KiwiValue>();
            var ids = new HashSet<string>();
            var stack = new Stack<FigNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                FigNode n = stack.Pop();
                ids.Add(n.Id);
                foreach (FigNode child in n.Children)
                {
                    stack.Push(child);
                }
            }

            // Every change of those GUIDs goes, not just the latest one.
            foreach (KiwiValue change in NodeChanges)
            {
                if (ids.Contains(FigUtil.GuidString(change.Get("guid")) ?? ""))
                {
                    doomed.Add(change);
                }
            }

            NodeChanges.RemoveAll(doomed.Contains);
            RebuildIndex();
            return ids.Count;
        }

        /// <summary>Swap <paramref name="node"/> with its previous (<paramref name="delta"/> = -1) or next (+1) sibling.</summary>
        public bool MoveAmongSiblings(FigNode node, int delta)
        {
            List<FigNode> siblings = node.Parent?.Children ?? Roots;
            int index = siblings.IndexOf(node);
            int other = index + delta;
            if (index < 0 || other < 0 || other >= siblings.Count)
            {
                return false;
            }

            KiwiValue a = node.Change.Get("parentIndex")?.Get("position");
            KiwiValue b = siblings[other].Change.Get("parentIndex")?.Get("position");
            if (a == null || b == null || a.String == b.String)
            {
                return false;
            }

            string swap = a.String;
            a.String = b.String;
            b.String = swap;
            RebuildIndex();
            return true;
        }

        /// <summary>
        /// Copy <paramref name="node"/> and its descendants with fresh GUIDs, placed right after it.
        /// References to layers inside the copy (e.g. from instance overrides) still point at the
        /// originals. Returns the copy.
        /// </summary>
        public FigNode Duplicate(FigNode node)
        {
            List<KiwiValue> changes = NodeChanges;
            // Highest local id per session over every change, removed ones included, so no GUID is reused.
            var used = new Dictionary<ulong, ulong>();
            foreach (KiwiValue change in changes)
            {
                KiwiValue guid = change.Get("guid");
                KiwiValue sessionValue = guid?.Get("sessionID");
                KiwiValue localValue = guid?.Get("localID");
                if (sessionValue == null || localValue == null)
                {
                    continue;
                }

                ulong session = sessionValue.Uint;
                ulong local = localValue.Uint;
                used[session] = used.TryGetValue(session, out ulong max) ? Math.Max(max, local) : local;
            }

            var remap = new Dictionary<string, KiwiValue>();
            var order = new List<FigNode>();
            var stack = new Stack<FigNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                FigNode n = stack.Pop();
                order.Add(n);
                for (int i = n.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(n.Children[i]);
                }
            }

            int insertAt = changes.Count;
            KiwiValue copyRoot = null;
            var copies = new List<KiwiValue>();
            foreach (FigNode n in order)
            {
                KiwiValue copy = n.Change.Clone();
                KiwiValue guid = copy.Get("guid");
                ulong session = guid.Get("sessionID").Uint;
                ulong next = (used.TryGetValue(session, out ulong max) ? max : 0) + 1;
                used[session] = next;
                guid.Set("localID", KiwiValue.FromUint(next));
                remap[n.Id] = guid;
                copies.Add(copy);
                copyRoot ??= copy;
            }

            // Re-parent the copies onto each other, by the original parent ids.
            for (int i = 1; i < order.Count; i++)
            {
                KiwiValue parentGuid = copies[i].Get("parentIndex")?.Get("guid");
                if (parentGuid != null && remap.TryGetValue(order[i].ParentId ?? "", out KiwiValue newParent))
                {
                    parentGuid.Set("sessionID", KiwiValue.FromUint(newParent.Get("sessionID").Uint));
                    parentGuid.Set("localID", KiwiValue.FromUint(newParent.Get("localID").Uint));
                }
            }

            // Place the copy right after the original among its siblings.
            KiwiValue position = copyRoot.Get("parentIndex")?.Get("position");
            if (position != null)
            {
                List<FigNode> siblings = node.Parent?.Children ?? Roots;
                int index = siblings.IndexOf(node);
                string after = node.Position;
                string candidate = after + "V";
                if (index >= 0 && index + 1 < siblings.Count && string.CompareOrdinal(candidate, siblings[index + 1].Position) >= 0)
                {
                    candidate = siblings[siblings.Count - 1].Position + "V";
                }

                position.String = candidate;
            }

            KiwiValue name = copyRoot.Get("name");
            if (name != null)
            {
                name.String += " copy";
            }

            changes.InsertRange(insertAt, copies);
            RebuildIndex();
            return Find(FigUtil.GuidString(copyRoot.Get("guid")));
        }

        // ─── New layers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Add a new layer of <paramref name="type"/> under <paramref name="parent"/>, on top of its
        /// siblings, at <paramref name="x"/>, <paramref name="y"/> (parent space) and
        /// <paramref name="w"/> × <paramref name="h"/>. <paramref name="customize"/> sets the rest
        /// (fills, text…) through the helpers below. Fields the file's schema lacks are skipped.
        /// Returns the new layer, or null when the schema can't describe it.
        /// </summary>
        public FigNode CreateNode(FigNode parent, string type, string name, float x, float y, float w, float h, Action<KiwiValue> customize = null)
        {
            KiwiDef nodeDef = MessageDef.FieldByName("nodeChanges")?.TypeDef;
            if (nodeDef == null || parent == null)
            {
                return null;
            }

            KiwiValue n = KiwiValue.FromObject(nodeDef, new List<KiwiMember>());
            ulong session = 0, local = 0;
            foreach (KiwiValue change in NodeChanges)
            {
                KiwiValue g = change.Get("guid");
                if (g?.Get("sessionID") == null || g.Get("localID") == null)
                {
                    continue;
                }

                if (g.Get("sessionID").Uint > session)
                {
                    session = g.Get("sessionID").Uint;
                    local = 0;
                }

                if (g.Get("sessionID").Uint == session)
                {
                    local = Math.Max(local, g.Get("localID").Uint);
                }
            }

            KiwiValue guid = Default(nodeDef, "guid");
            guid?.Set("sessionID", KiwiValue.FromUint(session));
            guid?.Set("localID", KiwiValue.FromUint(local + 1));
            n.Set("guid", guid);
            SetEnum(n, "phase", "CREATED");

            KiwiValue parentIndex = Default(nodeDef, "parentIndex");
            if (parentIndex != null)
            {
                parentIndex.Set("guid", parent.Change.Get("guid").Clone());
                string last = parent.Children.Count > 0 ? parent.Children[parent.Children.Count - 1].Position : "";
                parentIndex.Set("position", KiwiValue.FromString(last + "~"));
                n.Set("parentIndex", parentIndex);
            }

            if (!SetEnum(n, "type", type))
            {
                return null;
            }

            n.Set("name", KiwiValue.FromString(name));
            n.Set("visible", KiwiValue.FromBool(true));
            n.Set("opacity", KiwiValue.FromFloat(1f));
            KiwiValue size = Default(nodeDef, "size");
            size?.Set("x", KiwiValue.FromFloat(w));
            size?.Set("y", KiwiValue.FromFloat(h));
            n.Set("size", size);
            KiwiValue transform = Default(nodeDef, "transform");
            if (transform != null)
            {
                transform.Set("m00", KiwiValue.FromFloat(1));
                transform.Set("m11", KiwiValue.FromFloat(1));
                transform.Set("m02", KiwiValue.FromFloat(x));
                transform.Set("m12", KiwiValue.FromFloat(y));
                n.Set("transform", transform);
            }

            customize?.Invoke(n);
            NodeChanges.Add(n);
            RebuildIndex();
            return Find(FigUtil.GuidString(guid));
        }

        /// <summary>A fresh value for field <paramref name="field"/> of <paramref name="def"/>, or null when it has none.</summary>
        public static KiwiValue Default(KiwiDef def, string field)
        {
            KiwiField f = def?.FieldByName(field);
            return f == null ? null : KiwiSchema.DefaultField(f);
        }

        /// <summary>Set enum field <paramref name="field"/> to the value named <paramref name="name"/>.</summary>
        public static bool SetEnum(KiwiValue owner, string field, string name)
        {
            KiwiField f = owner.Def.FieldByName(field);
            KiwiField value = f?.TypeDef?.FieldByName(name);
            if (value == null || f.TypeDef.Kind != KiwiDefKind.Enum)
            {
                return false;
            }

            return owner.Set(field, KiwiValue.FromEnum(f.TypeDef, value.Value));
        }

        /// <summary>A solid paint of <paramref name="color"/> for a <c>fillPaints</c> / <c>strokePaints</c> list of <paramref name="owner"/>.</summary>
        public static KiwiValue SolidPaint(KiwiValue owner, string listField, float r, float g, float b, float a)
        {
            KiwiField list = owner.Def.FieldByName(listField);
            if (list?.TypeDef == null)
            {
                return null;
            }

            KiwiValue paint = KiwiSchema.DefaultElement(list);
            SetEnum(paint, "type", "SOLID");
            KiwiValue color = Default(paint.Def, "color");
            if (color != null)
            {
                color.Set("r", KiwiValue.FromFloat(r));
                color.Set("g", KiwiValue.FromFloat(g));
                color.Set("b", KiwiValue.FromFloat(b));
                color.Set("a", KiwiValue.FromFloat(1f));
                paint.Set("color", color);
            }

            paint.Set("opacity", KiwiValue.FromFloat(a));
            paint.Set("visible", KiwiValue.FromBool(true));
            SetEnum(paint, "blendMode", "NORMAL");
            return paint;
        }

        /// <summary>Replace <paramref name="listField"/> of <paramref name="owner"/> with one solid paint.</summary>
        public static void SetSolidFill(KiwiValue owner, string listField, float r, float g, float b, float a)
        {
            KiwiValue paint = SolidPaint(owner, listField, r, g, b, a);
            if (paint != null)
            {
                owner.Set(listField, KiwiValue.FromArray(new List<KiwiValue> { paint }));
            }
        }

        // ─── Saving ──────────────────────────────────────────────────────────────

        public byte[] BuildCanvas()
        {
            byte[] schema = FigUtil.Deflate(Schema.Encode());
            byte[] message = FigUtil.Deflate(Schema.EncodeMessage(MessageDef, Message));

            using var output = new MemoryStream();
            output.Write(Magic, 0, Magic.Length);
            output.Write(BitConverter.GetBytes(Version), 0, 4);
            foreach (byte[] chunk in new[] { schema, message })
            {
                output.Write(BitConverter.GetBytes((uint)chunk.Length), 0, 4);
                output.Write(chunk, 0, chunk.Length);
            }

            foreach (byte[] chunk in ExtraChunks)
            {
                output.Write(BitConverter.GetBytes((uint)chunk.Length), 0, 4);
                output.Write(chunk, 0, chunk.Length);
            }

            return output.ToArray();
        }

        public byte[] Build()
        {
            byte[] canvas = BuildCanvas();
            if (!IsArchive)
            {
                return canvas;
            }

            var entries = new List<FigZipEntry> { new(CanvasMember, canvas) };
            entries.AddRange(Members);
            return FigZip.Write(entries, name => name == MetaMember);
        }

        public void Save(string path)
        {
            byte[] bytes = Build();
            // Write next to the target first so a failure never leaves a half-written design file.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
            SourcePath = path;
        }

        public string ToJson(KiwiValue value, int maxValues = 2_000_000)
        {
            var sb = new StringBuilder();
            int budget = maxValues;
            value.WriteJson(sb, 0, ref budget);
            if (budget < 0)
            {
                sb.Append("\n// truncated after ").Append(maxValues).Append(" values");
            }

            return sb.ToString();
        }
    }
}
