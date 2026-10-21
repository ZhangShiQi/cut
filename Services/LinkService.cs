using System;
using System.Diagnostics;
using System.Linq;

namespace Cut.Services;

public static class LinkService
{
    public static Uri? GetWebUri(string text)
    {
        string value = text.Trim();
        if (value.Any(char.IsControl) || value.Contains('\\')) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
    internal static ProcessStartInfo CreateStartInfo(string text)
    {
        var uri = GetWebUri(text) ?? throw new InvalidOperationException("这条内容不是 HTTP 或 HTTPS 网页链接。");
        return new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
    }
    public static void Open(string text) => Process.Start(CreateStartInfo(text));
}
