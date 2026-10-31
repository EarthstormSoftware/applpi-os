using System.Globalization;
using System.Text;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Cli;

/// <summary>
/// Runs jobs in the terminal: messages on their own lines, progress on one line that updates in place (with messages
/// that arrive meanwhile, such as the Pi's, above it), and questions asked one at a time.
/// </summary>
sealed class ConsoleHost : IJobHost
{
    readonly object console = new();

    // The progress line showing, or "" if there isn't one
    string line = "";

    public void Say(string text)
    {
        lock (console)
        {
            if (line.Length == 0)
            {
                Console.WriteLine(text);
                return;
            }
            Console.WriteLine($"\r{text}".PadRight(line.Length + 1));
            Console.Write($"\r{line}");
        }
    }

    public void Problem(string text)
    {
        lock (console)
        {
            EndLine();
            Console.Error.WriteLine(text);
        }
    }

    public void Progress(JobProgress? progress)
    {
        lock (console)
        {
            if (progress is not { } now)
            {
                EndLine();
                return;
            }
            var text = now.Stage switch
            {
                JobStage.Download => now.Total is { } all
                    ? $"  {now.Item}: {now.Done >> 20} of {all >> 20} MiB"
                    : $"  {now.Item}: {now.Done >> 20} MiB",
                JobStage.Write or JobStage.Check => $"{(now.Stage == JobStage.Check ? "Checking" : "Writing")} " +
                    $"{now.Fraction:P0}  ({now.Done >> 20} of {now.Total >> 20} MiB)   ",
                JobStage.Send => $"Sent {now.Fraction:P0} of the compressed image ({now.Done >> 20} of " +
                    $"{now.Total >> 20} MiB)   ",
                JobStage.WaitForRestart => "Waiting for it" + new string('.', (int)now.Done),
                _ => throw new ArgumentOutOfRangeException(nameof(progress)),
            };
            if (text != line)
            {
                Console.Write($"\r{text}");
                line = text;
            }
        }
    }

    // Leaves the progress line as it is, and goes on below it
    void EndLine()
    {
        if (line.Length > 0)
        {
            Console.WriteLine();
            line = "";
        }
    }

    public RemovableDisk ChooseDisk(IReadOnlyList<RemovableDisk> disks)
    {
        Console.WriteLine("Cards in card readers:");
        foreach (var candidate in disks)
        {
            Console.WriteLine($"  {candidate.Description}");
        }
        // A Windows disk number, or a Linux device (sdb, or /dev/sdb)
        var ids = disks.Select(candidate => OperatingSystem.IsWindows() ? candidate.Id : candidate.DevicePath).ToList();
        string IdOf(string answer) => OperatingSystem.IsWindows() || answer.StartsWith('/') ? answer : $"/dev/{answer}";
        var chosen = Ask(OperatingSystem.IsWindows() ? "Write to disk number" : "Write to disk", disks.Count == 1 ? ids[0] : null,
            answer => ids.Contains(IdOf(answer)) ? null : $"Choose one of: {string.Join(", ", ids)}.");
        return disks[ids.IndexOf(IdOf(chosen))];
    }

    public bool ConfirmErase(RemovableDisk disk)
    {
        Console.WriteLine($"""

            Everything on this card will be erased:
              {disk.Description}
            """);
        Console.Write("Type ERASE to write the image to it: ");
        return Console.ReadLine()?.Trim() == "ERASE";
    }

    public BootPartition ChooseCard(IReadOnlyList<BootPartition> cards)
    {
        Console.WriteLine("Juggle cards:");
        for (var i = 0; i < cards.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {cards[i].Description}");
        }
        var choice = Ask("Which card", null,
            answer => int.TryParse(answer, out var n) && n >= 1 && n <= cards.Count ? null : $"Choose 1 to {cards.Count}.");
        return cards[int.Parse(choice, CultureInfo.InvariantCulture) - 1];
    }

    public SettingsAnswers AskSettings(SettingsForm form)
    {
        var hostname = AskPiName(form.Hostname);
        var defaults = form.DefaultsFor(hostname);
        var timeZone = Ask("Time zone", defaults.TimeZone, SettingsRules.CheckTimeZone);
        return new SettingsAnswers(hostname, timeZone, AskWifi(defaults));
    }

    public string AskPiName(string? suggested) => Ask("Pi name", suggested,
        answer => SettingsRules.CheckHostname(answer.ToLowerInvariant())).ToLowerInvariant();

    public bool ConfirmUpdate(UpdatePlan plan) =>
        Ask("Update it? (y/n)", "y", answer => answer is "y" or "n" ? null : "Answer y or n.") == "y";

    public bool CreateSshKey(string publicKeyFile)
    {
        // Without someone at the keyboard, ssh-keygen would make it without a passphrase
        if (Console.IsInputRedirected)
        {
            Problem($"You have no SSH key yet ({publicKeyFile}): run juggle in a terminal, where ssh-keygen can ask for " +
                "its passphrase, or make one with ssh-keygen first.");
            return false;
        }
        Console.WriteLine($"""

            You have no SSH key yet ({publicKeyFile}): it's how you log in to the Pi. Making one now with ssh-keygen.
            It asks for a passphrase, which protects the key if anyone copies it (or press Enter twice for none).
            """);
        if (SshKeys.Create(publicKeyFile))
        {
            return true;
        }
        Problem("No SSH key was made, so the Pi couldn't be logged in to: stopping.");
        return false;
    }

    static WifiSettings? AskWifi(SettingsDefaults defaults)
    {
        var known = defaults.Wifi;
        var ssid = Ask("Wi-Fi network (leave empty for Ethernet only)", known?.Ssid,
            answer => answer.Length == 0 ? null : SettingsRules.CheckSsid(answer), allowEmpty: true);
        if (ssid.Length == 0)
        {
            return null;
        }
        var keepPassword = known is not null && known.Ssid == ssid && known.Password.Length > 0;
        var password = AskSecret(keepPassword ? "Wi-Fi password (Enter to keep the saved one)" : "Wi-Fi password",
            keepPassword ? known!.Password : null, SettingsRules.CheckPassword);
        var country = Ask("Wi-Fi country", defaults.Country,
            answer => SettingsRules.CheckCountry(answer.ToUpperInvariant())).ToUpperInvariant();
        return new WifiSettings(country, ssid, password);
    }

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
