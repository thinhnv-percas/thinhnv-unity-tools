using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>Where a <c>.fig</c> made from a Figma link came from (kept in its <c>meta.json</c>).</summary>
    public sealed class FigmaSource
    {
        public const string MetaKey = "thinhnv_figma_source";

        public string Url;
        public string FileKey;
        public string NodeIds;
        public string Name;
        public string Version;
        public string LastModified;
        public string ImportedAt;

        /// <summary>Set by a partial update: the Figma version some frames were last updated to, and which.</summary>
        public string PartialVersion;

        public string PartialIds;

        public static FigmaSource FromFile(FigFile file)
        {
            FigZipEntry meta = file?.Members.Find(m => m.Name == FigFile.MetaMember);
            if (meta == null)
            {
                return null;
            }

            try
            {
                object json = FigJson.Parse(Encoding.UTF8.GetString(meta.Data));
                Dictionary<string, object> s = FigJson.Obj(json, MetaKey);
                if (s == null || string.IsNullOrEmpty(FigJson.Str(s, "fileKey")))
                {
                    return null;
                }

                return new FigmaSource
                {
                    Url = FigJson.Str(s, "url"),
                    FileKey = FigJson.Str(s, "fileKey"),
                    NodeIds = FigJson.Str(s, "nodeIds"),
                    Name = FigJson.Str(s, "name"),
                    Version = FigJson.Str(s, "version"),
                    LastModified = FigJson.Str(s, "lastModified"),
                    ImportedAt = FigJson.Str(s, "importedAt"),
                    PartialVersion = FigJson.Str(s, "partialVersion"),
                    PartialIds = FigJson.Str(s, "partialIds"),
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public void WriteJson(StringBuilder sb)
        {
            sb.Append('{');
            Field(sb, "url", Url, true);
            Field(sb, "fileKey", FileKey, false);
            Field(sb, "nodeIds", NodeIds, false);
            Field(sb, "name", Name, false);
            Field(sb, "version", Version, false);
            Field(sb, "lastModified", LastModified, false);
            Field(sb, "importedAt", ImportedAt, false);
            Field(sb, "partialVersion", PartialVersion, false);
            Field(sb, "partialIds", PartialIds, false);
            sb.Append('}');
        }

        private static void Field(StringBuilder sb, string key, string value, bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }

            FigUtil.JsonString(sb, key);
            sb.Append(':');
            if (value == null)
            {
                sb.Append("null");
            }
            else
            {
                FigUtil.JsonString(sb, value);
            }
        }
    }

    /// <summary>
    /// Turns a Figma REST file response (<c>GET /v1/files/:key?geometry=paths</c>) into a <c>.fig</c>
    /// file written with <see cref="FigDefaultSchema"/>: one node change per layer with its
    /// transform, size, paints, strokes, effects, corner radii, constraints, auto layout settings,
    /// fill / stroke outlines and text, plus the image fills' bytes and the thumbnail.
    ///
    /// What REST doesn't carry is left out: glyph outlines (text is drawn in an OS font until Figma
    /// lays it out), component properties and overrides (instances arrive resolved and become
    /// frames), prototype links and variables. Thread-safe (no Unity API), so it runs off the
    /// main thread.
    /// </summary>
    public sealed class FigRestImport
    {
        private const uint FreshSession = 60000;

        private readonly KiwiSchema schema;

        /// <summary>Blob indices start here (after the blobs a merged-into file already has).</summary>
        private int blobBase;
        private readonly List<Dictionary<string, object>> nodes = new();
        private readonly List<object> blobs = new();
        private readonly Dictionary<string, byte[]> imageHashes = new();
        private readonly List<string> warnings;
        private readonly HashSet<string> warned = new();
        private uint freshLocal = 1;

        private FigRestImport(List<string> warnings, KiwiSchema schema = null)
        {
            this.warnings = warnings;
            this.schema = schema ?? FigDefaultSchema.Schema;
        }

        /// <summary>
        /// The <c>.fig</c> bytes for <paramref name="file"/>. <paramref name="images"/> maps image refs to
        /// their downloaded bytes (missing ones draw as placeholders); <paramref name="thumbnail"/> may be null.
        /// </summary>
        public static byte[] Build(object file, IDictionary<string, byte[]> images, byte[] thumbnail, FigmaSource source, List<string> warnings)
        {
            var import = new FigRestImport(warnings ?? new List<string>());
            Dictionary<string, object> document = FigJson.Obj(file, "document");
            if (document == null)
            {
                throw new FormatException("The Figma response has no document.");
            }

            // Figma names image members by the SHA-1 of their bytes; the REST image refs are those
            // hashes, but hash the downloads anyway so the archive stays consistent.
            var members = new List<FigZipEntry>();
            if (images != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in images)
                {
                    if (pair.Value == null || pair.Value.Length == 0)
                    {
                        continue;
                    }

                    byte[] hash = FigUtil.Sha1(pair.Value);
                    import.imageHashes[pair.Key] = hash;
                    members.Add(new FigZipEntry(FigFile.ImagePrefix + FigUtil.Hex(hash), pair.Value));
                }
            }

            import.Document(document);
            var message = new Dictionary<string, object>
            {
                ["type"] = "NODE_CHANGES",
                ["sessionID"] = 0u,
                ["nodeChanges"] = import.nodes.ConvertAll(n => (object)n),
                ["blobs"] = import.blobs,
            };

            KiwiDef messageDef = import.schema.Find("Message");
            KiwiValue value = import.Object(messageDef, message);
            byte[] canvas = Canvas(import.schema, messageDef, value);

            var entries = new List<FigZipEntry> { new FigZipEntry(FigFile.CanvasMember, canvas) };
            entries.Add(new FigZipEntry(FigFile.MetaMember, Encoding.UTF8.GetBytes(Meta(file, source, import.imageHashes))));
            if (thumbnail != null && thumbnail.Length > 0)
            {
                entries.Add(new FigZipEntry(FigFile.ThumbnailMember, thumbnail));
            }

            entries.AddRange(members);
            return FigZip.Write(entries, name => name == FigFile.MetaMember);
        }

        private static byte[] Canvas(KiwiSchema schema, KiwiDef messageDef, KiwiValue message)
        {
            byte[] schemaChunk = FigUtil.Deflate(schema.Encode());
            byte[] messageChunk = FigUtil.Deflate(schema.EncodeMessage(messageDef, message));
            using var output = new MemoryStream();
            byte[] magic = Encoding.ASCII.GetBytes("fig-kiwi");
            output.Write(magic, 0, magic.Length);
            output.Write(BitConverter.GetBytes(FigDefaultSchema.CanvasVersion), 0, 4);
            foreach (byte[] chunk in new[] { schemaChunk, messageChunk })
            {
                output.Write(BitConverter.GetBytes((uint)chunk.Length), 0, 4);
                output.Write(chunk, 0, chunk.Length);
            }

            return output.ToArray();
        }

        /// <summary>The meta.json key mapping Figma image refs to the file's image members (so updates reuse them).</summary>
        public const string ImageMapKey = "thinhnv_figma_images";

        /// <summary>Figma image ref → image member hash, as recorded by an import (empty when none).</summary>
        public static Dictionary<string, string> ImageMap(FigFile file)
        {
            var map = new Dictionary<string, string>();
            FigZipEntry meta = file?.Members.Find(m => m.Name == FigFile.MetaMember);
            if (meta == null)
            {
                return map;
            }

            try
            {
                Dictionary<string, object> refs = FigJson.Obj(FigJson.Parse(Encoding.UTF8.GetString(meta.Data)), ImageMapKey);
                if (refs != null)
                {
                    foreach (KeyValuePair<string, object> pair in refs)
                    {
                        if (pair.Value is string hex)
                        {
                            map[pair.Key] = hex;
                        }
                    }
                }
            }
            catch (FormatException)
            {
                // No map: refs are matched by name only.
            }

            return map;
        }

        private static Dictionary<string, object> ImageMapJson(Dictionary<string, byte[]> hashes)
        {
            var refs = new Dictionary<string, object>();
            foreach (KeyValuePair<string, byte[]> pair in hashes)
            {
                refs[pair.Key] = FigUtil.Hex(pair.Value);
            }

            return refs;
        }

        private static string Meta(object file, FigmaSource source, Dictionary<string, byte[]> imageHashes)
        {
            if (imageHashes.Count > 0)
            {
                var meta = new Dictionary<string, object>
                {
                    ["file_name"] = FigJson.Str(file, "name") ?? source?.Name ?? "Figma file",
                    ["exported_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                };

                if (source != null)
                {
                    var src = new StringBuilder();
                    source.WriteJson(src);
                    meta[FigmaSource.MetaKey] = FigJson.Parse(src.ToString());
                }

                meta[ImageMapKey] = ImageMapJson(imageHashes);
                return FigJson.Write(meta);
            }

            var sb = new StringBuilder("{");
            FigUtil.JsonString(sb, "file_name");
            sb.Append(':');
            FigUtil.JsonString(sb, FigJson.Str(file, "name") ?? source?.Name ?? "Figma file");
            sb.Append(',');
            FigUtil.JsonString(sb, "exported_at");
            sb.Append(':');
            FigUtil.JsonString(sb, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            if (source != null)
            {
                sb.Append(',');
                FigUtil.JsonString(sb, FigmaSource.MetaKey);
                sb.Append(':');
                source.WriteJson(sb);
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Update some layers of an existing file: each of <paramref name="ids"/> is replaced (with its
        /// subtree) by its current version from a <c>GET /v1/files/:key/nodes</c> response, in place
        /// (same parent and position); everything else in the file is kept as it is. New images are
        /// added; images already in the file are reused. Returns the new file's bytes.
        /// </summary>
        public static byte[] Merge(FigFile local, object nodesResponse, IList<string> ids, IDictionary<string, byte[]> images, FigmaSource source, List<string> warnings)
        {
            warnings ??= new List<string>();
            var import = new FigRestImport(warnings, local.Schema);
            KiwiDef nodeDef = local.Schema.Find("NodeChange");
            KiwiField blobsField = local.MessageDef.FieldByName("blobs");
            if (nodeDef == null || blobsField?.TypeDef == null)
            {
                throw new FormatException("This file's schema has no node changes to update.");
            }

            List<KiwiValue> changes = local.NodeChanges;
            KiwiValue blobsValue = local.Message.Get("blobs");
            if (blobsValue == null)
            {
                blobsValue = KiwiValue.FromArray(new List<KiwiValue>());
                local.Message.Set("blobs", blobsValue);
            }

            import.blobBase = blobsValue.Items.Count;
            // Fresh ids (layers inside instances) continue after the ones the file already has.
            foreach (KiwiValue c in changes)
            {
                KiwiValue g = c.Get("guid");
                if (g != null && (uint)(g.Get("sessionID")?.Uint ?? 0) == FreshSession)
                {
                    import.freshLocal = Math.Max(import.freshLocal, (uint)(g.Get("localID")?.Uint ?? 0) + 1);
                }
            }

            // Images: what the file has is reused; downloads are hashed like a full import.
            var have = new HashSet<string>();
            foreach (FigZipEntry m in local.Members)
            {
                if (m.Name.StartsWith(FigFile.ImagePrefix, StringComparison.Ordinal))
                {
                    have.Add(m.Name.Substring(FigFile.ImagePrefix.Length));
                }
            }

            if (images != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in images)
                {
                    if (pair.Value == null || pair.Value.Length == 0)
                    {
                        continue;
                    }

                    byte[] hash = FigUtil.Sha1(pair.Value);
                    import.imageHashes[pair.Key] = hash;
                    string hex = FigUtil.Hex(hash);
                    if (have.Add(hex))
                    {
                        local.Members.Add(new FigZipEntry(FigFile.ImagePrefix + hex, pair.Value));
                    }
                }
            }

            foreach (KeyValuePair<string, string> pair in ImageMap(local))
            {
                if (have.Contains(pair.Value) && !import.imageHashes.ContainsKey(pair.Key))
                {
                    import.imageHashes[pair.Key] = FigUtil.FromHex(pair.Value);
                }
            }

            foreach (string hex in have)
            {
                if (!import.imageHashes.ContainsKey(hex))
                {
                    import.imageHashes[hex] = FigUtil.FromHex(hex);
                }
            }

            Dictionary<string, object> nodes = FigJson.Obj(nodesResponse, "nodes") ?? new Dictionary<string, object>();
            var removed = new HashSet<string>();
            var updated = new List<string>();
            foreach (string id in ids)
            {
                Dictionary<string, object> document = FigJson.Obj(nodes.TryGetValue(id, out object entry) ? entry : null, "document");
                FigNode target = local.Find(id);
                if (document == null)
                {
                    warnings.Add($"Layer {id}{(target != null ? " (" + target.Name + ")" : "")} is no longer in the Figma file; kept as it was.");
                    continue;
                }

                if (target == null || target.Parent == null)
                {
                    warnings.Add($"Layer {id} ({FigJson.Str(document, "name")}) isn't in this file yet: update the whole file to add it.");
                    continue;
                }

                // Placement: the parent's page transform, and the frame REST measures group children from.
                FigAffine parentWorld = FigAffine.Identity, frameWorld = FigAffine.Identity;
                bool frameFound = false;
                var chain = new List<FigNode>();
                for (FigNode p = target.Parent; p != null && p.Type != "CANVAS" && p.Type != "DOCUMENT"; p = p.Parent)
                {
                    chain.Insert(0, p);
                }

                foreach (FigNode p in chain)
                {
                    parentWorld = parentWorld.Then(FigAffine.FromMatrix(p.Change.Get("transform")));
                }

                for (int k = chain.Count - 1; k >= 0 && !frameFound; k--)
                {
                    if (chain[k].Type == "FRAME" || chain[k].Type == "SYMBOL" || chain[k].Type == "SECTION" || chain[k].Type == "INSTANCE")
                    {
                        frameFound = true;
                        frameWorld = FigAffine.Identity;
                        for (int m = 0; m <= k; m++)
                        {
                            frameWorld = frameWorld.Then(FigAffine.FromMatrix(chain[m].Change.Get("transform")));
                        }
                    }
                }

                KiwiValue parentGuid = target.Change.Get("parentIndex")?.Get("guid");
                var parentDict = new Dictionary<string, object>
                {
                    ["sessionID"] = (uint)(parentGuid?.Get("sessionID")?.Uint ?? 0),
                    ["localID"] = (uint)(parentGuid?.Get("localID")?.Uint ?? 0),
                };

                int first = import.nodes.Count;
                import.Layer(document, parentDict, 0, parentWorld, frameWorld);
                // The layer keeps its place among its siblings.
                ((Dictionary<string, object>)import.nodes[first]["parentIndex"])["position"] = target.Position;
                CollectSubtree(target, removed);
                updated.Add(id);
            }

            if (updated.Count > 0)
            {
                // A layer moved into an updated frame from elsewhere leaves its old place.
                foreach (Dictionary<string, object> n in import.nodes)
                {
                    var g = (Dictionary<string, object>)n["guid"];
                    FigNode moved = local.Find(Convert.ToUInt32(g["sessionID"]) + ":" + Convert.ToUInt32(g["localID"]));
                    if (moved != null && !removed.Contains(moved.Id))
                    {
                        CollectSubtree(moved, removed);
                    }
                }

                changes.RemoveAll(c => removed.Contains(FigUtil.GuidString(c.Get("guid"))));
                foreach (Dictionary<string, object> n in import.nodes)
                {
                    changes.Add(import.Object(nodeDef, n));
                }

                foreach (object b in import.blobs)
                {
                    blobsValue.Items.Add(import.Object(blobsField.TypeDef, (Dictionary<string, object>)b));
                }
            }

            // meta.json: the partial update's version, other keys kept.
            FigZipEntry meta = local.Members.Find(m => m.Name == FigFile.MetaMember);
            Dictionary<string, object> metaJson = null;
            try
            {
                metaJson = meta != null ? FigJson.Parse(Encoding.UTF8.GetString(meta.Data)) as Dictionary<string, object> : null;
            }
            catch (FormatException)
            {
                metaJson = null;
            }

            metaJson ??= new Dictionary<string, object>();
            if (source != null)
            {
                var sb = new StringBuilder();
                source.WriteJson(sb);
                metaJson[FigmaSource.MetaKey] = FigJson.Parse(sb.ToString());
            }

            Dictionary<string, object> refsJson = ImageMapJson(import.imageHashes);
            foreach (string hex in have)
            {
                // Members named by their own hash map to themselves: only real refs are kept.
                if (refsJson.TryGetValue(hex, out object v) && (string)v == hex && !(FigJson.Obj(metaJson, ImageMapKey)?.ContainsKey(hex) ?? false))
                {
                    refsJson.Remove(hex);
                }
            }

            metaJson[ImageMapKey] = refsJson;
            byte[] metaBytes = Encoding.UTF8.GetBytes(FigJson.Write(metaJson));
            if (meta != null)
            {
                local.Members[local.Members.IndexOf(meta)] = new FigZipEntry(FigFile.MetaMember, metaBytes);
            }
            else
            {
                local.Members.Add(new FigZipEntry(FigFile.MetaMember, metaBytes));
            }

            local.IsArchive = true;
            local.RebuildIndex();
            return local.Build();
        }

        private static void CollectSubtree(FigNode node, HashSet<string> ids)
        {
            ids.Add(node.Id);
            foreach (FigNode c in node.Children)
            {
                CollectSubtree(c, ids);
            }
        }

        private void Warn(string message)
        {
            if (warned.Add(message))
            {
                warnings.Add(message);
            }
        }

        // ─── Tree ────────────────────────────────────────────────────────────────

        private void Document(Dictionary<string, object> document)
        {
            var root = Guid(0, 0);
            nodes.Add(new Dictionary<string, object>
            {
                ["guid"] = root,
                ["phase"] = "CREATED",
                ["type"] = "DOCUMENT",
                ["name"] = FigJson.Str(document, "name") ?? "Document",
                ["visible"] = true,
            });

            List<object> pages = FigJson.Arr(document, "children") ?? new List<object>();
            for (int i = 0; i < pages.Count; i++)
            {
                if (!(pages[i] is Dictionary<string, object> page))
                {
                    continue;
                }

                Dictionary<string, object> guid = GuidFor(FigJson.Str(page, "id"));
                var change = new Dictionary<string, object>
                {
                    ["guid"] = guid,
                    ["phase"] = "CREATED",
                    ["parentIndex"] = Parent(root, i),
                    ["type"] = "CANVAS",
                    ["name"] = FigJson.Str(page, "name") ?? "Page " + (i + 1),
                    ["visible"] = true,
                };

                if (FigJson.Obj(page, "backgroundColor") is Dictionary<string, object> bg)
                {
                    change["backgroundColor"] = ColorOf(bg, 1f);
                }

                nodes.Add(change);
                Children(page, guid, FigAffine.Identity, FigAffine.Identity);
            }
        }

        private void Children(Dictionary<string, object> parent, Dictionary<string, object> parentGuid, FigAffine parentWorld, FigAffine frameWorld)
        {
            List<object> children = FigJson.Arr(parent, "children");
            if (children == null)
            {
                return;
            }

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is Dictionary<string, object> child)
                {
                    Layer(child, parentGuid, i, parentWorld, frameWorld);
                }
            }
        }

        private void Layer(Dictionary<string, object> j, Dictionary<string, object> parentGuid, int index, FigAffine parentWorld, FigAffine frameWorld)
        {
            string restType = FigJson.Str(j, "type") ?? "FRAME";
            bool hasChildren = FigJson.Arr(j, "children")?.Count > 0;
            string type = MapType(restType, hasChildren);
            Dictionary<string, object> guid = GuidFor(FigJson.Str(j, "id"));
            var c = new Dictionary<string, object>
            {
                ["guid"] = guid,
                ["phase"] = "CREATED",
                ["parentIndex"] = Parent(parentGuid, index),
                ["type"] = type,
                ["name"] = FigJson.Str(j, "name") ?? "",
                ["visible"] = FigJson.Bool(j, "visible") ?? true,
                ["locked"] = FigJson.Bool(j, "locked") ?? false,
                ["opacity"] = Mathf.Clamp01(FigJson.Float(j, "opacity", 1f)),
            };

            if (FigJson.Str(j, "blendMode") is string blend && IsEnum("BlendMode", blend))
            {
                c["blendMode"] = blend;
            }

            if (restType == "COMPONENT_SET")
            {
                c["isStateGroup"] = true;
            }

            // Placement: the file's own transform, in the space of the parent layer.
            Vector2 size = SizeOf(j);
            FigAffine world = World(j, size, parentWorld, frameWorld);
            FigAffine local = parentWorld.Inverse().Then(world);
            c["size"] = Vec(size.x, size.y);
            c["transform"] = Matrix(local);

            if (type != "GROUP")
            {
                List<object> fills = Paints(FigJson.Arr(j, "fills"));
                if (fills != null)
                {
                    c["fillPaints"] = fills;
                }
            }

            Strokes(j, c);
            Effects(j, c);
            Corners(j, c);
            Constraints(j, c);
            if (type == "FRAME" || type == "SYMBOL" || type == "SECTION")
            {
                c["frameMaskDisabled"] = !(FigJson.Bool(j, "clipsContent") ?? false);
                AutoLayout(j, c);
            }

            ChildLayout(j, c);
            if (FigJson.Bool(j, "isMask") == true)
            {
                c["mask"] = true;
                string mt = FigJson.Str(j, "maskType");
                c["maskType"] = mt == "VECTOR" ? "OUTLINE" : mt == "LUMINANCE" ? "LUMINANCE" : "ALPHA";
            }

            List<object> fillGeometry = Geometry(FigJson.Arr(j, "fillGeometry"));
            if (fillGeometry != null && type != "TEXT")
            {
                c["fillGeometry"] = fillGeometry;
            }

            List<object> strokeGeometry = Geometry(FigJson.Arr(j, "strokeGeometry"));
            if (strokeGeometry != null && type != "TEXT")
            {
                c["strokeGeometry"] = strokeGeometry;
            }

            if (type == "BOOLEAN_OPERATION")
            {
                string op = FigJson.Str(j, "booleanOperation");
                c["booleanOperation"] = op == "EXCLUDE" ? "XOR" : IsEnum("BooleanOperation", op) ? op : "UNION";
            }

            if (type == "TEXT")
            {
                Text(j, c);
            }

            if (restType == "INSTANCE")
            {
                Warn("Instances are imported as frames with their resolved content (the REST API has no override data).");
            }

            nodes.Add(c);
            bool frameLike = type == "FRAME" || type == "SYMBOL" || type == "SECTION";
            Children(j, guid, world, frameLike ? world : frameWorld);
        }

        private string MapType(string restType, bool hasChildren)
        {
            switch (restType)
            {
                case "FRAME":
                case "GROUP":
                case "VECTOR":
                case "BOOLEAN_OPERATION":
                case "STAR":
                case "LINE":
                case "ELLIPSE":
                case "REGULAR_POLYGON":
                case "RECTANGLE":
                case "TEXT":
                case "SLICE":
                case "SECTION":
                    return restType;
                case "TEXT_PATH":
                    Warn("Text on a path is imported as plain text.");
                    return "TEXT";
                case "COMPONENT":
                    return "SYMBOL";
                case "COMPONENT_SET":
                case "INSTANCE":
                    return "FRAME";
                default:
                    Warn($"{restType} layers are imported as {(hasChildren ? "frames" : "shapes")}.");
                    return hasChildren ? "FRAME" : "VECTOR";
            }
        }

        // ─── Placement ───────────────────────────────────────────────────────────

        private static Vector2 SizeOf(Dictionary<string, object> j)
        {
            Dictionary<string, object> s = FigJson.Obj(j, "size");
            if (s != null)
            {
                return new Vector2(Mathf.Max(0, FigJson.Float(s, "x", 0)), Mathf.Max(0, FigJson.Float(s, "y", 0)));
            }

            Dictionary<string, object> b = FigJson.Obj(j, "absoluteBoundingBox");
            return b != null ? new Vector2(FigJson.Float(b, "width", 0), FigJson.Float(b, "height", 0)) : Vector2.zero;
        }

        /// <summary>
        /// The layer's page transform. REST places children of groups and booleans in the enclosing
        /// frame's space; the absolute bounds settle which space a layer's transform is in.
        /// </summary>
        private static FigAffine World(Dictionary<string, object> j, Vector2 size, FigAffine parentWorld, FigAffine frameWorld)
        {
            Dictionary<string, object> bbox = FigJson.Obj(j, "absoluteBoundingBox");
            List<object> rel = FigJson.Arr(j, "relativeTransform");
            if (rel != null && rel.Count == 2 && rel[0] is List<object> r0 && rel[1] is List<object> r1 && r0.Count == 3 && r1.Count == 3)
            {
                var m = new FigAffine { A = F(r0[0]), B = F(r0[1]), C = F(r0[2]), D = F(r1[0]), E = F(r1[1]), F = F(r1[2]) };
                FigAffine inFrame = frameWorld.Then(m), inParent = parentWorld.Then(m);
                if (bbox == null)
                {
                    return inFrame;
                }

                var box = new Rect(FigJson.Float(bbox, "x", 0), FigJson.Float(bbox, "y", 0), FigJson.Float(bbox, "width", 0), FigJson.Float(bbox, "height", 0));
                return Error(inParent, size, box) < Error(inFrame, size, box) ? inParent : inFrame;
            }

            if (bbox != null)
            {
                return new FigAffine { A = 1, E = 1, C = FigJson.Float(bbox, "x", 0), F = FigJson.Float(bbox, "y", 0) };
            }

            return parentWorld;
        }

        private static float Error(FigAffine world, Vector2 size, Rect box)
        {
            Rect r = FigGeometry.TransformBounds(world, new Rect(0, 0, size.x, size.y));
            return Mathf.Abs(r.xMin - box.xMin) + Mathf.Abs(r.yMin - box.yMin) + Mathf.Abs(r.xMax - box.xMax) + Mathf.Abs(r.yMax - box.yMax);
        }

        private static float F(object o) => o is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? (float)d : 0f;

        // ─── Paint ───────────────────────────────────────────────────────────────

        private List<object> Paints(List<object> list)
        {
            if (list == null)
            {
                return null;
            }

            var paints = new List<object>();
            foreach (object o in list)
            {
                if (o is Dictionary<string, object> p && Paint(p) is Dictionary<string, object> paint)
                {
                    paints.Add(paint);
                }
            }

            return paints;
        }

        private Dictionary<string, object> Paint(Dictionary<string, object> p)
        {
            string type = FigJson.Str(p, "type") ?? "SOLID";
            var paint = new Dictionary<string, object>
            {
                ["opacity"] = Mathf.Clamp01(FigJson.Float(p, "opacity", 1f)),
                ["visible"] = FigJson.Bool(p, "visible") ?? true,
                ["blendMode"] = FigJson.Str(p, "blendMode") is string b && IsEnum("BlendMode", b) ? b : "NORMAL",
            };

            switch (type)
            {
                case "SOLID":
                    paint["type"] = "SOLID";
                    paint["color"] = ColorOf(FigJson.Obj(p, "color"), 1f);
                    return paint;
                case "GRADIENT_LINEAR":
                case "GRADIENT_RADIAL":
                case "GRADIENT_ANGULAR":
                case "GRADIENT_DIAMOND":
                {
                    paint["type"] = type;
                    var stops = new List<object>();
                    foreach (object s in FigJson.Arr(p, "gradientStops") ?? new List<object>())
                    {
                        stops.Add(new Dictionary<string, object>
                        {
                            ["color"] = ColorOf(FigJson.Obj(s, "color"), 1f),
                            ["position"] = Mathf.Clamp01(FigJson.Float(s, "position", 0)),
                        });
                    }

                    paint["stops"] = stops;
                    paint["transform"] = Matrix(GradientTransform(type, FigJson.Arr(p, "gradientHandlePositions")));
                    return paint;
                }
                case "IMAGE":
                {
                    string imageRef = FigJson.Str(p, "imageRef");
                    if (string.IsNullOrEmpty(imageRef))
                    {
                        Warn("Image fills without an image (videos, GIF frames) were left out.");
                        return null;
                    }

                    byte[] hash = imageHashes.TryGetValue(imageRef, out byte[] h) ? h : SafeHex(imageRef);
                    if (!imageHashes.ContainsKey(imageRef))
                    {
                        Warn("Some images couldn't be downloaded; they draw as grey placeholders.");
                    }

                    paint["type"] = "IMAGE";
                    paint["image"] = new Dictionary<string, object> { ["hash"] = hash, ["name"] = "" };
                    string mode = FigJson.Str(p, "scaleMode") ?? "FILL";
                    paint["imageScaleMode"] = IsEnum("ImageScaleMode", mode) ? mode : "FILL";
                    paint["scale"] = FigJson.Float(p, "scalingFactor", 1f);
                    paint["rotation"] = FigJson.Float(p, "rotation", 0f);
                    List<object> t = FigJson.Arr(p, "imageTransform");
                    if (mode == "STRETCH" && t != null && t.Count == 2 && t[0] is List<object> a && t[1] is List<object> c && a.Count == 3 && c.Count == 3)
                    {
                        paint["transform"] = Matrix(new FigAffine { A = F(a[0]), B = F(a[1]), C = F(a[2]), D = F(c[0]), E = F(c[1]), F = F(c[2]) });
                    }

                    return paint;
                }
                default:
                    Warn($"{type} paints were left out.");
                    return null;
            }
        }

        private static byte[] SafeHex(string hex)
        {
            try
            {
                return FigUtil.FromHex(hex);
            }
            catch (FormatException)
            {
                return FigUtil.Sha1(Encoding.UTF8.GetBytes(hex));
            }
        }

        /// <summary>
        /// The paint transform (unit box → gradient space) from REST's handles (in the unit box):
        /// a linear gradient runs from (0, ½) to (1, ½) in gradient space and the third handle sits at
        /// (0, 1); the others are centred on (½, ½) with handles at (1, ½) and (½, 1) — like figcraft.
        /// </summary>
        private static FigAffine GradientTransform(string type, List<object> handles)
        {
            Vector2 h0 = new Vector2(0, 0.5f), h1 = new Vector2(1, 0.5f), h2 = new Vector2(0, 1);
            bool linear = type == "GRADIENT_LINEAR";
            if (!linear)
            {
                h0 = new Vector2(0.5f, 0.5f);
                h1 = new Vector2(1, 0.5f);
                h2 = new Vector2(0.5f, 1);
            }

            if (handles != null && handles.Count >= 2)
            {
                h0 = new Vector2(FigJson.Float(handles[0], "x", h0.x), FigJson.Float(handles[0], "y", h0.y));
                h1 = new Vector2(FigJson.Float(handles[1], "x", h1.x), FigJson.Float(handles[1], "y", h1.y));
                h2 = handles.Count >= 3
                    ? new Vector2(FigJson.Float(handles[2], "x", h2.x), FigJson.Float(handles[2], "y", h2.y))
                    // Without the width handle: perpendicular to the main axis, half its length (linear) or the same (others).
                    : h0 + new Vector2(-(h1.y - h0.y), h1.x - h0.x) * (linear ? 0.5f : 1f);
            }

            // M maps gradient space to the unit box; the paint transform is its inverse.
            FigAffine m;
            if (linear)
            {
                Vector2 a = h1 - h0, b = (h2 - h0) * 2f;
                Vector2 c = h0 - b * 0.5f;
                m = new FigAffine { A = a.x, B = b.x, C = c.x, D = a.y, E = b.y, F = c.y };
            }
            else
            {
                Vector2 a = (h1 - h0) * 2f, b = (h2 - h0) * 2f;
                Vector2 c = h0 - a * 0.5f - b * 0.5f;
                m = new FigAffine { A = a.x, B = b.x, C = c.x, D = a.y, E = b.y, F = c.y };
            }

            float det = m.A * m.E - m.B * m.D;
            return Mathf.Abs(det) < 1e-9f ? FigAffine.Identity : m.Inverse();
        }

        private static Dictionary<string, object> ColorOf(Dictionary<string, object> c, float alpha)
        {
            return new Dictionary<string, object>
            {
                ["r"] = FigJson.Float(c, "r", 0),
                ["g"] = FigJson.Float(c, "g", 0),
                ["b"] = FigJson.Float(c, "b", 0),
                ["a"] = FigJson.Float(c, "a", 1) * alpha,
            };
        }

        private void Strokes(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            List<object> strokes = Paints(FigJson.Arr(j, "strokes"));
            if (strokes == null || strokes.Count == 0)
            {
                return;
            }

            c["strokePaints"] = strokes;
            c["strokeWeight"] = FigJson.Float(j, "strokeWeight", 1f);
            string align = FigJson.Str(j, "strokeAlign");
            c["strokeAlign"] = IsEnum("StrokeAlign", align) ? align : "CENTER";
            string join = FigJson.Str(j, "strokeJoin");
            if (IsEnum("StrokeJoin", join))
            {
                c["strokeJoin"] = join;
            }

            string cap = FigJson.Str(j, "strokeCap");
            if (cap == "ROUND" || cap == "SQUARE" || cap == "NONE")
            {
                c["strokeCap"] = cap;
            }

            if (FigJson.Arr(j, "strokeDashes") is List<object> dashes && dashes.Count > 0)
            {
                c["dashPattern"] = dashes.ConvertAll(d => (object)F(d));
            }

            if (FigJson.Obj(j, "individualStrokeWeights") is Dictionary<string, object> w)
            {
                c["borderStrokeWeightsIndependent"] = true;
                c["borderTopWeight"] = FigJson.Float(w, "top", 0);
                c["borderRightWeight"] = FigJson.Float(w, "right", 0);
                c["borderBottomWeight"] = FigJson.Float(w, "bottom", 0);
                c["borderLeftWeight"] = FigJson.Float(w, "left", 0);
            }
        }

        private void Effects(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            List<object> list = FigJson.Arr(j, "effects");
            if (list == null || list.Count == 0)
            {
                return;
            }

            var effects = new List<object>();
            foreach (object o in list)
            {
                string type = FigJson.Str(o, "type");
                string kind = type == "LAYER_BLUR" ? "FOREGROUND_BLUR" : type;
                if (kind != "DROP_SHADOW" && kind != "INNER_SHADOW" && kind != "FOREGROUND_BLUR" && kind != "BACKGROUND_BLUR")
                {
                    Warn($"{type} effects were left out.");
                    continue;
                }

                var e = new Dictionary<string, object>
                {
                    ["type"] = kind,
                    ["radius"] = FigJson.Float(o, "radius", 0),
                    ["visible"] = FigJson.Bool(o, "visible") ?? true,
                };

                if (kind == "DROP_SHADOW" || kind == "INNER_SHADOW")
                {
                    e["color"] = ColorOf(FigJson.Obj(o, "color"), 1f);
                    Dictionary<string, object> off = FigJson.Obj(o, "offset");
                    e["offset"] = Vec(FigJson.Float(off, "x", 0), FigJson.Float(off, "y", 0));
                    e["spread"] = FigJson.Float(o, "spread", 0);
                    e["blendMode"] = FigJson.Str(o, "blendMode") is string b && IsEnum("BlendMode", b) ? b : "NORMAL";
                    e["showShadowBehindNode"] = FigJson.Bool(o, "showShadowBehindNode") ?? false;
                }

                effects.Add(e);
            }

            if (effects.Count > 0)
            {
                c["effects"] = effects;
            }
        }

        private static void Corners(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            if (FigJson.Num(j, "cornerRadius").HasValue)
            {
                c["cornerRadius"] = FigJson.Float(j, "cornerRadius", 0);
            }

            if (FigJson.Arr(j, "rectangleCornerRadii") is List<object> radii && radii.Count == 4)
            {
                c["rectangleCornerRadiiIndependent"] = true;
                c["rectangleTopLeftCornerRadius"] = F(radii[0]);
                c["rectangleTopRightCornerRadius"] = F(radii[1]);
                c["rectangleBottomRightCornerRadius"] = F(radii[2]);
                c["rectangleBottomLeftCornerRadius"] = F(radii[3]);
            }

            if (FigJson.Num(j, "cornerSmoothing").HasValue)
            {
                c["cornerSmoothing"] = FigJson.Float(j, "cornerSmoothing", 0);
            }
        }

        private static void Constraints(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            Dictionary<string, object> k = FigJson.Obj(j, "constraints");
            if (k == null)
            {
                return;
            }

            c["horizontalConstraint"] = Constraint(FigJson.Str(k, "horizontal"));
            c["verticalConstraint"] = Constraint(FigJson.Str(k, "vertical"));
        }

        private static string Constraint(string rest)
        {
            switch (rest)
            {
                case "RIGHT":
                case "BOTTOM":
                    return "MAX";
                case "CENTER":
                    return "CENTER";
                case "LEFT_RIGHT":
                case "TOP_BOTTOM":
                    return "STRETCH";
                case "SCALE":
                    return "SCALE";
                default:
                    return "MIN";
            }
        }

        private static void AutoLayout(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            string mode = FigJson.Str(j, "layoutMode");
            if (mode != "HORIZONTAL" && mode != "VERTICAL")
            {
                return;
            }

            c["stackMode"] = mode;
            c["stackSpacing"] = FigJson.Float(j, "itemSpacing", 0);
            c["stackCounterSpacing"] = FigJson.Float(j, "counterAxisSpacing", 0);
            c["stackHorizontalPadding"] = FigJson.Float(j, "paddingLeft", 0);
            c["stackVerticalPadding"] = FigJson.Float(j, "paddingTop", 0);
            c["stackPaddingRight"] = FigJson.Float(j, "paddingRight", 0);
            c["stackPaddingBottom"] = FigJson.Float(j, "paddingBottom", 0);
            string primary = FigJson.Str(j, "primaryAxisAlignItems");
            c["stackPrimaryAlignItems"] = primary == "CENTER" || primary == "MAX" || primary == "SPACE_BETWEEN" ? primary : "MIN";
            string counter = FigJson.Str(j, "counterAxisAlignItems");
            c["stackCounterAlignItems"] = counter == "CENTER" || counter == "MAX" || counter == "BASELINE" ? counter : "MIN";
            c["stackPrimarySizing"] = FigJson.Str(j, "primaryAxisSizingMode") == "AUTO" ? "RESIZE_TO_FIT" : "FIXED";
            c["stackCounterSizing"] = FigJson.Str(j, "counterAxisSizingMode") == "AUTO" ? "RESIZE_TO_FIT" : "FIXED";
            if (FigJson.Str(j, "layoutWrap") == "WRAP")
            {
                c["stackWrap"] = "WRAP";
            }

            if (FigJson.Bool(j, "itemReverseZIndex") == true)
            {
                c["stackReverseZIndex"] = true;
            }

            if (FigJson.Bool(j, "strokesIncludedInLayout") == true)
            {
                c["bordersTakeSpace"] = true;
            }
        }

        private static void ChildLayout(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            if (FigJson.Str(j, "layoutPositioning") == "ABSOLUTE")
            {
                c["stackPositioning"] = "ABSOLUTE";
            }

            if (FigJson.Float(j, "layoutGrow", 0) > 0)
            {
                c["stackChildPrimaryGrow"] = FigJson.Float(j, "layoutGrow", 0);
            }

            if (FigJson.Str(j, "layoutAlign") == "STRETCH")
            {
                c["stackChildAlignSelf"] = "STRETCH";
            }
        }

        // ─── Geometry ────────────────────────────────────────────────────────────

        private List<object> Geometry(List<object> list)
        {
            if (list == null || list.Count == 0)
            {
                return null;
            }

            var paths = new List<object>();
            foreach (object o in list)
            {
                byte[] blob = SvgPathBlob(FigJson.Str(o, "path"));
                if (blob == null)
                {
                    continue;
                }

                blobs.Add(new Dictionary<string, object> { ["bytes"] = blob });
                paths.Add(new Dictionary<string, object>
                {
                    ["windingRule"] = FigJson.Str(o, "windingRule") == "EVENODD" ? "ODD" : "NONZERO",
                    ["commandsBlob"] = (uint)(blobBase + blobs.Count - 1),
                    ["styleID"] = 0u,
                });
            }

            return paths.Count > 0 ? paths : null;
        }

        /// <summary>
        /// An SVG path (REST geometry: absolute M / L / Q / C / Z; relative commands and H / V are
        /// accepted too) as a Figma path blob: command bytes 1 move, 2 line, 3 quadratic, 4 cubic,
        /// 0 close, each followed by its points as little-endian floats.
        /// </summary>
        public static byte[] SvgPathBlob(string d)
        {
            if (string.IsNullOrEmpty(d))
            {
                return null;
            }

            using var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int pos = 0;
            char cmd = ' ';
            Vector2 cur = Vector2.zero, start = Vector2.zero;
            bool any = false;
            while (true)
            {
                SkipSeparators(d, ref pos);
                if (pos >= d.Length)
                {
                    break;
                }

                char ch = d[pos];
                if (char.IsLetter(ch) && ch != 'e' && ch != 'E')
                {
                    cmd = ch;
                    pos++;
                    if (cmd == 'Z' || cmd == 'z')
                    {
                        w.Write((byte)0);
                        cur = start;
                        continue;
                    }
                }
                else if (cmd == ' ')
                {
                    return null;
                }

                bool rel = char.IsLower(cmd);
                Vector2 o = rel ? cur : Vector2.zero;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        Vector2 p = o + ReadPoint(d, ref pos);
                        w.Write((byte)1);
                        Write(w, p);
                        cur = start = p;
                        // Further pairs after a move are lines.
                        cmd = rel ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                    {
                        Vector2 p = o + ReadPoint(d, ref pos);
                        w.Write((byte)2);
                        Write(w, p);
                        cur = p;
                        break;
                    }
                    case 'H':
                    {
                        float x = ReadNumber(d, ref pos) + (rel ? cur.x : 0);
                        cur = new Vector2(x, cur.y);
                        w.Write((byte)2);
                        Write(w, cur);
                        break;
                    }
                    case 'V':
                    {
                        float y = ReadNumber(d, ref pos) + (rel ? cur.y : 0);
                        cur = new Vector2(cur.x, y);
                        w.Write((byte)2);
                        Write(w, cur);
                        break;
                    }
                    case 'Q':
                    {
                        Vector2 a = o + ReadPoint(d, ref pos), p = o + ReadPoint(d, ref pos);
                        w.Write((byte)3);
                        Write(w, a);
                        Write(w, p);
                        cur = p;
                        break;
                    }
                    case 'C':
                    {
                        Vector2 a = o + ReadPoint(d, ref pos), b = o + ReadPoint(d, ref pos), p = o + ReadPoint(d, ref pos);
                        w.Write((byte)4);
                        Write(w, a);
                        Write(w, b);
                        Write(w, p);
                        cur = p;
                        break;
                    }
                    default:
                        // Arcs and the rest don't appear in REST geometry.
                        return null;
                }

                any = true;
            }

            w.Flush();
            return any ? ms.ToArray() : null;
        }

        private static void Write(BinaryWriter w, Vector2 p)
        {
            w.Write(p.x);
            w.Write(p.y);
        }

        private static void SkipSeparators(string d, ref int pos)
        {
            while (pos < d.Length && (d[pos] == ' ' || d[pos] == ',' || d[pos] == '\n' || d[pos] == '\t' || d[pos] == '\r'))
            {
                pos++;
            }
        }

        private static Vector2 ReadPoint(string d, ref int pos)
        {
            float x = ReadNumber(d, ref pos);
            float y = ReadNumber(d, ref pos);
            return new Vector2(x, y);
        }

        private static float ReadNumber(string d, ref int pos)
        {
            SkipSeparators(d, ref pos);
            int start = pos;
            if (pos < d.Length && (d[pos] == '-' || d[pos] == '+'))
            {
                pos++;
            }

            while (pos < d.Length && (char.IsDigit(d[pos]) || d[pos] == '.' ||
                                      ((d[pos] == 'e' || d[pos] == 'E') && pos + 1 < d.Length && (char.IsDigit(d[pos + 1]) || d[pos + 1] == '-' || d[pos + 1] == '+'))))
            {
                if (d[pos] == 'e' || d[pos] == 'E')
                {
                    pos++;
                }

                pos++;
            }

            if (pos == start || !float.TryParse(d.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                throw new FormatException("Bad number in SVG path at " + start);
            }

            return v;
        }

        // ─── Text ────────────────────────────────────────────────────────────────

        private void Text(Dictionary<string, object> j, Dictionary<string, object> c)
        {
            string characters = FigJson.Str(j, "characters") ?? "";
            Dictionary<string, object> style = FigJson.Obj(j, "style") ?? new Dictionary<string, object>();
            TextStyle(style, c, true, null);
            string baseFamily = FigJson.Str(style, "fontFamily") ?? "Inter";

            // One line entry per paragraph, like Figma writes.
            var lines = new List<object>();
            int paragraphs = 1;
            foreach (char ch in characters)
            {
                if (ch == '\n')
                {
                    paragraphs++;
                }
            }

            for (int i = 0; i < paragraphs; i++)
            {
                lines.Add(new Dictionary<string, object>
                {
                    ["lineType"] = "PLAIN",
                    ["styleId"] = 0,
                    ["indentationLevel"] = 0,
                    ["sourceDirectionality"] = "AUTO",
                    ["listStartOffset"] = 0,
                    ["isFirstLineOfList"] = false,
                });
            }

            var textData = new Dictionary<string, object> { ["characters"] = characters, ["lines"] = lines };

            // Per-character styles: REST lists a style id per character and a table of the overrides.
            List<object> ids = FigJson.Arr(j, "characterStyleOverrides");
            Dictionary<string, object> table = FigJson.Obj(j, "styleOverrideTable");
            if (ids != null && ids.Count > 0 && table != null && table.Count > 0)
            {
                textData["characterStyleIDs"] = ids.ConvertAll(o => (object)(uint)Math.Max(0, F(o)));
                var overrides = new List<object>();
                foreach (KeyValuePair<string, object> pair in table)
                {
                    if (!uint.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) || !(pair.Value is Dictionary<string, object> s))
                    {
                        continue;
                    }

                    var entry = new Dictionary<string, object> { ["styleID"] = id };
                    TextStyle(s, entry, false, baseFamily);
                    if (Paints(FigJson.Arr(s, "fills")) is List<object> fills)
                    {
                        entry["fillPaints"] = fills;
                    }

                    overrides.Add(entry);
                }

                textData["styleOverrideTable"] = overrides;
            }

            c["textData"] = textData;
        }

        /// <summary>REST type style → text fields; <paramref name="full"/> fills in defaults for the layer's own style.</summary>
        private static void TextStyle(Dictionary<string, object> s, Dictionary<string, object> c, bool full, string baseFamily)
        {
            string family = FigJson.Str(s, "fontFamily");
            // An override of only the weight or slant still names a whole font: the layer's family in that style.
            bool fontChanged = family != null || FigJson.Has(s, "fontWeight") || FigJson.Has(s, "italic") || FigJson.Has(s, "fontStyle") || FigJson.Has(s, "fontPostScriptName");
            family = family ?? baseFamily;
            if (fontChanged || full)
            {
                c["fontName"] = new Dictionary<string, object>
                {
                    ["family"] = family ?? "Inter",
                    ["style"] = FontStyle(s),
                    ["postscript"] = FigJson.Str(s, "fontPostScriptName") ?? "",
                };
            }

            if (FigJson.Num(s, "fontSize").HasValue || full)
            {
                c["fontSize"] = FigJson.Float(s, "fontSize", 12f);
            }

            string h = FigJson.Str(s, "textAlignHorizontal");
            if (h == "LEFT" || h == "CENTER" || h == "RIGHT" || h == "JUSTIFIED")
            {
                c["textAlignHorizontal"] = h;
            }

            string v = FigJson.Str(s, "textAlignVertical");
            if (v == "TOP" || v == "CENTER" || v == "BOTTOM")
            {
                c["textAlignVertical"] = v;
            }

            string resize = FigJson.Str(s, "textAutoResize");
            if (resize == "WIDTH_AND_HEIGHT" || resize == "HEIGHT" || resize == "NONE")
            {
                c["textAutoResize"] = resize;
            }
            else if (resize == "TRUNCATE")
            {
                c["textAutoResize"] = "NONE";
                c["textTruncation"] = "ENDING";
            }

            if (FigJson.Num(s, "letterSpacing").HasValue)
            {
                c["letterSpacing"] = new Dictionary<string, object> { ["value"] = FigJson.Float(s, "letterSpacing", 0), ["units"] = "PIXELS" };
            }

            string unit = FigJson.Str(s, "lineHeightUnit");
            if (unit == "PIXELS" && FigJson.Num(s, "lineHeightPx").HasValue)
            {
                c["lineHeight"] = new Dictionary<string, object> { ["value"] = FigJson.Float(s, "lineHeightPx", 0), ["units"] = "PIXELS" };
            }
            else if (unit == "FONT_SIZE_%" && FigJson.Num(s, "lineHeightPercentFontSize").HasValue)
            {
                c["lineHeight"] = new Dictionary<string, object> { ["value"] = FigJson.Float(s, "lineHeightPercentFontSize", 100), ["units"] = "PERCENT" };
            }

            string textCase = FigJson.Str(s, "textCase");
            if (textCase == "UPPER" || textCase == "LOWER" || textCase == "TITLE" || textCase == "SMALL_CAPS")
            {
                c["textCase"] = textCase;
            }

            string decoration = FigJson.Str(s, "textDecoration");
            if (decoration == "UNDERLINE" || decoration == "STRIKETHROUGH")
            {
                c["textDecoration"] = decoration;
            }

            if (FigJson.Num(s, "paragraphSpacing").HasValue)
            {
                c["paragraphSpacing"] = FigJson.Float(s, "paragraphSpacing", 0);
            }

            if (FigJson.Num(s, "paragraphIndent").HasValue)
            {
                c["paragraphIndent"] = FigJson.Float(s, "paragraphIndent", 0);
            }
        }

        /// <summary>The font's style name ("Bold Italic"): REST's own when given, else from the weight.</summary>
        private static string FontStyle(Dictionary<string, object> s)
        {
            string named = FigJson.Str(s, "fontStyle");
            if (!string.IsNullOrEmpty(named))
            {
                return named;
            }

            int weight = Mathf.RoundToInt(FigJson.Float(s, "fontWeight", 400) / 100f) * 100;
            string name = weight <= 100 ? "Thin" : weight <= 200 ? "ExtraLight" : weight <= 300 ? "Light" : weight <= 400 ? "Regular" :
                weight <= 500 ? "Medium" : weight <= 600 ? "SemiBold" : weight <= 700 ? "Bold" : weight <= 800 ? "ExtraBold" : "Black";
            if (FigJson.Bool(s, "italic") == true)
            {
                return name == "Regular" ? "Italic" : name + " Italic";
            }

            return name;
        }

        // ─── Identity and order ──────────────────────────────────────────────────

        private Dictionary<string, object> GuidFor(string id)
        {
            // Plain ids are "session:local"; ids inside instances ("I1:2;3:4") get fresh ones.
            if (id != null)
            {
                int colon = id.IndexOf(':');
                if (colon > 0 && uint.TryParse(id.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out uint session) &&
                    uint.TryParse(id.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint local))
                {
                    return Guid(session, local);
                }
            }

            return Guid(FreshSession, freshLocal++);
        }

        private static Dictionary<string, object> Guid(uint session, uint local)
        {
            return new Dictionary<string, object> { ["sessionID"] = session, ["localID"] = local };
        }

        /// <summary>A position string that sorts siblings by <paramref name="index"/> (three printable characters).</summary>
        private static Dictionary<string, object> Parent(Dictionary<string, object> parent, int index)
        {
            var chars = new char[3];
            int n = index;
            for (int i = 2; i >= 0; i--)
            {
                chars[i] = (char)('!' + n % 90);
                n /= 90;
            }

            return new Dictionary<string, object> { ["guid"] = parent, ["position"] = new string(chars) };
        }

        private static Dictionary<string, object> Vec(float x, float y)
        {
            return new Dictionary<string, object> { ["x"] = x, ["y"] = y };
        }

        private static Dictionary<string, object> Matrix(FigAffine m)
        {
            return new Dictionary<string, object> { ["m00"] = m.A, ["m01"] = m.B, ["m02"] = m.C, ["m10"] = m.D, ["m11"] = m.E, ["m12"] = m.F };
        }

        private bool IsEnum(string enumName, string value)
        {
            return value != null && schema.Find(enumName)?.FieldByName(value) != null;
        }

        // ─── Plain values → kiwi ─────────────────────────────────────────────────

        /// <summary>A kiwi object of <paramref name="def"/> from a dictionary keyed by field name (structs get every field).</summary>
        private KiwiValue Object(KiwiDef def, Dictionary<string, object> plain)
        {
            foreach (string key in plain.Keys)
            {
                if (def.FieldByName(key) == null)
                {
                    throw new ArgumentException($"Figma import: {def.Name} has no field {key}");
                }
            }

            var members = new List<KiwiMember>();
            foreach (KiwiField f in def.Fields)
            {
                if (plain.TryGetValue(f.Name, out object v) && v != null)
                {
                    members.Add(new KiwiMember(f, Field(f, v)));
                }
                else if (def.Kind == KiwiDefKind.Struct)
                {
                    members.Add(new KiwiMember(f, KiwiSchema.DefaultField(f)));
                }
            }

            return KiwiValue.FromObject(def, members);
        }

        private KiwiValue Field(KiwiField f, object v)
        {
            if (!f.IsArray)
            {
                return Element(f, v);
            }

            if (f.Type == KiwiType.Byte)
            {
                return KiwiValue.FromBytes((byte[])v);
            }

            var items = new List<KiwiValue>();
            foreach (object o in (System.Collections.IList)v)
            {
                items.Add(Element(f, o));
            }

            return KiwiValue.FromArray(items);
        }

        private KiwiValue Element(KiwiField f, object v)
        {
            switch (f.Type)
            {
                case KiwiType.Bool:
                    return KiwiValue.FromBool((bool)v);
                case KiwiType.Byte:
                case KiwiType.Uint:
                case KiwiType.Uint64:
                    return KiwiValue.FromUint(Convert.ToUInt64(v, CultureInfo.InvariantCulture));
                case KiwiType.Int:
                case KiwiType.Int64:
                    return KiwiValue.FromInt(Convert.ToInt64(v, CultureInfo.InvariantCulture));
                case KiwiType.Float:
                    return KiwiValue.FromFloat(Convert.ToSingle(v, CultureInfo.InvariantCulture));
                case KiwiType.String:
                    return KiwiValue.FromString((string)v);
            }

            KiwiDef def = f.TypeDef;
            if (def.Kind == KiwiDefKind.Enum)
            {
                KiwiField item = def.FieldByName((string)v);
                if (item == null)
                {
                    throw new ArgumentException($"Figma import: {def.Name} has no value {v}");
                }

                return KiwiValue.FromEnum(def, item.Value);
            }

            return Object(def, (Dictionary<string, object>)v);
        }
    }
}
