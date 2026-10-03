using System;
using System.IO;

namespace Radio808.Shared;

/// <summary>Appends errors to 808Radio.log in the settings folder (kept under ~1 MB).</summary>
public static class AppLog
{
    private static readonly object Lock = new();
    public static string FilePath { get; } = Path.Combine(AppSettings.Dir, "808Radio.log");

    public static void Write(Exception ex) => Write(ex.ToString());

    public static void Write(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
