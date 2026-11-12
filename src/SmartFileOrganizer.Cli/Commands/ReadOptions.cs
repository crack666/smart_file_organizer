using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// Options shared by the read commands (<c>dirs</c>, <c>files</c>, <c>show</c>, <c>summary</c>, <c>export</c>):
/// paging, the verdict, retention and sensitive filters, and <c>--include-sensitive</c>.
/// One instance per command, because System.CommandLine symbols must not be shared.
/// </summary>
public sealed class ReadOptions
{
    public Option<int> Limit { get; } = new("--limit")
    {
        Description = $"Items per page, 1 to {JobReadService.MaxLimit}.",
        DefaultValueFactory = _ => JobReadService.DefaultLimit
    };

    public Option<int> Offset { get; } = new("--offset")
    {
        Description = "Items to skip (see next_offset in the result).",
        DefaultValueFactory = _ => 0
    };

    // Codes come from VerdictCodes, the single place that defines them, so a later rename there
    // changes the accepted values and the help text without touching this file.
    public Option<string[]> Verdict { get; } = new("--verdict")
    {
        Description = $"Directory verdict: {string.Join(", ", VerdictCodes.All<DirectoryVerdictKind>())}, "
                      + $"{ReadFilterCodes.Error} (only an error stored) or {ReadFilterCodes.None} (not judged yet). Repeatable.",
        HelpName = "code"
    };

    public Option<string[]> Retention { get; } = new("--retention")
    {
        Description = $"Retention of the directory: {string.Join(", ", VerdictCodes.All<RetentionValue>())} or {ReadFilterCodes.None}. Repeatable.",
        HelpName = "code"
    };

    public Option<string?> Sensitive { get; } = new("--sensitive")
    {
        Description = "yes: only marked files (directories holding marked files); no: only the others.",
        HelpName = "yes|no"
    };

    public Option<bool> IncludeSensitive { get; } = new("--include-sensitive")
    {
        Description = "Also show model texts of marked files and of directories naming them. For a person at this machine only."
    };

    public ReadOptions()
    {
        Limit.Validators.Add(r =>
        {
            var value = r.GetValueOrDefault<int>();
            if (value < 1 || value > JobReadService.MaxLimit)
                r.AddError($"--limit must be between 1 and {JobReadService.MaxLimit}.");
        });
        Offset.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--offset must not be negative.");
        });
        Verdict.AcceptOnlyFromAmong([.. VerdictCodes.All<DirectoryVerdictKind>(), ReadFilterCodes.Error, ReadFilterCodes.None]);
        Retention.AcceptOnlyFromAmong([.. VerdictCodes.All<RetentionValue>(), ReadFilterCodes.None]);
        Sensitive.AcceptOnlyFromAmong("yes", "no");
    }

    public IEnumerable<Option> Paging => [Limit, Offset];
    public IEnumerable<Option> Filters => [Verdict, Retention, Sensitive];

    public bool? GetSensitive(ParseResult parseResult) => parseResult.GetValue(Sensitive) switch
    {
        "yes" => true,
        "no" => false,
        _ => null
    };

    public SensitiveOutputOptions GetOutput(CommandContext ctx)
    {
        var include = ctx.ParseResult.GetValue(IncludeSensitive);
        if (include)
            ctx.Note("--include-sensitive: model texts of marked files are shown. Do not pass this output on.");
        return new SensitiveOutputOptions { IncludeSensitive = include };
    }

    /// <summary>Fills the shared fields of a query.</summary>
    public T Apply<T>(T query, CommandContext ctx) where T : ReadQuery => query with
    {
        Verdicts = ctx.ParseResult.GetValue(Verdict) ?? [],
        Retentions = ctx.ParseResult.GetValue(Retention) ?? [],
        Sensitive = GetSensitive(ctx.ParseResult),
        Limit = ctx.ParseResult.GetValue(Limit),
        Offset = ctx.ParseResult.GetValue(Offset),
        Output = GetOutput(ctx)
    };
}

/// <summary>Parsing and formatting helpers of the read commands.</summary>
public static class ReadFormat
{
    /// <summary>Enum value names as the contract writes them: <c>snake_case</c>.</summary>
    public static string? Snake(string? value) =>
        value is null ? null : JsonNamingPolicy.SnakeCaseLower.ConvertName(value);

    /// <summary>Snake-case codes of every member, for help texts and validation.</summary>
    public static string[] Codes<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Select(n => Snake(n)!).ToArray();

    /// <summary>Accepts <c>review_needed</c>, <c>ReviewNeeded</c> or <c>review-needed</c>.</summary>
    public static bool TryParseEnum<TEnum>(string value, out TEnum result) where TEnum : struct, Enum =>
        Enum.TryParse(value.Replace("_", string.Empty).Replace("-", string.Empty), ignoreCase: true, out result)
        && Enum.IsDefined(result)
        && !int.TryParse(value, out _);

