namespace Juggle.Core.Jobs;

/// <summary>
/// The default Pi: the one a new Pi starts from (its settings, and its name offered), whichever was set up last.
/// Without one chosen, the last Pi set up.
/// </summary>
public sealed class DefaultJob(IJobReport report, JugglePaths? paths = null)
{
    readonly JugglePaths paths = paths ?? JugglePaths.Default;

    /// <summary>Makes the Pi called name the default (or with null, only says which it is). Returns whether it could.</summary>
    public bool Run(string? name)
    {
        var store = DeviceStore.Load(paths.DeviceStore);
        if (name?.ToLowerInvariant() is { } chosen)
        {
            if (!store.Devices.ContainsKey(chosen))
            {
                report.Problem($"{chosen} isn't known on this PC: set it up first (juggle flash or juggle setup).");
                return false;
            }
            store.DefaultDevice = chosen;
            store.Save(paths.DeviceStore);
        }
        report.Say(store.StartingDevice switch
        {
            null => "No Pis are set up on this PC yet.",
            var pi when pi == store.DefaultDevice => $"New Pis start from {pi}'s settings: it's the default.",
            var pi => $"New Pis start from {pi}'s settings: it was set up last. juggle default <name> chooses one.",
        });
        return true;
    }
}
