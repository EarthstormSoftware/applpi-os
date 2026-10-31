namespace Juggle.Core.Jobs;

/// <summary>Forgets a Pi no longer used: its saved settings and host key, and ssh's known host.</summary>
public sealed class ForgetJob(IJobHost host, JugglePaths? paths = null)
{
    readonly JugglePaths paths = paths ?? JugglePaths.Default;

    /// <summary>Returns whether there was anything to forget.</summary>
    public bool Run(string name)
    {
        name = name.ToLowerInvariant();
        var store = DeviceStore.Load(paths.DeviceStore);
        var settings = store.Remove(name);
        if (settings)
        {
            store.Save(paths.DeviceStore);
        }
        var knownHost = SshKeys.ForgetHost(paths.KnownHosts, PiShell.HostFor(name));
        if (!settings && !knownHost)
        {
            host.Problem($"{name} isn't known on this PC: nothing to forget.");
            return false;
        }
        host.Say($"Forgot {name}: {(settings ? "its settings and host key" : "no saved settings")}, and " +
            $"{(knownHost ? $"{PiShell.HostFor(name)} in ssh's known_hosts" : "nothing in ssh's known_hosts")}.");
        if (settings && store.StartingDevice is { } last)
        {
            host.Say($"New Pis now start from {last}'s settings.");
        }
        return true;
    }
}