    /// <summary>An option taking enum values in snake_case, repeatable, validated at parse time.</summary>
    public static Option<string[]> EnumOption<TEnum>(string name, string what, params string[] extra) where TEnum : struct, Enum
    {
        var codes = Codes<TEnum>().Concat(extra).ToArray();
        var option = new Option<string[]>(name)
        {
            Description = $"{what}: {string.Join(", ", codes)}. Repeatable.",
            HelpName = "value"
        };
        option.Validators.Add(r =>
        {
            foreach (var value in r.GetValueOrDefault<string[]>() ?? [])
            {
                if (!extra.Contains(value, StringComparer.OrdinalIgnoreCase) && !TryParseEnum<TEnum>(value, out _))
                    r.AddError($"Unknown value '{value}' for {name}. Use one of: {string.Join(", ", codes)}.");
            }
        });
        return option;
    }

    public static List<TEnum> ParseEnums<TEnum>(IEnumerable<string>? values) where TEnum : struct, Enum =>
        (values ?? []).Select(v => TryParseEnum<TEnum>(v, out var e) ? (TEnum?)e : null)
            .Where(e => e.HasValue).Select(e => e!.Value).Distinct().ToList();

    /// <summary>Bytes, or a number with K, M, G or T (powers of 1024), for example <c>500M</c>.</summary>
    public static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToUpperInvariant();
        if (s.EndsWith('B')) s = s[..^1];

        long factor = 1;
        if (s.Length > 0 && "KMGT".IndexOf(s[^1]) is var i and >= 0)
        {
            factor = 1L << (10 * (i + 1));
            s = s[..^1];
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;
        bytes = (long)Math.Round(value * factor);
        return true;
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Maps a failed read query to the contract's error codes.</summary>
    public static CliFailureException ToFailure(JobReadException ex) => ex.Failure switch
    {
        ReadFailure.JobNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, ex.Message),
        ReadFailure.BundleNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.BundleNotFound, ex.Message),
        _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, ex.Message)
    };

    public static string PageFooter<T>(ReadPage<T> page) =>
        page.Items.Count == 0
            ? $"0 of {page.Total}"
            : $"{page.Offset + 1}-{page.Offset + page.Items.Count} of {page.Total}"
              + (page.NextOffset is { } next ? $" (next: --offset {next})" : string.Empty);
}

/// <summary>A page as every list command writes it.</summary>
public sealed record PageView<T>(long JobId, long Total, int Limit, int Offset, int? NextOffset, IReadOnlyList<T> Items)
{
    public static PageView<T> From<TSource>(long jobId, ReadPage<TSource> page, Func<TSource, T> map) =>
        new(jobId, page.Total, page.Limit, page.Offset, page.NextOffset, page.Items.Select(map).ToList());
}

/// <summary>A directory in <c>dirs</c> and <c>summary</c>. Sizes and file counts cover the subtree.</summary>
public sealed record DirectoryView(
    string Path,
    string RelativePath,
    int Depth,
    DirectoryStatus Status,
    long SizeBytes,
    long Files,
    long DirectFiles,
    int Subdirs,
    string? Verdict,
    string? VerdictSource,
    string? Retention,
    long SensitiveFiles,
    string? Purpose,
    string? Summary,
    bool TextWithheld)
{
    public static DirectoryView From(DirectoryItem d) => new(
        d.FullPath, d.RelativePath, d.Depth, d.Status, d.SizeBytes, d.Files, d.DirectFiles, d.Subdirs,
        d.Verdict ?? (d.VerdictError is not null ? ReadFilterCodes.Error : null), d.VerdictSource, d.Retention,
        d.SensitiveFiles, d.Purpose, d.Summary, d.TextWithheld);

    public string ToLine() =>
        $"{ReadFormat.Size(SizeBytes),10}  {Files,7} files  {Verdict ?? "-",-11} {Retention ?? "-",-11}"
        + (SensitiveFiles > 0 ? $" [{SensitiveFiles} sensitive]" : string.Empty)
        + $"  {Path}";
}

/// <summary>A file in <c>files</c>: what the output guard lets out, without raw model output.</summary>
public sealed record FileListView(
    string Path,
    string Name,
    string? FileType,
    long SizeBytes,
    DateTime? LastWriteTime,
    string? Status,
    string? Category,
    string? CategorySource,
    double? Confidence,
    string? Summary,
    IReadOnlyList<string> Sensitive,
    bool Protected,
    string? DirVerdict,
    string? Certainty)
{
    public static FileListView From(FileItem f)
    {
        var r = f.Record;
        return new FileListView(r.FullPath, r.Name, ReadFormat.Snake(r.FileType), r.SizeBytes, r.LastWriteTime,
            ReadFormat.Snake(r.Status), ReadFormat.Snake(r.Category), r.CategorySource, r.Confidence, r.Summary,
            r.Sensitive, r.Protected, f.DirectoryVerdict, r.Certainty);
    }

    public string ToLine() =>
        $"{Category ?? "-",-18} {(Confidence is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "-"),5}  {Certainty ?? "-",-9} {Status,-12}"
        + (Sensitive.Count > 0 ? $" [{string.Join(',', Sensitive)}]" : string.Empty)
        + $"  {Path}";
}
