using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Juggle.Core.Jobs;

/// <summary>
/// Runs a <see cref="CardJob"/> in juggle started with administrator rights, for that one job: on Windows through UAC
/// (Windows asks), talking over a named pipe only this user can open (an elevated process can't share this one's
/// output); on Linux through sudo in a terminal or pkexec on a desktop (which may ask for the password), talking over
/// its input and output. juggle is started as program with arguments (dotnet with juggle.dll, say), then
/// <c>card-job --pipe NAME</c> or <c>card-job --stdio</c>.
/// </summary>
public sealed partial class ElevatedCardJob(IJobReport report, string program, IReadOnlyList<string> arguments,
    string linuxElevator = "sudo") : ICardJob
{
    public CardOutcome Run(CardRequest request)
    {
        var why = request.Image is null ? "Mounting the card's boot partition" : "Writing a card";
        return OperatingSystem.IsWindows() ? RunWindows(request, why) : RunLinux(request, why);
    }

    CardOutcome RunLinux(CardRequest request, string why)
    {
        report.Say($"{why} needs root: continuing with {linuxElevator}.");
        var start = new ProcessStartInfo(linuxElevator)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // Why it stopped, if it does (sudo asks for the password on the terminal itself, not here)
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])[program, .. arguments, "card-job", "--stdio"])
        {
            start.ArgumentList.Add(arg);
        }
        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Win32Exception)
        {
            report.Problem($"{why} needs root, which juggle asks for with {linuxElevator}, but it isn't installed " +
                $"(on Debian or Ubuntu, the {linuxElevator} package): nothing was written.");
            return CardOutcome.Failed;
        }
        using var job = started ?? throw new InvalidOperationException($"{linuxElevator} didn't start.");
        string? said = null;
        job.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 } text)
            {
                said = text.Trim();
            }
        };
        job.BeginErrorReadLine();
        var outcome = CardJobStream.Relay(request, job.StandardInput.BaseStream, job.StandardOutput.BaseStream, report);
        job.StandardInput.Close();
        job.WaitForExit();
        return Ended(outcome, job.ExitCode, said);
    }

    [SupportedOSPlatform("windows")]
    CardOutcome RunWindows(CardRequest request, string why)
    {
        report.Say($"{why} needs administrator rights: Windows asks for them.");
        var name = $"juggle-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? job;
        try
        {
            job = Process.Start(new ProcessStartInfo(program, [.. arguments, "card-job", "--pipe", name])
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            report.Problem("Administrator rights weren't given: nothing was written.");
            return CardOutcome.Failed;
        }
        using (job)
        {
            if (job is null)
            {
                throw new InvalidOperationException("juggle didn't start with administrator rights.");
            }
            // It connects, or stops without connecting
            var connected = pipe.WaitForConnectionAsync();
            Task.WaitAny(connected, job.WaitForExitAsync());
            if (!connected.IsCompleted)
            {
                job.WaitForExit();
                return Ended(null, job.ExitCode);
            }
            connected.GetAwaiter().GetResult();
            // Only the juggle just started gets the request, with the settings' secrets
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != job.Id)
            {
                report.Problem("Another program connected to juggle's pipe instead of juggle: nothing was written.");
                return CardOutcome.Failed;
            }
            var outcome = CardJobStream.Relay(request, pipe, pipe, report);
            job.WaitForExit();
            return Ended(outcome, job.ExitCode);
        }
    }

    /// <summary>How the job ended; if it didn't say, what it (or sudo or pkexec) said last, if anything.</summary>
    CardOutcome Ended(CardOutcome? outcome, int exitCode, string? said = null)
    {
        if (outcome is { } ended)
        {
            return ended;
        }
        var rights = OperatingSystem.IsWindows() ? "administrator rights" : $"root's rights ({linuxElevator})";
        report.Problem($"juggle with {rights} stopped without finishing (exit code {exitCode})" +
            (said is null ? "." : $": {said.TrimEnd('.')}."));
        return CardOutcome.Failed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);
}
