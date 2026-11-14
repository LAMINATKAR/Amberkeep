using System.Globalization;
using System.Net;
using System.Text;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.History;

namespace Amberkeep.App.Services;

/// <summary>Everything a run report shows, from a history record or a just-finished run.</summary>
public sealed record RunReport(
    string Title,
    string SourcePath,
    string TargetPath,
    DateTimeOffset? Started,
    string Outcome,
    IReadOnlyList<StatViewModel> Stats,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    IReadOnlyList<RecordedChange> Changes,
    bool ChangesTruncated)
{
    public static RunReport From(RunRecord record, IReadOnlyList<RecordedChange> changes)
    {
        var outcome = RunOutcome.From(record);
        return new RunReport(
            $"{record.JobName} → {record.TargetName}",
            record.SourcePath,
            record.TargetPath,
            record.StartedUtc,
            Format.Outcome(record.Outcome),
            RunStats.From(outcome),
            RunStats.Problems(outcome),
            record.Notes,
            changes,
            record.ChangesTruncated);
    }
}

/// <summary>Statistics and problem lines shown for a finished run (result page, history, reports).</summary>
public static class RunStats
{
    public static IReadOnlyList<StatViewModel> From(RunOutcome o)
    {
        var stats = new List<StatViewModel>();
        Add("Copied", o.CopiedFiles > 0 ? Format.Count(o.CopiedFiles, "file", "files") : null);
        Add("Replaced", o.ReplacedFiles > 0 ? Format.Count(o.ReplacedFiles, "file", "files") : null);
        Add("Removed", (o.RemovedFiles, o.RemovedDirectories) switch
        {
            (0, 0) => null,
            (_, 0) => Format.Count(o.RemovedFiles, "file", "files"),
            (0, _) => Format.Count(o.RemovedDirectories, "folder", "folders"),
            _ => $"{Format.Count(o.RemovedFiles, "file", "files")}, {Format.Count(o.RemovedDirectories, "folder", "folders")}",
        });
        Add("New folders", o.CreatedDirectories > 0 ? o.CreatedDirectories.ToString("N0", CultureInfo.InvariantCulture) : null);
        Add("Renamed", o.RenamedEntries > 0 ? o.RenamedEntries.ToString("N0", CultureInfo.InvariantCulture) : null);
        Add("Times updated", o.UpdatedTimestamps > 0 ? o.UpdatedTimestamps.ToString("N0", CultureInfo.InvariantCulture) : null);
        Add("Archived", o.ArchivedFiles > 0 ? Format.Count(o.ArchivedFiles, "file", "files") : null);
        Add("Written", Format.Bytes(o.CopiedBytes));
        Add("Duration", Format.Duration(o.Duration));
        return stats;

        void Add(string label, string? value)
        {
            if (value is not null)
            {
                stats.Add(new StatViewModel(label, value));
            }
        }
    }

    public static IReadOnlyList<string> Problems(RunOutcome o)
    {
        var problems = new List<string>();
        if (o.RefusedReason is not null)
        {
            problems.Add(o.RefusedReason);
        }

        problems.AddRange(o.Failures.Select(f => f.RelativePath.Length > 0 ? $"{f.RelativePath}: {f.Message}" : f.Message));
        return problems;
    }

    /// <summary>A one-line summary, e.g. "12 copied · 3 removed · 1.2 GB".</summary>
    public static string Summary(RunOutcome o)
    {
        if (o.Outcome == ExecutionOutcome.Refused)
        {
            return o.RefusedReason ?? "Not started";
        }

        var parts = new List<string>();
        if (o.CopiedFiles > 0)
        {
            parts.Add($"{o.CopiedFiles:N0} copied");
        }

        if (o.ReplacedFiles > 0)
        {
            parts.Add($"{o.ReplacedFiles:N0} replaced");
        }

        if (o.RemovedFiles > 0)
        {
            parts.Add($"{o.RemovedFiles:N0} removed");
        }

        if (o.Failures.Count > 0)
        {
            parts.Add(Format.Count(o.Failures.Count, "problem", "problems"));
        }

        parts.Add(Format.Bytes(o.CopiedBytes));
        return string.Join("  ·  ", parts);
    }
}

