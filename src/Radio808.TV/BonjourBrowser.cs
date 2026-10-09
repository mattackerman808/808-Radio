namespace Radio808.TV;

/// <summary>
/// Finds rtl_tcp servers on the network: a Raspberry Pi set up by the install script, or a Mac or PC running 808 Radio
/// in server mode, both advertised as _rtl-tcp._tcp. tvOS allows Bonjour browsing (NSNetServiceBrowser) where it
/// forbids the raw multicast the desktop apps use; the service type is declared in Info.plist (NSBonjourServices).
/// </summary>
public sealed class BonjourBrowser : IDisposable
{
    public sealed record Found(string Name, string Address);

    private readonly NSNetServiceBrowser _browser = new();
    private readonly List<NSNetService> _pending = new();
    private readonly Dictionary<string, Found> _found = new();
    public IReadOnlyList<Found> Servers { get { lock (_found) return _found.Values.OrderBy(f => f.Name).ToList(); } }
    public event Action? Changed;

    public BonjourBrowser()
    {
        _browser.FoundService += (_, e) =>
        {
            var svc = e.Service;
            lock (_pending) _pending.Add(svc);   // kept alive while resolving
            svc.AddressResolved += (_, _) =>
            {
                string host = svc.HostName ?? "";
                if (host.EndsWith(".")) host = host[..^1];
                if (host.Length == 0) return;
                string address = svc.Port == 1234 ? host : $"{host}:{svc.Port}";
                lock (_found) _found[svc.Name] = new Found(svc.Name, address);
                Changed?.Invoke();
            };
            svc.Resolve(10);
        };
        _browser.ServiceRemoved += (_, e) =>
        {
            lock (_found) _found.Remove(e.Service.Name);
            Changed?.Invoke();
        };
        _browser.SearchForServices("_rtl-tcp._tcp", "local.");
    }

    public void Dispose()
    {
        _browser.Stop();
        _browser.Dispose();
    }
}
