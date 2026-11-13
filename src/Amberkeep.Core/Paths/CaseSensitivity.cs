namespace Amberkeep.Core.Paths;

/// <summary>How file names are matched between source and target.</summary>
public enum CaseSensitivity
{
    /// <summary>Platform default: insensitive on Windows and macOS, sensitive elsewhere.</summary>
    Auto,
    Sensitive,
    Insensitive,
}

public static class CaseSensitivityExtensions
{
    public static bool IsCaseSensitive(this CaseSensitivity value) => value switch
    {
        CaseSensitivity.Sensitive => true,
        CaseSensitivity.Insensitive => false,
        _ => !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()),
    };

    public static StringComparer ToComparer(this CaseSensitivity value) =>
        value.IsCaseSensitive() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
