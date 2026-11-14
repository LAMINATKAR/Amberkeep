using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Amberkeep.Storage.Cleanup;
using Microsoft.Win32;

namespace Amberkeep.App.Platform;

/// <summary>
/// "Start with the system" for the running executable. The entry name contains a short hash of the
/// executable's folder, so portable copies in different folders have their own entries.
/// </summary>
public static class Autostart
{
    public const string BackgroundArgument = "--background";

    public static IAutostart ForCurrentProcess()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        var name = EntryName(exe);
        if (OperatingSystem.IsWindows())
        {
            return new RegistryAutostart(@"Software\Microsoft\Windows\CurrentVersion\Run", name, exe);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? new LaunchAgentAutostart(Path.Combine(home, "Library", "LaunchAgents"), name, exe)
            : new DesktopFileAutostart(Path.Combine(home, ".config", "autostart"), name, exe);
    }

    /// <summary>
    /// True for an entry of an Amberkeep copy that no longer exists (the program was moved or deleted).
    /// Entries of copies that still exist elsewhere are kept.
    /// </summary>
    internal static bool IsStale(string? executablePath) =>
        string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath);

    /// <summary>The first double-quoted value, e.g. the executable in <c>"C:\x\Amberkeep.exe" --background</c>.</summary>
    internal static string? FirstQuoted(string text)
    {
        var start = text.IndexOf('"', StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf('"', start + 1);
        return end > start ? text[(start + 1)..end] : null;
    }

    public static string EntryName(string executablePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? executablePath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant()));
        return "Amberkeep-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}

/// <summary>A value under a registry key in HKEY_CURRENT_USER (normally ...\CurrentVersion\Run).</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutostart(string keyPath, string name, string executablePath) : IAutostart
{
    public string Command => $"\"{executablePath}\" {Autostart.BackgroundArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name) is string;
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        foreach (var other in key.GetValueNames().Where(n => n.StartsWith("Amberkeep-", StringComparison.Ordinal) && n != name))
        {
            if (key.GetValue(other) is string command && Autostart.IsStale(Autostart.FirstQuoted(command)))
            {
                key.DeleteValue(other, throwOnMissingValue: false);
            }
        }

        key.SetValue(name, Command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>An XDG autostart entry (<c>~/.config/autostart/*.desktop</c>) on Linux.</summary>
public sealed class DesktopFileAutostart(string folder, string name, string executablePath) : IAutostart
{
    public string FilePath => Path.Combine(folder, name + ".desktop");

    public bool IsEnabled => File.Exists(FilePath);

    public void Enable()
    {
        Directory.CreateDirectory(folder);
        foreach (var other in Directory.GetFiles(folder, "Amberkeep-*.desktop").Where(f => f != FilePath))
        {
            var exec = File.ReadLines(other).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
            if (Autostart.IsStale(exec is null ? null : Autostart.FirstQuoted(exec)))
            {
                File.Delete(other);
            }
        }

        File.WriteAllText(FilePath, $"""
            [Desktop Entry]
            Type=Application
            Name=Amberkeep
            Comment=Folder backups
            Exec="{executablePath}" {Autostart.BackgroundArgument}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    public void Remove()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

/// <summary>A per-user launch agent (<c>~/Library/LaunchAgents/*.plist</c>) on macOS.</summary>
public sealed class LaunchAgentAutostart(string folder, string name, string executablePath) : IAutostart
{
    public string FilePath => Path.Combine(folder, $"com.amberkeep.{name}.plist");

    public bool IsEnabled => File.Exists(FilePath);

    public void Enable()
    {
        Directory.CreateDirectory(folder);
        foreach (var other in Directory.GetFiles(folder, "com.amberkeep.Amberkeep-*.plist").Where(f => f != FilePath))
        {
            var text = File.ReadAllText(other);
            var start = text.IndexOf("<array><string>", StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf("</string>", start, StringComparison.Ordinal);
            var exe = end > start ? System.Net.WebUtility.HtmlDecode(text[(start + 15)..end]) : null;
            if (Autostart.IsStale(exe))
            {
                File.Delete(other);
            }
        }

        var escaped = System.Security.SecurityElement.Escape(executablePath);
        File.WriteAllText(FilePath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>com.amberkeep.{name}</string>
              <key>ProgramArguments</key>
              <array><string>{escaped}</string><string>{Autostart.BackgroundArgument}</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>

            """);
    }

    public void Remove()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
