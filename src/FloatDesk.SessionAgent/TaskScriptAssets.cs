using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.SessionAgent;

internal static class TaskScriptAssets
{
    public static string Materialize(Guid environmentId, IReadOnlyDictionary<string,string> parameters)
    {
        if (environmentId == Guid.Empty || !parameters.TryGetValue("scriptContent", out var content) || content.Length > 65536 ||
            !parameters.TryGetValue("scriptSha256", out var expected)) throw new InvalidOperationException("ScriptAssetInvalid");
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ScriptAssetHashMismatch");
        // Windows PowerShell 5.1 interprets BOM-less .ps1 files using the ANSI
        // code page. The asset digest still describes the original UTF-8 text.
        var fileBytes = new byte[bytes.Length + 3];
        fileBytes[0] = 0xEF; fileBytes[1] = 0xBB; fileBytes[2] = 0xBF;
        bytes.CopyTo(fileBytes, 3);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YueYuOrb", "task-scripts", environmentId.ToString("N"));
        for (var folder = new DirectoryInfo(root); folder is not null; folder = folder.Parent)
            if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("ScriptAssetReparsePoint");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, hash + ".utf8.ps1");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(fileBytes)))
                throw new InvalidOperationException("ScriptAssetCacheChanged");
        }
        else
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(fileBytes); file.Flush(true);
        }
        return path;
    }
}
