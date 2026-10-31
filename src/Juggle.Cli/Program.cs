using System.IO.Pipes;
using System.Reflection;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Cli;

static class Program
{
    static readonly string Usage = $"""
        juggle: sets up Raspberry Pis running Juggle OS

        Usage:
          juggle flash [image] [--no-eject]
                                Write the Juggle OS image to a card in this computer's card reader, check it, and put
                                a Pi's settings on it as below. The image is a .img.xz file, or a release (latest, or a
                                version such as v0.4.0), downloaded from GitHub and checked. It defaults to the newest
                                image you've built in deploy{Path.DirectorySeparatorChar}, or else the latest release.
                                Only card readers are offered, and you confirm before writing. Writing the card
                                {(OperatingSystem.IsWindows() ? "asks for administrator rights" : "runs with sudo")}; nothing else does. The card is ejected
                                safely after: --no-eject leaves it in.
          juggle setup [name] [--no-eject]
                                Ask for a Pi's settings (name, Wi-Fi, time zone, SSH key) and put them on its card.
                                Works on a card written with the Juggle OS image, in this computer's card reader.
                                The Pi reads them at its next boot, then deletes them from the card.
          juggle update [name] [image] [--yes]
                                Update a Pi's OS over SSH (the image as for flash). The new OS
                                goes into the slot that isn't running, and the Pi restarts into it once; it keeps it
                                if it starts properly, and otherwise goes back to the OS it had. --yes doesn't ask.
          juggle forget <name>  Forget a Pi you no longer use: its saved settings and host key, and ssh's known host.
          juggle --version      Show this tool's version.

        {(OperatingSystem.IsWindows()
            ? @"Settings are remembered in %APPDATA%\Juggle (Wi-Fi passwords encrypted for your Windows account)."
            : "Settings are remembered in ~/.config/juggle, in a file only you can read (as ~/.ssh's keys are).")}
        """;

    static readonly ConsoleHost Host = new();

    static int Main(string[] args)
    {
        // A boot partition juggle mounted (Linux) isn't left mounted if it's stopped
        Console.CancelKeyPress += (_, _) => BootPartition.ReleaseAll();
        var eject = !args.Contains("--no-eject");
        if (args is ["flash" or "setup", ..])
        {
            args = [.. args.Where(arg => arg != "--no-eject")];
        }
        try
        {
            return args switch
            {
                ["setup"] => Setup(null, eject),
                ["setup", var name] => Setup(name, eject),
                ["flash"] => Flash(null, eject),
                ["flash", var image] => Flash(image, eject),
                ["update", .. var rest] when rest.Count(arg => arg != "--yes") <= 2 => Update(rest),
                ["forget", var name] => ExitCode(new ForgetJob(Host).Run(name)),
                // Started by juggle itself, with administrator rights, to write a card
                ["card-job", "--pipe", var pipe] => RunCardJob(pipe),
                ["card-job", "--stdio"] => RunCardJob(null),
                ["--version"] => ShowVersion(),
                _ => ShowUsage(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Host.Problem(error.Message);
            return 1;
        }
    }

    static int ExitCode(bool succeeded) => succeeded ? 0 : 1;

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

    static int Flash(string? image, bool eject)
    {
        image = new ImageSource(Host).Find(image);
        return image is null ? 1 : ExitCode(new FlashJob(Host, new SetupJob(Host), CardJob()).Run(image, eject));
    }

    /// <summary>
    /// juggle setup: the card in the card reader. On Linux, a card the desktop hasn't mounted can only be mounted by root,
    /// which the card job does.
    /// </summary>
    static int Setup(string? name, bool eject)
    {
        var cards = BootPartition.FindCards();
        try
        {
            var unmounted = cards.Count == 0 && !Platform.IsAdministrator ? BootPartition.UnmountedCards() : [];
            return ExitCode(new SetupJob(Host).Run(name, cards, unmounted, CardJob(), eject));
        }
        finally
        {
            foreach (var card in cards)
            {
                card.Dispose();
            }
        }
    }

    /// <summary>juggle update [name] [image] [--yes]</summary>
    static int Update(string[] args)
    {
        var plain = args.Where(arg => arg != "--yes").ToArray();
        return ExitCode(new UpdateJob(Host, new ImageSource(Host))
            .Run(plain.ElementAtOrDefault(0), plain.ElementAtOrDefault(1), yes: args.Contains("--yes")));
    }

    /// <summary>
    /// What writes a card: this process if it has administrator rights already, or else this program started again
    /// with them (with sudo on Linux, in this terminal).
    /// </summary>
    static ICardJob CardJob()
    {
        if (Platform.IsAdministrator)
        {
            return new CardJob(Host);
        }
        // Run with dotnet (not as a published program), it needs the program's own file too
        var program = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(program) == "dotnet" ? [Environment.GetCommandLineArgs()[0]] : [];
        return new ElevatedCardJob(Host, program, arguments);
    }

    /// <summary>juggle card-job: the card job, started with administrator rights, talking over a pipe or its input and output.</summary>
    static int RunCardJob(string? pipeName)
    {
        CardOutcome outcome;
        if (pipeName is null)
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            outcome = CardJobStream.Serve(input, output);
        }
        else
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("juggle card-job is started by juggle flash and juggle setup: there was no juggle to " +
                    "talk to.");
                return 1;
            }
            outcome = CardJobStream.Serve(pipe, pipe);
        }
        return outcome == CardOutcome.Failed ? 1 : 0;
    }
}
