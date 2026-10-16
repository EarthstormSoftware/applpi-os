using System.Diagnostics;

namespace ApplPi.Core;

/// <summary>The user's SSH keys, made and kept by Windows' OpenSSH in %USERPROFILE%\.ssh.</summary>
public static class SshKeys
{
    /// <summary>The public key OpenSSH makes by default (and the ssh command uses).</summary>
    public static string DefaultPublicKeyFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519.pub");

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
        using var keygen = Process.Start(new ProcessStartInfo("ssh-keygen", ["-t", "ed25519", "-f", privateKeyFile])
        {
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("ssh-keygen didn't start: is OpenSSH installed?");
        keygen.WaitForExit();
        return keygen.ExitCode == 0 && File.Exists(publicKeyFile);
    }
}
