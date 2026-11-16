using System.Diagnostics;
using Amberkeep.App.Services;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.App;

/// <summary>
/// Checks against the real desktop. They open windows on the screen, so they run only when
/// <c>AMBERKEEP_DESKTOP_TESTS=1</c> is set (manual check before a release).
/// </summary>
public sealed class DesktopTests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Show_in_folder_opens_Explorer_with_the_file_selected()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("AMBERKEEP_DESKTOP_TESTS") == "1",
            "Opens Explorer; set AMBERKEEP_DESKTOP_TESTS=1 on Windows to run it.");
        _app.Source.File("docs/report, final.txt", "x");
        var file = _app.Source.FullPath("docs/report, final.txt");
        var dialogs = new OverlayDialogService(_app.Shell, () => null);

        dialogs.RevealInFileManager(file);

        // Asks the Windows shell which Explorer windows are open, what folder they show and what is selected; closes ours.
        var folder = Path.GetDirectoryName(file)!;
        var script = $$"""
            $deadline = (Get-Date).AddSeconds(15)
            do {
              foreach ($w in (New-Object -ComObject Shell.Application).Windows()) {
                try { $path = $w.Document.Folder.Self.Path } catch { continue }
                if ($path -eq '{{folder.Replace("'", "''", StringComparison.Ordinal)}}') {
                  $selected = @($w.Document.SelectedItems() | ForEach-Object { $_.Path })
                  if ($selected.Count -gt 0) { $selected -join '|'; $w.Quit(); exit 0 }
                }
              }
              Start-Sleep -Milliseconds 300
            } while ((Get-Date) -lt $deadline)
            'NOT FOUND'
            """;
        var scriptFile = Path.Combine(_app.BasePath, "check.ps1");
        File.WriteAllText(scriptFile, script);
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        Assert.Equal(file, output);
    }
}
