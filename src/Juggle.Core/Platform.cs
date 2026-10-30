using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Juggle.Core;

/// <summary>
/// Where juggle keeps its files on Windows and Linux, and whose they are. Writing a card needs administrator rights:
/// on Windows a second process for the same user, on Linux the same command again with sudo, which runs it as root. Under
/// sudo the settings, SSH keys and known_hosts are still the user's who ran it: in their home, and owned by them.
/// </summary>
public static partial class Platform
{
    /// <summary>The user's home folder (under sudo, that of the user who ran sudo, not root's).</summary>
    public static string Home { get; } = FindHome();

    /// <summary>Where the settings of each Pi are kept: %APPDATA%\Juggle, or ~/.config/juggle.</summary>
    public static string ConfigDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Juggle")
        : Path.Combine(UserDirectory("XDG_CONFIG_HOME", ".config"), "juggle");

    /// <summary>Where downloads are kept: %LOCALAPPDATA%\Juggle, or ~/.cache/juggle.</summary>
    public static string CacheDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Juggle")
        : Path.Combine(UserDirectory("XDG_CACHE_HOME", ".cache"), "juggle");

    /// <summary>Whether this process may write a whole disk: an administrator on Windows, root on Linux.</summary>
    public static bool IsAdministrator => OperatingSystem.IsWindows() ? IsWindowsAdministrator() : Environment.IsPrivilegedProcess;

    /// <summary>The user who ran this with sudo, or null when it isn't running as root under sudo.</summary>
    static string? SudoUser => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess
        && Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } user && user != "root" ? user : null;

    /// <summary>
    /// Gives a file or folder made under sudo to the user who ran sudo, so they can still use it without root. Does
    /// nothing otherwise.
    /// </summary>
    public static void GiveToUser(string path)
    {
        if (SudoUser is null || !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var uid)
            || !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out var gid))
        {
            return;
        }
        if (Chown(path, uid, gid) != 0)
        {
            throw new IOException($"Can't give {path} to {SudoUser}: error {Marshal.GetLastPInvokeError()}.");
        }
    }

    /// <summary>
    /// Makes a folder of the user's, and any it's in that don't exist yet, each given to the user under sudo (so a new
    /// ~/.config isn't left to root). On Linux, new folders get the mode given (by default, as the umask allows).
    /// </summary>
    public static void CreateUserDirectory(string path, UnixFileMode? mode = null)
    {
        var missing = new Stack<string>();
        for (var folder = Path.GetFullPath(path); !Directory.Exists(folder); folder = Path.GetDirectoryName(folder)!)
        {
            missing.Push(folder);
        }
        while (missing.TryPop(out var folder))
        {
            if (OperatingSystem.IsWindows() || mode is null || missing.Count > 0)
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, mode.Value);
            }
            GiveToUser(folder);
        }
    }

    /// <summary>Runs a program and waits for it, throwing with what it said if it fails.</summary>
    public static string Run(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} didn't start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"{program} {string.Join(' ', args)} failed: {error.Result.Trim()}");
        }
        return output;
    }

    static string FindHome()
    {
        if (SudoUser is { } user)
        {
            // getent passwd: name:password:uid:gid:comment:home:shell
            var fields = Run("getent", "passwd", user).Trim().Split(':');
            if (fields.Length >= 6 && fields[5].Length > 0)
            {
                return fields[5];
            }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// An XDG base folder on Linux: its variable if set (not under sudo, which may have kept root's), or else its usual
    /// place in the home folder.
    /// </summary>
    static string UserDirectory(string variable, string inHome) =>
        SudoUser is null && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } directory
            ? directory
            : Path.Combine(Home, inHome);

    [SupportedOSPlatform("windows")]
    static bool IsWindowsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chown(string path, uint owner, uint group);
}
