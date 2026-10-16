using System.Diagnostics;

namespace ApplPi.Core;

/// <summary>The user's SSH keys, made and kept by Windows' OpenSSH in %USERPROFILE%\.ssh, and the Pis' host keys.</summary>
public static class SshKeys
{
    static readonly string SshDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>The public key OpenSSH makes by default (and the ssh command uses).</summary>
    public static string DefaultPublicKeyFile { get; } = Path.Combine(SshDirectory, "id_ed25519.pub");

    /// <summary>Where ssh keeps the host keys it trusts.</summary>
    public static string DefaultKnownHostsFile { get; } = Path.Combine(SshDirectory, "known_hosts");

    /// <summary>The public keys in a .pub file, one per line.</summary>
    public static IReadOnlyList<string> Read(string publicKeyFile) => File.ReadAllLines(publicKeyFile)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .ToList();

    /// <summary>
    /// Makes a new ed25519 key pair with ssh-keygen, which asks for a passphrase on this console. Returns whether it
    /// succeeded.
    /// </summary>
    public static bool Create(string publicKeyFile)
    {
        var privateKeyFile = publicKeyFile.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            ? publicKeyFile[..^4]
            : throw new ArgumentException("A public key file ends in .pub", nameof(publicKeyFile));
        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyFile)!);
        return RunKeygen(["-t", "ed25519", "-f", privateKeyFile]) && File.Exists(publicKeyFile);
    }

    /// <summary>
    /// Makes a host key for a Pi: the private key (OpenSSH format, LF line ends) for the Pi, and the public key
    /// ("ssh-ed25519 AAAA...") for known_hosts.
    /// </summary>
    public static (string PrivateKey, string PublicKey) CreateHostKey()
    {
        var directory = Directory.CreateTempSubdirectory("applpi-hostkey");
        try
        {
            var keyFile = Path.Combine(directory.FullName, "key");
            if (!RunKeygen(["-q", "-t", "ed25519", "-N", "", "-C", "", "-f", keyFile]))
            {
                throw new InvalidOperationException("ssh-keygen couldn't make a host key.");
            }
            var publicKey = string.Join(' ', File.ReadAllText(keyFile + ".pub").Split(' ', StringSplitOptions.TrimEntries)[..2]);
            return (File.ReadAllText(keyFile).Replace("\r\n", "\n"), publicKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Makes ssh trust this host key for a host name, replacing whatever it knew for that name, so a reflashed Pi
    /// (with the same key) connects without warnings and a new one without the "authenticity can't be established"
    /// question.
    /// </summary>
    public static void TrustHostKey(string knownHostsFile, string host, string publicKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(knownHostsFile)!);
        var lines = File.Exists(knownHostsFile) ? File.ReadAllLines(knownHostsFile).ToList() : [];
        lines.RemoveAll(line => line.Split(' ', 2)[0].Split(',').Contains(host, StringComparer.OrdinalIgnoreCase));
        lines.Add($"{host} {publicKey}");
        File.WriteAllLines(knownHostsFile, lines);
    }

    static bool RunKeygen(string[] args)
    {
        using var keygen = Process.Start(new ProcessStartInfo("ssh-keygen", args) { UseShellExecute = false })
            ?? throw new InvalidOperationException("ssh-keygen didn't start: is OpenSSH installed?");
        keygen.WaitForExit();
        return keygen.ExitCode == 0;
    }
}
