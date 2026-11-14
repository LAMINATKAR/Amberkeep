using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Amberkeep.App.Platform;

/// <summary>
/// One running Amberkeep per program folder. A second start asks the first one to show its window and exits.
/// Uses a named mutex and a named pipe (a Unix domain socket on Linux and macOS).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowMessage = "show";
    private static readonly HashSet<string> OwnedInProcess = [];
    private readonly Mutex _mutex;
    private string? _id;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    private SingleInstance(string id)
    {
        _pipeName = "amberkeep-" + id;
        _mutex = new Mutex(initiallyOwned: false, "amberkeep-" + id);
    }

    /// <summary>Raised (on a background thread) when another start asked this instance to show itself.</summary>
    public event EventHandler? ShowRequested;

    public bool IsFirst => _owned;

    /// <param name="programFolder">Folder of the executable; copies in other folders are independent.</param>
    public static SingleInstance Acquire(string programFolder)
    {
        var id = Id(programFolder);
        var instance = new SingleInstance(id);
        lock (OwnedInProcess)
        {
            // A named mutex can be taken again by the thread that owns it, so remember ownership per process too.
            if (!OwnedInProcess.Contains(id))
            {
                try
                {
                    instance._owned = instance._mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner crashed; we own it now.
                    instance._owned = true;
                }

                if (instance._owned)
                {
                    OwnedInProcess.Add(id);
                    instance._id = id;
                }
            }
        }

        if (instance._owned)
        {
            _ = instance.ListenAsync();
        }

        return instance;
    }

    /// <summary>Asks the first instance to show its window. Returns false when it did not answer.</summary>
    public bool SignalFirst(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(ShowMessage);
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owned)
        {
            lock (OwnedInProcess)
            {
                OwnedInProcess.Remove(_id!);
            }

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread; the system releases it when the process ends.
            }
        }

        _mutex.Dispose();
    }

    internal static string Id(string programFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsLinux() ? folder : folder.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task ListenAsync()
    {
        await Task.Yield();
        var buffer = new byte[64];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var read = await server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(buffer, 0, read) == ShowMessage)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client disconnected early, or the pipe is briefly unavailable; keep listening.
                try
                {
                    await Task.Delay(200, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
