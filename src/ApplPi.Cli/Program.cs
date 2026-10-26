using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using ApplPi.Core;

namespace ApplPi.Cli;

static class Program
{
    const string Usage = """
        applpi: sets up Raspberry Pis running ApplPi OS

        Usage:
          applpi flash [image]  Write the ApplPi OS image to a card in this PC's card reader, check it, then set it
                                up as below. The image defaults to the newest deploy\applpi-os-*.img.xz. Asks for
                                administrator rights; only card readers are offered, and you confirm before writing.
          applpi setup [name]   Ask for a Pi's settings (name, Wi-Fi, time zone, SSH key) and put them on its card.
                                Works on a card written with the ApplPi OS image, in this PC's card reader.
                                The Pi reads them at its next boot, then deletes them from the card.
          applpi update [name] [image]
                                Update a Pi's OS over SSH (the image defaults to the newest in deploy\). The new OS
                                goes into the slot that isn't running, and the Pi restarts into it once; it's kept
                                if the Pi comes back on it, and otherwise the Pi goes back to the OS it had.
          applpi --version      Show this tool's version.

        Settings are remembered in %APPDATA%\ApplPi (Wi-Fi passwords encrypted for your Windows account).
        """;

    static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["setup"] => Setup(null, ChooseCard()),
                ["setup", var name] => Setup(name, ChooseCard()),
                ["flash"] => Flash(null, elevated: false),
                ["flash", "--elevated"] => Flash(null, elevated: true),
                ["flash", var image] => Flash(image, elevated: false),
                ["flash", var image, "--elevated"] => Flash(image, elevated: true),
                ["update"] => Update(null, null),
                ["update", var name] => Update(name, null),
                ["update", var name, var image] => Update(name, image),
                ["--version"] => ShowVersion(),
                _ => ShowUsage(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    static int ShowVersion()
    {
        Console.WriteLine(typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        return 0;
    }

    static int ShowUsage()
    {
        Console.WriteLine(Usage);
        return 2;
    }

    /// <summary>The image named, or the newest in deploy\; null (having said why) if there isn't one.</summary>
    static string? FindImage(string? image)
    {
        image ??= Directory.Exists("deploy")
            ? Directory.GetFiles("deploy", "applpi-os-*.img.xz").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault()
            : null;
        if (image is null || !File.Exists(image))
        {
            Console.Error.WriteLine(image is null
                ? "No image found in deploy\\: build one with image\\build.ps1, or name the .img.xz file."
                : $"No such image: {image}");
            return null;
        }
        return Path.GetFullPath(image);
    }

    static int Flash(string? image, bool elevated)
    {
        image = FindImage(image);
        if (image is null)
        {
            return 1;
        }

        if (!elevated)
        {
            // The SSH key is made as this user, before asking for administrator rights (OpenSSH checks who owns it)
            if (!File.Exists(SshKeys.DefaultPublicKeyFile) && !CreateKey(SshKeys.DefaultPublicKeyFile))
            {
                return 1;
            }
            if (!IsAdministrator())
            {
                return RunElevated(["flash", image, "--elevated"]);
            }
        }

        var result = WriteCard(image);
        if (elevated)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close this window.");
            Console.ReadLine();
        }
        return result;
    }

    static int WriteCard(string image)
    {
        Console.WriteLine($"Image: {Path.GetFileName(image)}");
        var disks = RemovableDisk.FindAll();
        if (disks.Count == 0)
        {
            Console.Error.WriteLine("No card found: put the card in the card reader and try again.");
            return 1;
        }
        Console.WriteLine("Cards in card readers:");
        foreach (var candidate in disks)
        {
            Console.WriteLine($"  {candidate.Description}");
        }
        var numbers = disks.Select(candidate => candidate.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        var chosen = Ask("Write to disk number", disks.Count == 1 ? numbers[0] : null,
            answer => numbers.Contains(answer) ? null : $"Choose one of: {string.Join(", ", numbers)}.");
        var disk = disks.Single(candidate => candidate.Number.ToString(CultureInfo.InvariantCulture) == chosen);

        Console.WriteLine($"""

            Everything on this card will be erased:
              {disk.Description}
            """);
        Console.Write("Type ERASE to write the image to it: ");
        if (Console.ReadLine()?.Trim() != "ERASE")
        {
            Console.WriteLine("Stopped: nothing was written.");
            return 1;
        }

        if (BlockMap.ForImage(image) is { } map)
        {
            Console.WriteLine($"Writing {map.MappedBytes / (1 << 20)} MiB of the {map.ImageSize / (1 << 20)} MiB image " +
                "(the rest is empty space), then reading it back to check it.");
        }
        var lastShown = "";
        ImageWriter.Write(disk, image, new ConsoleProgress<WriteProgress>(progress =>
        {
            var line = $"\r{(progress.Verifying ? "Checking" : "Writing")} {progress.Fraction:P0}  " +
                $"({progress.Bytes / (1 << 20)} of {progress.Total / (1 << 20)} MiB)   ";
            if (line != lastShown)
            {
                Console.Write(line);
                lastShown = line;
            }
        }));
        Console.WriteLine("\rWritten and checked.                          ");

        var card = WaitForBootPartition(disk.Number);
        if (card is null)
        {
            Console.Error.WriteLine("Windows didn't show the card's boot partition: take the card out, put it back " +
                "in and run applpi setup.");
            return 1;
        }
        return Setup(null, card);
    }

    /// <summary>The boot partition of the card just written, once Windows has mounted it.</summary>
    static BootPartition? WaitForBootPartition(int diskNumber)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var letters = RemovableDisk.FindAll().FirstOrDefault(disk => disk.Number == diskNumber)?.DriveLetters ?? [];
            var root = letters.Select(letter => $@"{letter}:\").FirstOrDefault(BootPartition.IsApplPi);
            if (root is not null)
            {
                return new BootPartition(root, $"{root.TrimEnd('\\')} (boot partition)");
            }
            Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it to the thread pool, out of order).</summary>
    sealed class ConsoleProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Runs this program again with administrator rights (Windows asks first), in its own window.</summary>
    static int RunElevated(string[] args)
    {
        Console.WriteLine("Writing a card needs administrator rights: continuing in a new window.");
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (Win32Exception)
        {
            Console.Error.WriteLine("Administrator rights weren't given: nothing was written.");
            return 1;
        }
    }

    static int Setup(string? name, BootPartition? card)
    {
        if (card is null)
        {
            return 1;
        }
        if (card.ReadRelease() is var (version, built))
        {
            Console.WriteLine($"Image: ApplPi OS {version}, build {built}");
        }
        if (card.ReadError() is { } error)
        {
            Console.WriteLine($"The Pi left a message on this card about the last settings: {error}");
        }
        if (card.HasPendingSettings)
        {
            Console.WriteLine("The card already has settings the Pi hasn't read yet: these will replace them.");
        }

        var store = DeviceStore.Load(DeviceStore.DefaultPath);
        var hostname = Ask("Pi name", name?.ToLowerInvariant() ?? store.LastDevice,
            answer => SettingsRules.CheckHostname(answer.ToLowerInvariant())).ToLowerInvariant();

        // A Pi set up before keeps its settings; a new one starts from the last Pi's
        var known = store.Get(hostname) ?? (store.LastDevice is null ? null : store.Get(store.LastDevice));
        var timeZone = Ask("Time zone", known?.Settings.TimeZone ?? LocalTimeZone(), SettingsRules.CheckTimeZone);
        var wifi = AskWifi(known?.Settings.Wifi);
        var keyFile = known?.SshKeyFile is { Length: > 0 } stored && File.Exists(stored) ? stored : SshKeys.DefaultPublicKeyFile;
        if (!File.Exists(keyFile) && !CreateKey(keyFile))
        {
            return 1;
        }
        var keys = SshKeys.Read(keyFile);

        // Each Pi keeps one host key, made here the first time, so reflashing it doesn't change its identity
        var hostKey = store.Get(hostname)?.Settings.HostKey;
        var hostPublicKey = store.GetHostPublicKey(hostname);
        if (hostKey is null || hostPublicKey is null)
        {
            (hostKey, hostPublicKey) = SshKeys.CreateHostKey();
        }
        var settings = new DeviceSettings(hostname, timeZone, wifi, keys, hostKey);
        var errors = SettingsRules.Check(settings);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"These settings won't work ({keyFile}): {string.Join(" ", errors)}");
            return 1;
        }

        card.WriteSettings(settings);
        store.Set(settings, keyFile, hostPublicKey);
        store.Save(DeviceStore.DefaultPath);
        SshKeys.TrustHostKey(SshKeys.DefaultKnownHostsFile, $"{hostname}.local", hostPublicKey);

        Console.WriteLine($"""

            Settings for {hostname} are on the card ({card.Root}), and ssh on this PC knows its host key.
            Eject the card, put it in the Pi and power it on. After a few minutes (the first boot also sets up the card):
              ssh applpi@{hostname}.local
            """);
        if (wifi is null)
        {
            Console.WriteLine("No Wi-Fi: connect the Pi with an Ethernet cable.");
        }
        return 0;
    }

