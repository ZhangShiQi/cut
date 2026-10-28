using System;
using System.IO;
using System.Text.Json;

namespace Cut.Services;

public sealed record HotkeySettings(uint Modifiers = 3, uint Key = 65)
{
    private static string FilePath => Path.Combine(Cut.UpdateEngine.UpdatePaths.Data, "settings.json");
    public static HotkeySettings Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(FilePath));
            if (result is not null && result.Modifiers is > 0 and <= 7 && result.Key is >= 65 and <= 90) return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
    public override string ToString() => ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") + ((Modifiers & 4) != 0 ? "Shift + " : "") + (char)Key;
}
