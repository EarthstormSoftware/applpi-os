using System.Globalization;
using System.Text;
using ApplPi.Core;

namespace ApplPi.Cli;

static class Program
{
    const string Usage = """
        applpi: sets up Raspberry Pis running ApplPi OS

        Usage:
          applpi setup [name]   Ask for a Pi's settings (name, Wi-Fi, time zone, SSH key) and put them on its card.
                                Works on a card written with the ApplPi OS image, in this PC's card reader.
                                The Pi reads them at its next boot, then deletes them from the card.

        Settings are remembered in %APPDATA%\ApplPi (Wi-Fi passwords encrypted for your Windows account).
        """;

    static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["setup"] => Setup(null),
                ["setup", var name] => Setup(name),
                _ => ShowUsage(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    static int ShowUsage()
    {
        Console.WriteLine(Usage);
        return 2;
    }

    static int Setup(string? name)
    {
        var card = ChooseCard();
        if (card is null)
        {
            return 1;
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
        var settings = new DeviceSettings(hostname, timeZone, wifi, keys);
        var errors = SettingsRules.Check(settings);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"These settings won't work ({keyFile}): {string.Join(" ", errors)}");
            return 1;
        }

        card.WriteSettings(settings);
        store.Set(settings, keyFile);
        store.Save(DeviceStore.DefaultPath);

        Console.WriteLine($"""

            Settings for {hostname} are on the card ({card.Root}).
            Eject the card, put it in the Pi and power it on. After a few minutes (the first boot also sets up the card):
              ssh applpi@{hostname}.local
            """);
        if (wifi is null)
        {
            Console.WriteLine("No Wi-Fi: connect the Pi with an Ethernet cable.");
        }
        return 0;
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
