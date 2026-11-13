using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo show &lt;job&gt; &lt;path&gt;</c>: one directory or one file with everything that is known about it.</summary>
public static class ShowCommand
{
    /// <summary>A file with every field the output guard lets out.</summary>
    public sealed record FileDetailView(
        string Path,
        string Name,
        string RelativeDir,
        string Extension,
        string? FileType,
        long SizeBytes,
        DateTime? LastWriteTime,
        string? Status,
        string? Category,
        string? CategorySource,
        string? Importance,
        double? Confidence,
        string? Summary,
        string? SuggestedTarget,
        string? ModelUsed,
        DateTime? AnalyzedAt,
        string? Error,
        string? RawResponse,
        IReadOnlyList<string> Sensitive,
        string? SensitiveRule,
        bool Protected,
        string? DirVerdict,
        string? DirRetention,
        string? Certainty,
        int? Attempts,
        string? PreviousError,
        IReadOnlyList<CheckView> Checks)
    {
        public static FileDetailView From(FileItem f)
        {
            var r = f.Record;
            return new FileDetailView(r.FullPath, r.Name, r.RelativeDir, r.Extension, ReadFormat.Snake(r.FileType),
                r.SizeBytes, r.LastWriteTime, ReadFormat.Snake(r.Status), ReadFormat.Snake(r.Category), r.CategorySource,
                ReadFormat.Snake(r.Importance), r.Confidence, r.Summary, r.SuggestedTarget, r.ModelUsed, r.AnalyzedAt,
                r.Error, r.RawResponse, r.Sensitive, r.SensitiveRule, r.Protected, f.DirectoryVerdict, f.DirectoryRetention,
                r.Certainty, r.Attempts, r.PreviousError, f.Checks.Select(CheckView.From).ToList());
        }
    }

    /// <summary>One second check of a file: reasons, outcome and both answers.</summary>
    public sealed record CheckView(
        IReadOnlyList<string> Reasons, string Outcome, string? RuleNote, AnswerView First, AnswerView? Second, DateTime CheckedAt)
    {
        public static CheckView From(CheckItem c) =>
            new(c.Reasons, c.Outcome, c.RuleNote, AnswerView.From(c.First), c.Second is null ? null : AnswerView.From(c.Second), c.CheckedAt);
    }

    /// <summary>One answer of a check; model texts are null for a protected file.</summary>
    public sealed record AnswerView(
        string? Category, string? Importance, double? Confidence, string? KeepHint, string? Summary,
        string? SuggestedTarget, string? KeepReason, string? ModelUsed, string? Error, string? RawResponse)
    {
        public static AnswerView From(CheckAnswerItem a) =>
            new(ReadFormat.Snake(a.Category), ReadFormat.Snake(a.Importance), a.Confidence, a.KeepHint, a.Summary,
                a.SuggestedTarget, a.KeepReason, a.ModelUsed, a.Error, a.RawResponse);
    }

    /// <summary>A directory with verdict, profile and the stored analyses.</summary>
    public sealed record DirectoryDetailView(
        string Path,
        string RelativePath,
        int Depth,
        DirectoryStatus Status,
        long SizeBytes,
        long Files,
        long DirectFiles,
        int Subdirs,
        long SensitiveFiles,
        IReadOnlyList<string> Sensitive,
        string? SensitiveRule,
        long SensitiveDirs,
        string? Verdict,
        string? VerdictSource,
        string? VerdictRule,
        string? Retention,
        string? Purpose,
        string? Reason,
        string? VerdictError,
        string? UnitDirectory,
        string? VerdictModel,
        DateTime? JudgedAt,
        string? VerdictRawResponse,
        DirectoryProfile? Profile,
        string? Summary,
        string? SummaryError,
        IReadOnlyList<DirectoryAnalysisItem> Analyses,
        bool TextWithheld)
    {
        public static DirectoryDetailView From(DirectoryDetail detail)
        {
            var d = detail.Directory;
            return new DirectoryDetailView(d.FullPath, d.RelativePath, d.Depth, d.Status, d.SizeBytes, d.Files,
                d.DirectFiles, d.Subdirs, d.SensitiveFiles, d.Sensitive, d.SensitiveRule, d.SensitiveDirs, d.Verdict, d.VerdictSource, d.VerdictRule, d.Retention,
                d.Purpose, d.Reason, d.VerdictError, detail.UnitDirectory, detail.VerdictModel, detail.JudgedAt,
                detail.VerdictRawResponse, detail.Profile, d.Summary, d.SummaryError, detail.Analyses, d.TextWithheld);
        }
    }