/// <summary>Writes run reports as plain text or as a self-contained HTML page.</summary>
public static class ReportBuilder
{
    /// <summary>HTML for <c>.html</c>/<c>.htm</c> paths, text otherwise.</summary>
    public static string Build(RunReport report, string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".HTML" or ".HTM" ? Html(report) : Text(report);

    public static string Text(RunReport r)
    {
        var text = new StringBuilder();
        text.AppendLine($"Amberkeep – {r.Title}");
        if (r.Started is { } started)
        {
            text.AppendLine($"Started: {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        }

        text.AppendLine($"Source:  {r.SourcePath}");
        text.AppendLine($"Target:  {r.TargetPath}");
        text.AppendLine($"Result:  {r.Outcome}");
        text.AppendLine();
        foreach (var stat in r.Stats)
        {
            text.AppendLine($"{stat.Label,-16} {stat.Value}");
        }

        AppendList("Problems", r.Problems);
        AppendList("Notes", r.Notes);
        if (r.Changes.Count > 0)
        {
            AppendList(
                r.ChangesTruncated ? $"Changes (first {r.Changes.Count:N0})" : "Changes",
                r.Changes.Select(c => $"{Format.Category(c.Category),-13} {c.Path}"));
        }

        return text.ToString();

        void AppendList(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0)
            {
                return;
            }

            text.AppendLine();
            text.AppendLine($"{title}:");
            foreach (var line in list)
            {
                text.AppendLine($"  {line}");
            }
        }
    }

    public static string Html(RunReport r)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
            .Append(E($"Amberkeep – {r.Title}"))
            .Append("</title><style>")
            .Append("body{font-family:system-ui,sans-serif;margin:32px;color:#222;max-width:1000px}")
            .Append("h1{font-size:22px;margin:0 0 4px}.muted{color:#777}.path{font-family:monospace;font-size:13px}")
            .Append(".stats{display:flex;flex-wrap:wrap;gap:8px;margin:16px 0}.stat{background:#f3efe9;border-radius:8px;padding:8px 12px;min-width:120px}")
            .Append(".stat b{display:block}table{border-collapse:collapse;width:100%;font-size:13px}td,th{text-align:left;padding:4px 8px;border-bottom:1px solid #eee}")
            .Append(".problems li{color:#b3261e}")
            .Append("</style></head><body>");
        html.Append("<h1>").Append(E(r.Title)).Append("</h1>");
        html.Append("<div class=\"muted\">").Append(E(r.Outcome));
        if (r.Started is { } started)
        {
            html.Append(" · ").Append(E(started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        html.Append("</div>");
        html.Append("<p class=\"path\">From ").Append(E(r.SourcePath)).Append("<br>To ").Append(E(r.TargetPath)).Append("</p>");
        html.Append("<div class=\"stats\">");
        foreach (var stat in r.Stats)
        {
            html.Append("<div class=\"stat\"><span class=\"muted\">").Append(E(stat.Label)).Append("</span><b>").Append(E(stat.Value)).Append("</b></div>");
        }

        html.Append("</div>");
        List("Problems", r.Problems, "problems");
        List("Notes", r.Notes, "notes");
        if (r.Changes.Count > 0)
        {
            html.Append("<h2>Changes</h2>");
            if (r.ChangesTruncated)
            {
                html.Append("<p class=\"muted\">Only the first ").Append(r.Changes.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" changes are listed.</p>");
            }

            html.Append("<table><tr><th>Change</th><th>Path</th></tr>");
            foreach (var change in r.Changes)
            {
                html.Append("<tr><td>").Append(E(Format.Category(change.Category))).Append("</td><td class=\"path\">").Append(E(change.Path)).Append("</td></tr>");
            }

            html.Append("</table>");
        }

        return html.Append("</body></html>").ToString();

        void List(string title, IReadOnlyList<string> items, string cssClass)
        {
            if (items.Count == 0)
            {
                return;
            }

            html.Append("<h2>").Append(E(title)).Append("</h2><ul class=\"").Append(cssClass).Append("\">");
            foreach (var item in items)
            {
                html.Append("<li>").Append(E(item)).Append("</li>");
            }

            html.Append("</ul>");
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
