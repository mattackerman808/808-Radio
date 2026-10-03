using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Radio808.Shared;

namespace Radio808.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write(e.ExceptionObject?.ToString() ?? "unknown error");
        if (args.Length >= 2 && args[0] == "--mvtdump")   // development: what's in a cached vector tile
        {
            var tile = Radio808.Shared.Map.MvtTile.Decode(System.IO.File.ReadAllBytes(args[1]));
            foreach (var l in tile.Layers)
            {
                Console.WriteLine($"layer {l.Name}: {l.Features.Count} features, extent {l.Extent}, keys: {string.Join(" ", l.Keys)}");
                var kinds = l.Features.GroupBy(f => $"{f.Type}/{f.Str("kind")}/{f.Tags.GetValueOrDefault("kind_detail")}").OrderByDescending(g => g.Count()).Take(8);
                foreach (var k in kinds) Console.WriteLine($"   {k.Count(),5} {k.Key}");
                var f0 = l.Features.FirstOrDefault(f => f.Tags.ContainsKey("name"));
                if (f0 != null) Console.WriteLine("   sample: " + string.Join(", ", f0.Tags.Select(t => $"{t.Key}={t.Value}({t.Value.GetType().Name})")));
            }
            return 0;
        }
        // one instance: a second copy couldn't use the dongle anyway (one rtl_tcp client at a time)
        using var mutex = new System.Threading.Mutex(true, "808Radio-single-instance", out bool first);
        if (!first && !(args.Length > 0 && args[0].StartsWith("--")))
        {
            Console.Error.WriteLine("808 Radio is already running.");
            return 1;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