    /// <summary><c>kind</c> is <c>directory</c> or <c>file</c>; the other member is null.</summary>
    public sealed record ShowResult(long JobId, string Kind, DirectoryDetailView? Directory, FileDetailView? File);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var path = new Argument<string>("path")
        {
            Description = "Directory or file: full path as listed by dirs/files, or relative to the job root."
        };
        var read = new ReadOptions();

        var command = new Command("show", "Show one directory or file of a job with everything that is known about it.")
        {
            job, path, read.IncludeSensitive
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);

            PathDetail detail;
            try
            {
                detail = await services.GetRequiredService<JobReadService>()
                    .ShowAsync(jobId, ctx.ParseResult.GetValue(path)!, read.GetOutput(ctx), ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = detail.File is not null
                ? new ShowResult(jobId, "file", null, FileDetailView.From(detail.File))
                : new ShowResult(jobId, "directory", DirectoryDetailView.From(detail.Directory!), null);
            var language = ReadFormat.Language(services);
            return CommandOutcome.Success(result, () => Render(result, language));
        });

        return command;
    }

    private static string Render(ShowResult result, DisplayLanguage language)
    {
        string? Verdict(string? code) => CodeLabels.LabelOfCode<DirectoryVerdictKind>(code, language);
        string? Retention(string? code) => CodeLabels.LabelOfCode<RetentionValue>(code, language);

        var sb = new StringBuilder();
        if (result.File is { } f)
        {
            sb.AppendLine(f.Path);
            Line(sb, "type", $"{f.FileType}, {ReadFormat.Size(f.SizeBytes)}");
            Line(sb, "status", f.Status);
            Line(sb, "category", f.Category is null ? null : $"{f.Category} ({CodeLabels.LabelOfCode<ResultOrigin>(f.CategorySource, language)})");
            Line(sb, "confidence", f.Confidence?.ToString("0.00", CultureInfo.InvariantCulture));
            Line(sb, "certainty", f.Certainty);
            Line(sb, "attempts", f.Attempts is > 1 ? f.Attempts.Value.ToString(CultureInfo.InvariantCulture) : null);
            Line(sb, "previous error", f.PreviousError);
            foreach (var c in f.Checks)
            {
                Line(sb, "check", $"{c.Outcome} ({string.Join(", ", c.Reasons)}): first {c.First.Category}"
                    + (c.Second is null ? string.Empty : $", second {c.Second.Error ?? c.Second.Category}"));
            }
            Line(sb, "importance", f.Importance);
            Line(sb, "summary", f.Summary);

            Line(sb, "sensitive", f.Sensitive.Count == 0 ? null : $"{CodeLabels.Markers(f.Sensitive, language)} ({f.SensitiveRule})");
            Line(sb, "directory", Verdict(f.DirVerdict));
            Line(sb, "error", f.Error);
        }
        else if (result.Directory is { } d)
        {
            sb.AppendLine(d.Path);
            Line(sb, "size", $"{ReadFormat.Size(d.SizeBytes)}, {d.Files} files ({d.DirectFiles} direct), {d.Subdirs} subdirectories");
            Line(sb, "verdict", d.Verdict is null ? null
                : $"{Verdict(d.Verdict)} ({CodeLabels.LabelOfCode<VerdictSource>(d.VerdictSource, language)}{(d.VerdictRule is null ? "" : ": " + d.VerdictRule)})");
            Line(sb, "retention", Retention(d.Retention));
            Line(sb, "purpose", d.Purpose);
            Line(sb, "reason", d.Reason);
            Line(sb, "unit", d.UnitDirectory);
            Line(sb, "summary", d.Summary);
            Line(sb, "sensitive", d.SensitiveFiles > 0 ? $"{d.SensitiveFiles} marked files" : null);
            Line(sb, "marked", d.Sensitive.Count > 0 ? $"{CodeLabels.Markers(d.Sensitive, language)} ({d.SensitiveRule})" : null);
            Line(sb, "error", d.VerdictError ?? d.SummaryError);
        }
        return sb.ToString().TrimEnd();
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"  {label,-11} {value}");
    }
}
