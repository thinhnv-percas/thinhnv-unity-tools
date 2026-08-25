using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DreamCode.SmartImporter.Editor.IO
{
    /// <summary>
    /// Metadata for one asset entry inside a .unitypackage archive - enough to build an import
    /// selection list without Unity's own (removed) PackageUtility.ExtractAndPrepareAssetList.
    /// </summary>
    internal sealed class PackageEntry
    {
        internal string Guid;
        internal string ExportedAssetPath;
        internal bool IsFolder;
    }

    /// <summary>
    /// Where one archive entry's real content should be written on Import.
    /// </summary>
    internal sealed class PackageExtractionTarget
    {
        internal string AssetPath;
        internal bool IsFolder;
    }

    /// <summary>
    /// Reads and extracts a .unitypackage (a gzip-compressed tar) directly, with no dependency on
    /// Unity's internal package-import APIs - those were observed to change shape across Unity
    /// versions (UnityEditor.PackageUtility.ExtractAndPrepareAssetList was removed entirely by
    /// 6000.3.21f1), and even UnityEditor.PackageUtility.ImportPackageAssets/PackageImport's own
    /// native dialog no longer performs the actual file copy for an externally-built item list.
    /// So this tool reads and writes every byte itself.
    /// </summary>
    internal static class UnityPackageArchive
    {
        private const int BlockSize = 512;

        internal static List<PackageEntry> ReadEntries(string packagePath)
        {
            var entriesByGuid = new Dictionary<string, PackageEntry>();
            var guidsWithAssetContent = new HashSet<string>();

            ReadTar(packagePath, tarEntry =>
            {
                var slashIndex = tarEntry.Name.IndexOf('/');
                if (slashIndex <= 0)
                    return;

                var guid = tarEntry.Name.Substring(0, slashIndex);
                var member = tarEntry.Name.Substring(slashIndex + 1);

                if (!entriesByGuid.TryGetValue(guid, out var entry))
                {
                    entry = new PackageEntry { Guid = guid };
                    entriesByGuid.Add(guid, entry);
                }

                if (member == "pathname")
                    entry.ExportedAssetPath = ParsePathnameContent(tarEntry.Content);
                else if (member == "asset")
                    guidsWithAssetContent.Add(guid);
            });

            var result = new List<PackageEntry>(entriesByGuid.Count);
            foreach (var entry in entriesByGuid.Values)
            {
                if (string.IsNullOrEmpty(entry.ExportedAssetPath))
                    continue;
                entry.IsFolder = !guidsWithAssetContent.Contains(entry.Guid);
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// A "pathname" entry is meant to be a single line of text, but some real-world packages
        /// carry trailing bytes after it within the same declared content length (observed: a
        /// literal "\n00" tail on every entry of one third-party package) - a plain Trim only
        /// strips characters at the very ends, so it leaves that tail in place and corrupts the
        /// destination path. Cutting at the first newline is robust to that regardless of cause.
        /// </summary>
        private static string ParsePathnameContent(byte[] content)
        {
            var text = Encoding.UTF8.GetString(content);
            var newlineIndex = text.IndexOf('\n');
            if (newlineIndex >= 0)
                text = text.Substring(0, newlineIndex);
            return text.Trim('\r', '\0', ' ');
        }

        /// <summary>
        /// Writes the real "asset" and "asset.meta" bytes for every requested guid straight to
        /// <see cref="PackageExtractionTarget.AssetPath"/> (project-relative, e.g. "Assets/Foo/Bar.mat").
        /// Folder targets are created up front since a folder-only entry has no "asset" tar member.
        /// </summary>
        internal static void ExtractAssets(string packagePath, Dictionary<string, PackageExtractionTarget> targetsByGuid)
        {
            foreach (var target in targetsByGuid.Values)
            {
                if (target.IsFolder)
                    Directory.CreateDirectory(Path.GetFullPath(target.AssetPath));
            }

            ReadTar(packagePath, tarEntry =>
            {
                var slashIndex = tarEntry.Name.IndexOf('/');
                if (slashIndex <= 0)
                    return;

                var guid = tarEntry.Name.Substring(0, slashIndex);
                var member = tarEntry.Name.Substring(slashIndex + 1);

                if (!targetsByGuid.TryGetValue(guid, out var target))
                    return;

                if (member == "asset" && !target.IsFolder)
                    WriteFile(target.AssetPath, tarEntry.Content);
                else if (member == "asset.meta")
                    WriteFile(target.AssetPath + ".meta", tarEntry.Content);
            });
        }

        private static void WriteFile(string assetRelativePath, byte[] content)
        {
            var fullPath = Path.GetFullPath(assetRelativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(fullPath, content);
        }

        private struct TarEntry
        {
            internal string Name;
            internal byte[] Content;
        }

        private static void ReadTar(string packagePath, Action<TarEntry> onEntry)
        {
            using (var fileStream = File.OpenRead(packagePath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (var tarStream = new MemoryStream())
            {
                gzipStream.CopyTo(tarStream);
                tarStream.Position = 0;

                var header = new byte[BlockSize];
                string pendingLongName = null;
                var keepReading = true;

                while (keepReading)
                {
                    if (ReadFully(tarStream, header) < BlockSize || IsZeroBlock(header))
                    {
                        keepReading = false;
                        continue;
                    }

                    var name = ReadHeaderString(header, 0, 100);
                    var prefix = ReadHeaderString(header, 345, 155);
                    if (!string.IsNullOrEmpty(prefix))
                        name = prefix + "/" + name;
                    if (pendingLongName != null)
                    {
                        name = pendingLongName;
                        pendingLongName = null;
                    }

                    var sizeText = ReadHeaderString(header, 124, 12).Trim(' ');
                    var size = string.IsNullOrEmpty(sizeText) ? 0L : Convert.ToInt64(sizeText, 8);
                    var typeFlag = (char)header[156];

                    var content = ReadTarContent(tarStream, (int)size);

                    if (typeFlag == 'L')
                    {
                        // GNU long-name extension: content is the real name for the following header.
                        pendingLongName = Encoding.UTF8.GetString(content).TrimEnd('\0');
                        continue;
                    }

                    if (typeFlag == '5' || string.IsNullOrEmpty(name) || name.EndsWith("/"))
                        continue;

                    onEntry(new TarEntry { Name = name, Content = content });
                }
            }
        }

        private static byte[] ReadTarContent(Stream tarStream, int contentLength)
        {
            var content = contentLength > 0 ? new byte[contentLength] : Array.Empty<byte>();
            if (contentLength > 0)
                ReadFully(tarStream, content);

            var paddedLength = ((contentLength + BlockSize - 1) / BlockSize) * BlockSize;
            var padding = paddedLength - contentLength;
            if (padding > 0)
                tarStream.Seek(padding, SeekOrigin.Current);

            return content;
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                    break;
                offset += read;
            }
            return offset;
        }

        private static bool IsZeroBlock(byte[] block)
        {
            for (var i = 0; i < block.Length; i++)
                if (block[i] != 0)
                    return false;
            return true;
        }

        private static string ReadHeaderString(byte[] header, int offset, int length)
        {
            var end = offset;
            var max = offset + length;
            while (end < max && header[end] != 0)
                end++;
            return Encoding.ASCII.GetString(header, offset, end - offset);
        }
    }
}
