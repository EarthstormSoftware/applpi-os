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
        };
        foreach (var arg in (string[])[program, .. arguments, "card-job", "--stdio"])
        {
            start.ArgumentList.Add(arg);
        }
        using var job = Process.Start(start) ?? throw new InvalidOperationException($"{linuxElevator} didn't start.");
        var outcome = CardJobStream.Relay(request, job.StandardInput.BaseStream, job.StandardOutput.BaseStream, report);
        job.StandardInput.Close();
        job.WaitForExit();
        return Ended(outcome, job.ExitCode);
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

    CardOutcome Ended(CardOutcome? outcome, int exitCode)
    {
        if (outcome is { } ended)
        {
            return ended;
        }
        report.Problem($"juggle with administrator rights stopped without finishing (exit code {exitCode}).");
        return CardOutcome.Failed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);
}
