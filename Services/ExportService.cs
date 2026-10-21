using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Cut.Services;

public static class ExportService
{
    public static async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); return; }
            catch (COMException) when (attempt < 4) { await Task.Delay(80, cancellationToken); }
        }
    }
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
    public static async Task CopyAsync(BitmapSource image)
    {
        // Supply both the standard bitmap and PNG formats for common paste targets.
        using var png = new MemoryStream(EncodePng(image));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                png.Position = 0;
                var data = new DataObject();
                data.SetImage(image);
                data.SetData("PNG", png);
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException) when (attempt < 4) { await Task.Delay(80); }
        }
    }
    public static void SavePng(BitmapSource image, string path)
    {
        // Stage beside the destination so a failed encode/write preserves an existing file.
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, EncodePng(image)); File.Move(temporary, fullPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
