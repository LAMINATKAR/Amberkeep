using System.Globalization;
using System.Text;
using Amberkeep.Storage;

namespace Amberkeep.App.Services;

/// <summary>
/// Unexpected errors, appended to <c>amberkeep-error.log</c> next to the executable, so a failure is never silent.
/// Kept small: when the file grows over 1 MB it starts again. Clean up everything removes it. Thread-safe.
/// </summary>
public static class ErrorLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>The log file, or null before <see cref="Configure"/> (nothing is written then, e.g. in tests).</summary>
    public static string? Path => _path;

    public static void Configure(string programDirectory) =>
        _path = System.IO.Path.Join(programDirectory, AppStore.ErrorLogFileName);

    /// <summary>Appends the error; never throws.</summary>
    public static void Write(Exception exception, string context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var path = _path;
        if (path is null)
        {
            return;
        }

        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
            .Append("  ")
            .AppendLine(context)
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxSize)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, entry, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The program folder may be read-only; the user still sees the message.
            }
        }
    }
}
