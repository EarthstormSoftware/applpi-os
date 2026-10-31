using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Cli;

static class Program
{
    static readonly string Usage = $"""
        juggle: sets up Raspberry Pis running Juggle OS

        Usage:
          juggle flash [image]  Write the Juggle OS image to a card in this computer's card reader, check it, then set
                                it up as below. The image is a .img.xz file, or a release (latest, or a version such as
                                v0.4.0), downloaded from GitHub and checked. It defaults to the newest image you've
                                built in deploy{Path.DirectorySeparatorChar}, or else the latest release. {(OperatingSystem.IsWindows()
                                    ? "Asks for administrator rights" : "Runs itself with sudo")}; only card
                                readers are offered, and you confirm before writing.
          juggle setup [name]   Ask for a Pi's settings (name, Wi-Fi, time zone, SSH key) and put them on its card.
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
        try
        {
            return args switch
            {
                ["setup"] => Setup(null, elevated: false),
                ["setup", "--elevated"] => Setup(null, elevated: true),
                ["setup", var name] => Setup(name, elevated: false),
                ["setup", var name, "--elevated"] => Setup(name, elevated: true),
                ["flash"] => Flash(null, elevated: false),
                ["flash", "--elevated"] => Flash(null, elevated: true),
                ["flash", var image] => Flash(image, elevated: false),
                ["flash", var image, "--elevated"] => Flash(image, elevated: true),
                ["update", .. var rest] when rest.Count(arg => arg != "--yes") <= 2 => Update(rest),
                ["forget", var name] => ExitCode(new ForgetJob(Host).Run(name)),
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

    static int Flash(string? image, bool elevated)
    {
        image = new ImageSource(Host).Find(image);
        if (image is null)
        {
            return 1;
        }

        if (!elevated)
        {
            // The SSH key is made as this user, before asking for administrator rights (OpenSSH checks who owns it)
            if (!File.Exists(SshKeys.DefaultPublicKeyFile) && !Host.CreateSshKey(SshKeys.DefaultPublicKeyFile))
            {
                return 1;
            }
            if (!Platform.IsAdministrator)
            {
                return RunElevated(["flash", image, "--elevated"], "Writing a card");
            }
        }

        var result = ExitCode(new FlashJob(Host, new SetupJob(Host)).Run(image));
        // Windows runs it in a window of its own, which would close before it could be read
        if (elevated && OperatingSystem.IsWindows())
        {
            Console.WriteLine();
            Console.Write("Press Enter to close this window.");
            Console.ReadLine();
        }
        return result;
    }

    /// <summary>
    /// Runs this program again with administrator rights: on Windows in its own window (Windows asks first), on Linux
    /// with sudo in this terminal (which may ask for your password). why says what needs them, such as "Writing a card".
    /// </summary>
    static int RunElevated(string[] args, string why)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"{why} needs root: continuing with sudo.");
            var start = new ProcessStartInfo("sudo") { UseShellExecute = false };
            start.ArgumentList.Add(Environment.ProcessPath!);
            // Run with dotnet (not as a published program), it needs the program's own file too
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            {
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var sudo = Process.Start(start) ?? throw new InvalidOperationException("sudo didn't start.");
            sudo.WaitForExit();
            return sudo.ExitCode;
        }
        Console.WriteLine($"{why} needs administrator rights: continuing in a new window.");
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

    /// <summary>
    /// juggle setup: the card in the card reader. On Linux, a card the desktop hasn't mounted can only be mounted by root,
    /// so it runs itself again with sudo.
    /// </summary>
    static int Setup(string? name, bool elevated)
    {
        var cards = BootPartition.FindCards();
        if (cards.Count == 0 && !elevated && !Platform.IsAdministrator && BootPartition.UnmountedCards() > 0)
        {
            return RunElevated(name is null ? ["setup", "--elevated"] : ["setup", name, "--elevated"],
                "The card's boot partition isn't mounted, and mounting it");
        }
        try
        {
            return ExitCode(new SetupJob(Host).Run(name, cards));
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
}
