using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cut.UpdateEngine;
using Cut.Services;

namespace Cut.Updates;

// Optional read-only release token. DPAPI binds it to this Windows user. The HTTP
// client sends it only to the official repository's API, never to redirect hosts.
public static class UpdateCredentials
{
    private static string FilePath => Path.Combine(UpdatePaths.Data, "device", "release-token.bin");
    public static Task<string?> ReadAsync(bool interactive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)));
    }
    public static void Save(string value)
    {
        value = value.Trim();
        if (value.Length == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
        if (value.Length > 4096 || value.AnyInvalidKeyCharacter()) throw new ArgumentException("读取令牌格式无效。");
        DurableFiles.WriteBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser), backup: false);
    }
}
