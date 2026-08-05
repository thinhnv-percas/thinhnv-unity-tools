using System;
using System.IO;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Copies the source .fbx (and its .fbm embedded-media sidecar folder, if any)
    /// into a timestamped backup before every overwrite-save, so a bad round trip
    /// never destroys the only copy of the original file.
    /// </summary>
    public static class FbxBackupService
    {
        private const string BackupFolderName = "FbxToolBackups";

        public static string CreateBackup(string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Cannot back up a file that does not exist.", sourcePath);
            }

            var sourceDir = Path.GetDirectoryName(sourcePath) ?? ".";
            var backupRoot = Path.Combine(sourceDir, BackupFolderName);
            Directory.CreateDirectory(backupRoot);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = Path.GetExtension(sourcePath);
            var backupPath = Path.Combine(backupRoot, $"{fileName}.{timestamp}{extension}");

            File.Copy(sourcePath, backupPath, overwrite: false);

            var fbmSource = Path.Combine(sourceDir, fileName + ".fbm");
            if (Directory.Exists(fbmSource))
            {
                var fbmBackup = Path.Combine(backupRoot, $"{fileName}.{timestamp}.fbm");
                CopyDirectory(fbmSource, fbmBackup);
            }

            return backupPath;
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
            }
        }
    }
}