    static int Update(string? name, string? image)
    {
        name = name?.ToLowerInvariant();
        if (name is null)
        {
            name = Ask("Pi name", DeviceStore.Load(DeviceStore.DefaultPath).LastDevice,
                answer => SettingsRules.CheckHostname(answer.ToLowerInvariant())).ToLowerInvariant();
        }
        else if (SettingsRules.CheckHostname(name) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }
        image = FindImage(image);
        if (image is null)
        {
            return 1;
        }

        var pi = new PiShell(PiShell.HostFor(name));
        Console.WriteLine($"Connecting to {pi.Host}");
        SlotStatus before;
        try
        {
            before = SlotStatus.Parse(pi.Run($"{SlotStatus.Command} status"));
        }
        catch (PiCommandException error) when (!error.Unreachable)
        {
            Console.Error.WriteLine($"{name} can't be updated over SSH ({error.Message}): its OS is from before OS " +
                "updates, so flash it once with applpi flash.");
            return 1;
        }
        if (before.Permanent != before.Running)
        {
            Console.Error.WriteLine($"{name} is running slot {before.Running} on trial: restart it (it goes back to " +
                $"slot {before.Permanent}) or keep it with 'sudo /usr/lib/applpi/os-update commit' first.");
            return 1;
        }
        var target = before.Other;
        Console.WriteLine($"""
            {name} runs slot {before.Running}: ApplPi OS {before.Holds(before.Running)}.
            Slot {target} ({before.Holds(target)}) gets {Path.GetFileName(image)}.
            """);
        var confirm = Ask("Update it? (y/n)", "y", answer => answer is "y" or "n" ? null : "Answer y or n.");
        if (confirm != "y")
        {
            Console.WriteLine("Stopped: nothing was changed.");
            return 1;
        }

        // The Pi decompresses and writes as the image arrives; its messages come between the progress lines
        var size = new FileInfo(image).Length;
        var console = new object();
        var progressLine = "";
        void Message(string text)
        {
            lock (console)
            {
                Console.WriteLine($"\r{text}".PadRight(progressLine.Length + 1));
                Console.Write(progressLine);
            }
        }
        // With the image's block map, the Pi writes only the parts of the root that hold data
        var map = before.Features.Contains("map") ? BlockMap.ForImage(image) : null;
        var write = $"{SlotStatus.Command} write{(map is null ? "" : $" --map={map.ToArgument()}")}";
        try
        {
            pi.Run(write, image, new ConsoleProgress<long>(sent =>
            {
                lock (console)
                {
                    if (sent == size)
                    {
                        // Once it's all sent, the Pi's messages carry on without the progress line under them
                        Console.WriteLine($"\rSent all {size / (1 << 20)} MiB of the image: the Pi is still writing and checking it."
                            .PadRight(progressLine.Length + 1));
                        progressLine = "";
                        return;
                    }
                    var line = $"\rSent {(double)sent / size:P0} of the compressed image ({sent / (1 << 20)} of " +
                        $"{size / (1 << 20)} MiB)   ";
                    if (line != progressLine)
                    {
                        Console.Write(line);
                        progressLine = line;
                    }
                }
            }), Message);
        }
        catch (PiCommandException error)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"The update wasn't written: {error.Message} {name} still runs slot {before.Running}, " +
                "unchanged.");
            return 1;
        }

        Console.WriteLine($"Restarting {name} into slot {target}, once. If it doesn't come back on it, it goes back to " +
            $"slot {before.Running} at its next restart.");
        try
        {
            pi.Run($"{SlotStatus.Command} try", Message);
        }
        catch (PiCommandException error) when (error.Unreachable)
        {
            // The connection can close as the Pi goes down
        }

        var after = WaitForRestart(pi, before.Boot, TimeSpan.FromMinutes(10));
        if (after is null)
        {
            Console.Error.WriteLine($"""
                {name} didn't answer within 10 minutes. If it's stuck, power it off and on: it starts slot
                {before.Running} again. Then 'applpi update {name}' shows where it is.
                """);
            return 1;
        }
        if (after.Running != target || !after.Tryboot)
        {
            Console.Error.WriteLine(after.FailureOf(target) is { } failure
                ? $"{name} came back on slot {after.Running}: slot {target} started, but failed its check. {failure}."
                : $"{name} came back on slot {after.Running}: slot {target} didn't get as far as checking itself. It " +
                    "didn't start, or it hung.");
            Console.Error.WriteLine($"It still runs ApplPi OS {after.Holds(after.Running)}, as before.");
            return 1;
        }

        if (after.Check is null)
        {
            // An OS from before the Pi checked its own trial boots: answering over SSH is the check
            pi.Run($"{SlotStatus.Command} commit", Message);
        }
        else
        {
            // The Pi decides: it keeps the slot once it has started properly, or restarts into the old one
            Console.WriteLine($"{name} is up on slot {target}, checking itself (up to 5 minutes).");
            after = WaitForCheck(pi, after, TimeSpan.FromMinutes(10));
            if (after?.Permanent != target)
            {
                var why = after?.Check is { } check && check.StartsWith("failed:", StringComparison.Ordinal)
                    ? check["failed:".Length..].Trim()
                    : after?.FailureOf(target);
                Console.Error.WriteLine(why is not null
                    ? $"{name} didn't keep slot {target}: it goes back to slot {before.Running}, as it was. {why}."
                    : $"{name} didn't keep slot {target}: it goes back to slot {before.Running} at its next restart.");
                return 1;
            }
        }
        Console.WriteLine($"Done: {name} runs ApplPi OS {after.Holds(target)} from slot {target}.");
        return 0;
    }

    /// <summary>
    /// Waits for the Pi's check of its trial boot: returns its slots once the slot is kept or the check failed, from
    /// its next boot if it restarted meanwhile, or as last seen when time runs out.
    /// </summary>
    static SlotStatus? WaitForCheck(PiShell pi, SlotStatus trial, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        var status = trial;
        while (status.Permanent != status.Running && status.Check?.StartsWith("failed", StringComparison.Ordinal) != true
            && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
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
    /// and returns its slots then.
    /// </summary>
    static SlotStatus? WaitForRestart(PiShell pi, string oldBoot, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        Console.Write("Waiting for it");
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            try
            {
                var status = SlotStatus.Parse(pi.Run($"{SlotStatus.Command} status", connectTimeoutSeconds: 10));
                if (status.Boot != oldBoot)
                {
                    Console.WriteLine();
                    return status;
                }
            }
            catch (PiCommandException error) when (error.Unreachable)
            {
            }
            Console.Write('.');
        }
        Console.WriteLine();
        return null;
    }

    static BootPartition? ChooseCard()
    {
        var cards = BootPartition.FindCards();
        switch (cards.Count)
        {
            case 0:
                Console.Error.WriteLine(
                    "No ApplPi card found. Put a card written with the ApplPi OS image in the card reader and try again.");
                return null;
            case 1:
                Console.WriteLine($"Card: {cards[0].Description}");
                return cards[0];
        }
        Console.WriteLine("ApplPi cards:");
        for (var i = 0; i < cards.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {cards[i].Description}");
        }
        var choice = Ask("Which card", null,
            answer => int.TryParse(answer, out var n) && n >= 1 && n <= cards.Count ? null : $"Choose 1 to {cards.Count}.");
        return cards[int.Parse(choice, CultureInfo.InvariantCulture) - 1];
    }

    static WifiSettings? AskWifi(WifiSettings? known)
    {
        var ssid = Ask("Wi-Fi network (leave empty for Ethernet only)", known?.Ssid,
            answer => answer.Length == 0 ? null : SettingsRules.CheckSsid(answer), allowEmpty: true);
        if (ssid.Length == 0)
        {
            return null;
        }
        var keepPassword = known is not null && known.Ssid == ssid && known.Password.Length > 0;
        var password = AskSecret(keepPassword ? "Wi-Fi password (Enter to keep the saved one)" : "Wi-Fi password",
            keepPassword ? known!.Password : null, SettingsRules.CheckPassword);
        var country = Ask("Wi-Fi country", known?.Country is { Length: > 0 } c ? c : RegionInfo.CurrentRegion.TwoLetterISORegionName,
            answer => SettingsRules.CheckCountry(answer.ToUpperInvariant())).ToUpperInvariant();
        return new WifiSettings(country, ssid, password);
    }

    static bool CreateKey(string keyFile)
    {
        Console.WriteLine($"""

            You have no SSH key yet ({keyFile}): it's how you log in to the Pi. Making one now with ssh-keygen.
            It asks for a passphrase, which protects the key if anyone copies it (or press Enter twice for none).
            """);
        if (SshKeys.Create(keyFile))
        {
            return true;
        }
        Console.Error.WriteLine("No SSH key was made, so the Pi couldn't be logged in to: stopping.");
        return false;
    }

    /// <summary>This PC's time zone as an IANA name, the default for a new Pi.</summary>
    static string LocalTimeZone() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";

    /// <summary>Asks until the answer passes the check; Enter takes the default.</summary>
    static string Ask(string prompt, string? defaultValue, Func<string, string?> check, bool allowEmpty = false)
    {
        while (true)
        {
            Console.Write(defaultValue is { Length: > 0 } ? $"{prompt} [{defaultValue}]: " : $"{prompt}: ");
            var answer = (Console.ReadLine() ?? throw new InvalidOperationException("No input: stopping.")).Trim();
            if (answer.Length == 0 && defaultValue is not null)
            {
                answer = defaultValue;
            }
            if (answer.Length == 0 && !allowEmpty)
            {
                continue;
            }
            if (check(answer) is { } problem)
            {
                Console.WriteLine($"  {problem}");
                continue;
            }
            return answer;
        }
    }

    /// <summary>Like <see cref="Ask"/>, without showing what's typed.</summary>
    static string AskSecret(string prompt, string? defaultValue, Func<string, string?> check)
    {
        while (true)
        {
            Console.Write($"{prompt}: ");
            var answer = ReadHidden();
            if (answer.Length == 0 && defaultValue is not null)
            {
                return defaultValue;
            }
            if (check(answer) is { } problem)
            {
                Console.WriteLine($"  {problem}");
                continue;
            }
            return answer;
        }
    }

    static string ReadHidden()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return text.ToString();
                case ConsoleKey.Backspace when text.Length > 0:
                    text.Length--;
                    Console.Write("\b \b");
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                        Console.Write('*');
                    }
                    break;
            }
        }
    }
}
