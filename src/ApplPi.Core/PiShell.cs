using System.Diagnostics;

namespace ApplPi.Core;

/// <summary>
/// Runs commands on a Pi over SSH, as its admin user, with Windows' OpenSSH (so the user's keys, agent and
/// known_hosts work as they do for ssh itself).
/// </summary>
public sealed class PiShell(string host)
{
    public const string User = "applpi";

    /// <summary>The host name of a Pi called name on the local network (mDNS).</summary>
    public static string HostFor(string name) => $"{name}.local";

    public string Host { get; } = host;

    /// <summary>Runs a command and returns what it printed. Its messages (stderr) go to onMessage, line by line.</summary>
    public string Run(string command, Action<string>? onMessage = null, int connectTimeoutSeconds = 15) =>
        Run(command, input: null, progress: null, onMessage, connectTimeoutSeconds);

    /// <summary>Runs a command with a file as its input, reporting how much of the file has been sent.</summary>
    public string Run(string command, string inputFile, IProgress<long> progress, Action<string>? onMessage = null) =>
        Run(command, inputFile, progress, onMessage, 15);

    string Run(string command, string? input, IProgress<long>? progress, Action<string>? onMessage, int connectTimeoutSeconds)
    {
        var start = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Without input, -n keeps ssh off this console's input (it still asks for a key's passphrase there)
        foreach (var arg in (string[])[
            .. input is null ? ["-n"] : Array.Empty<string>(),
            "-o", $"ConnectTimeout={connectTimeoutSeconds}", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4",
            $"{User}@{Host}", command])
        {
            start.ArgumentList.Add(arg);
        }

        using var ssh = Process.Start(start) ?? throw new InvalidOperationException("ssh didn't start: is OpenSSH installed?");
        var messages = new List<string>();
        ssh.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 } text)
            {
                lock (messages)
                {
                    messages.Add(text);
                }
                onMessage?.Invoke(text);
            }
        };
        ssh.BeginErrorReadLine();
        var output = ssh.StandardOutput.ReadToEndAsync();

        if (input is not null)
        {
            try
            {
                using var file = File.OpenRead(input);
                var buffer = new byte[1 << 20];
                long sent = 0;
                int read;
                while ((read = file.Read(buffer)) > 0)
                {
                    ssh.StandardInput.BaseStream.Write(buffer, 0, read);
                    sent += read;
                    progress?.Report(sent);
                }
                ssh.StandardInput.Close();
            }
            catch (IOException) when (ssh.HasExited || ssh.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                // The Pi stopped reading (it said why on stderr): its exit code tells
            }
        }

        ssh.WaitForExit();
        if (ssh.ExitCode != 0)
        {
            string last;
            lock (messages)
            {
                last = messages.LastOrDefault() ?? $"exit code {ssh.ExitCode}";
            }
            throw new PiCommandException(ssh.ExitCode, last);
        }
        return output.Result;
    }
}

/// <summary>A command on the Pi failed (or ssh couldn't reach it: exit code 255).</summary>
public sealed class PiCommandException(int exitCode, string message) : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;

    public bool Unreachable => ExitCode == 255;
}

/// <summary>
/// What os-update status reports: the slots, which is running, permanent and waiting to be tried, the boot's ID (new
/// at each start, so a restart can be told from a Pi that hasn't gone down yet) and what the Pi's own check of a trial
/// boot found: "" (no trial), "checking", "kept" or "failed: why". Check is null from an OS from before the check,
/// which leaves keeping the slot to applpi. Features lists what the running os-update can do ("map": write takes the
/// image's block map).
/// </summary>
public sealed record SlotStatus(string Boot, string Running, bool Tryboot, string Permanent, string Waiting, string SlotA,
    string SlotB, string? Check, IReadOnlyList<string> Features)
{
    public const string Command = "sudo -n /usr/lib/applpi/os-update";

    /// <summary>The slot an update writes: the one that isn't running.</summary>
    public string Other => Running == "A" ? "B" : "A";

    public string Holds(string slot) => (slot == "A" ? SlotA : SlotB) is { Length: > 0 } release ? release : "nothing yet";

    public static SlotStatus Parse(string text)
    {
        var values = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
        string Value(string key) => values.TryGetValue(key, out var value) ? value
            : throw new InvalidOperationException($"os-update status didn't say {key}.");
        return new SlotStatus(Value("boot"), Value("running"), Value("tryboot") == "1", Value("permanent"), Value("waiting"),
            Value("slotA"), Value("slotB"), values.GetValueOrDefault("check"),
            values.GetValueOrDefault("features", "").Split(',', StringSplitOptions.RemoveEmptyEntries));
    }
}
