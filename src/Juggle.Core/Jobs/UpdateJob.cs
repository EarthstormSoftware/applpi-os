namespace Juggle.Core.Jobs;

/// <summary>
/// Updates a Pi's OS over SSH. The new OS goes into the slot that isn't running, and the Pi restarts into it once; it
/// keeps it if it starts properly, and otherwise goes back to the OS it had.
/// </summary>
public sealed class UpdateJob(IJobHost host, ImageSource images, JugglePaths? paths = null,
    Func<string, IPi>? connect = null, IClock? clock = null)
{
    readonly JugglePaths paths = paths ?? JugglePaths.Default;
    readonly Func<string, IPi> connect = connect ?? (name => new PiShell(PiShell.HostFor(name)));
    readonly IClock clock = clock ?? SystemClock.Instance;

    /// <summary>
    /// Updates the Pi called name (asked for if null) to the image (see <see cref="ImageSource"/>), asking first unless
    /// yes. Returns whether it now runs the new OS.
    /// </summary>
    public bool Run(string? name, string? image, bool yes)
    {
        name = name?.ToLowerInvariant();
        if (name is null)
        {
            name = host.AskPiName(DeviceStore.Load(paths.DeviceStore).LastDevice).ToLowerInvariant();
        }
        else if (SettingsRules.CheckHostname(name) is { } problem)
        {
            host.Problem(problem);
            return false;
        }
        image = images.Find(image);
        if (image is null)
        {
            return false;
        }

        var pi = connect(name);
        host.Say($"Connecting to {pi.Host}");
        SlotStatus before;
        try
        {
            before = SlotStatus.Parse(pi.Run($"{SlotStatus.Command} status"));
        }
        catch (PiCommandException error) when (!error.Unreachable)
        {
            host.Problem($"{name} can't be updated over SSH ({error.Message}): its OS is from before OS updates, so " +
                "flash it once with juggle flash.");
            return false;
        }
        if (before.Permanent != before.Running)
        {
            host.Problem($"{name} is running slot {before.Running} on trial: restart it (it goes back to slot " +
                $"{before.Permanent}) or keep it with 'sudo /usr/lib/juggle/os-update commit' first.");
            return false;
        }
        var target = before.Other;
        host.Say($"{name} runs slot {before.Running}: Juggle OS {before.Holds(before.Running)}.");
        host.Say($"Slot {target} ({before.Holds(target)}) gets {Path.GetFileName(image)}.");
        if (!yes && !host.ConfirmUpdate(new UpdatePlan(name, before, target, image)))
        {
            host.Say("Stopped: nothing was changed.");
            return false;
        }

        // The Pi decompresses and writes as the image arrives; its messages come as it goes
        var size = new FileInfo(image).Length;
        // With the image's block map, the Pi writes only the parts of the root that hold data
        var map = before.Features.Contains("map") ? BlockMap.ForImage(image) : null;
        var write = $"{SlotStatus.Command} write{(map is null ? "" : $" --map={map.ToArgument()}")}";
        try
        {
            pi.Run(write, image, new DirectProgress<long>(sent =>
            {
                if (sent < size)
                {
                    host.Progress(new JobProgress(JobStage.Send, sent, size));
                    return;
                }
                host.Progress(null);
                host.Say($"Sent all {size / (1 << 20)} MiB of the image: the Pi is still writing and checking it.");
            }), host.Say);
        }
        catch (PiCommandException error)
        {
            host.Progress(null);
            host.Problem($"The update wasn't written: {error.Message} {name} still runs slot {before.Running}, unchanged.");
            return false;
        }

        host.Say($"Restarting {name} into slot {target}, once. If it doesn't come back on it, it goes back to slot " +
            $"{before.Running} at its next restart.");
        try
        {
            pi.Run($"{SlotStatus.Command} try", host.Say);
        }
        catch (PiCommandException error) when (error.Unreachable)
        {
            // The connection can close as the Pi goes down
        }

        var after = WaitForRestart(pi, before.Boot, TimeSpan.FromMinutes(10));
        if (after is null)
        {
            host.Problem($"{name} didn't answer within 10 minutes. If it's stuck, power it off and on: it starts slot " +
                $"{before.Running} again. Then 'juggle update {name}' shows where it is.");
            return false;
        }
        if (after.Running != target || !after.Tryboot)
        {
            host.Problem(after.FailureOf(target) is { } failure
                ? $"{name} came back on slot {after.Running}: slot {target} started, but failed its check. {failure}."
                : $"{name} came back on slot {after.Running}: slot {target} didn't get as far as checking itself. It " +
                    "didn't start, or it hung.");
            host.Problem($"It still runs Juggle OS {after.Holds(after.Running)}, as before.");
            return false;
        }

        if (after.Check is null)
        {
            // An OS from before the Pi checked its own trial boots: answering over SSH is the check
            pi.Run($"{SlotStatus.Command} commit", host.Say);
        }
        else
        {
            // The Pi decides: it keeps the slot once it has started properly, or restarts into the old one
            host.Say($"{name} is up on slot {target}, checking itself (up to 5 minutes).");
            // Longer than the Pi's own guard, which restarts a trial boot that isn't kept within 15 minutes
            after = WaitForCheck(pi, after, TimeSpan.FromMinutes(20));
            if (after.Permanent != target)
            {
                var why = after.Check is { } check && check.StartsWith("failed:", StringComparison.Ordinal)
                    ? check["failed:".Length..].Trim()
                    : after.FailureOf(target);
                if (after.Running != target)
                {
                    // It restarted during its check (a crash or a hang): already back on the old slot
                    host.Problem($"{name} came back on slot {after.Running}: slot {target} started, but didn't keep " +
                        $"running. {why ?? "It restarted before it could check itself"}.");
                    host.Problem($"It still runs Juggle OS {after.Holds(after.Running)}, as before.");
                    return false;
                }
                host.Problem(why is not null
                    ? $"{name} didn't keep slot {target}: it goes back to slot {before.Running}, as it was. {why}."
                    : $"{name} didn't keep slot {target} within 20 minutes: it goes back to slot {before.Running} at its " +
                        "next restart, which it does by itself if it's stuck.");
                return false;
            }
        }
        host.Say($"Done: {name} runs Juggle OS {after.Holds(target)} from slot {target}.");
        return true;
    }

    /// <summary>
    /// Waits for the Pi's check of its trial boot: returns its slots once the slot is kept or the check failed, from
    /// its next boot if it restarted meanwhile, or as last seen when time runs out.
    /// </summary>
    SlotStatus WaitForCheck(IPi pi, SlotStatus trial, TimeSpan limit)
    {
        var deadline = clock.UtcNow + limit;
        var status = trial;
        while (status.Permanent != status.Running && status.Check?.StartsWith("failed", StringComparison.Ordinal) != true
            && clock.UtcNow < deadline)
        {
            clock.Sleep(TimeSpan.FromSeconds(10));
            try
            {
                var now = SlotStatus.Parse(pi.Run($"{SlotStatus.Command} status", connectTimeoutSeconds: 10));
                if (now.Boot != trial.Boot)
                {
                    return now;
                }
                status = now;
            }
            catch (PiCommandException error) when (error.Unreachable)
            {
                // Restarting into the old slot, or the network dropped for a moment: its next answer says which
            }
        }
        return status;
    }

    /// <summary>
    /// Waits for a restarting Pi to answer over SSH from a new boot (until it has gone down, the old one answers),
    /// and returns its slots then, or null if it doesn't within the limit.
    /// </summary>
    SlotStatus? WaitForRestart(IPi pi, string oldBoot, TimeSpan limit)
    {
        var deadline = clock.UtcNow + limit;
        var attempts = 0L;
        host.Progress(new JobProgress(JobStage.WaitForRestart, attempts));
        try
        {
            while (clock.UtcNow < deadline)
            {
                clock.Sleep(TimeSpan.FromSeconds(10));
                try
                {
                    var status = SlotStatus.Parse(pi.Run($"{SlotStatus.Command} status", connectTimeoutSeconds: 10));
                    if (status.Boot != oldBoot)
                    {
                        return status;
                    }
                }
                catch (PiCommandException error) when (error.Unreachable)
                {
                }
                host.Progress(new JobProgress(JobStage.WaitForRestart, ++attempts));
            }
            return null;
        }
        finally
        {
            host.Progress(null);
        }
    }
}
