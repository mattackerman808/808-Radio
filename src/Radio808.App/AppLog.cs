using System;
using System.IO;

namespace Radio808.App;

/// <summary>Appends errors to %APPDATA%\808Radio\808Radio.log (kept under ~1 MB).</summary>
internal static class AppLog
{
    private static readonly object Lock = new();
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "808Radio", "808Radio.log");

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
