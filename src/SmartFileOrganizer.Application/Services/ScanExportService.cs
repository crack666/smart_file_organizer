using System.Globalization;
using System.Text;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Exports the result of a scan job as CSV.
///
/// Why this exists: the scan result currently only lives in the SQLite file and in the
/// desktop UI. Everything a human wants to do with it afterwards — sort 16k rows, filter
/// trash candidates, hand the list to somebody else, diff two runs — is spreadsheet work.
/// See docs/next-session-handover.md, "Immediate next priorities" #3.
///
/// The export is deliberately read-only: it never touches a file on disk other than the
/// output stream, and it never writes back into the database.
///
/// Every row goes through <see cref="SensitiveOutputGuard"/>: for a marked file the export
/// contains path, type, marker and a neutral summary only, unless
/// <see cref="SensitiveOutputOptions.IncludeSensitive"/> is set.
/// </summary>
public class ScanExportService
{
    /// <summary>
    /// Column order of the exported CSV. Kept as a constant so a test can pin it —
    /// a silently reordered column breaks every downstream sheet.
    /// </summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "full_path",
        "name",
        "relative_dir",
        "extension",
        "file_type",
        "size_bytes",
        "last_write_time",
        "status",
        "category",
        "category_source",
        "importance",
        "confidence",
        "summary",
        "suggested_target",
        "model_used",
        "analyzed_at",
        "error",
        // Appended, not inserted: existing sheets address the columns above by position.
        "sensitive",
        "sensitive_rule"
    ];

    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileRepository _fileRepo;

    public ScanExportService(IDirectoryRepository dirRepo, IFileRepository fileRepo)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
    }

    /// <summary>
    /// Writes every file of <paramref name="jobId"/> to <paramref name="writer"/> as CSV
    /// and returns the number of data rows written (header not counted).
    /// </summary>
    public Task<int> ExportJobAsync(
        long jobId, TextWriter writer, CancellationToken ct = default)
        => ExportJobAsync(jobId, writer, SensitiveOutputOptions.Default, ct);

    /// <summary>
    /// As <see cref="ExportJobAsync(long, TextWriter, CancellationToken)"/>, with explicit
    /// protection options (<c>--include-sensitive</c>).
    /// </summary>
    public async Task<int> ExportJobAsync(
        long jobId, TextWriter writer, SensitiveOutputOptions? options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WriteHeader(writer);

        var rows = 0;
        var directories = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);

        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();

            var files = await _fileRepo.GetByDirectoryAsync(jobId, dir.FullPath, ct);
            foreach (var file in files)
            {
                WriteRow(writer, file, options);
                rows++;
            }
        }

        await writer.FlushAsync(ct);
        return rows;
    }

    /// <summary>
    /// Convenience overload that writes to a file. UTF-8 <b>with</b> BOM on purpose:
    /// without it Excel opens the file as ANSI and mangles every umlaut in a filename,
    /// which on a German machine is most of them.
    /// </summary>
    public async Task<int> ExportJobToFileAsync(
        long jobId, string path, CancellationToken ct = default)
        => await ExportJobToFileAsync(jobId, path, SensitiveOutputOptions.Default, ct);

    /// <inheritdoc cref="ExportJobToFileAsync(long, string, CancellationToken)"/>
    public async Task<int> ExportJobToFileAsync(
        long jobId, string path, SensitiveOutputOptions? options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return await ExportJobAsync(jobId, writer, options, ct);
    }

    public static void WriteHeader(TextWriter writer)
        => writer.Write(string.Join(',', Columns) + "\r\n");

    public static void WriteRow(TextWriter writer, FileNode file, SensitiveOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        // The guard decides what may leave the machine; this method only formats.
        var r = SensitiveOutputGuard.Project(file, options);

        var fields = new[]
        {
            r.FullPath,
            r.Name,
            r.RelativeDir,
            r.Extension,
            r.FileType,
            r.SizeBytes.ToString(CultureInfo.InvariantCulture),
            r.LastWriteTime is { } lwt ? FormatTimestamp(lwt) : string.Empty,
            r.Status,
            r.Category,
            r.CategorySource,
            r.Importance,
            r.Confidence is { } conf ? conf.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
            r.Summary,
            r.SuggestedTarget,
            r.ModelUsed,
            r.AnalyzedAt is { } at ? FormatTimestamp(at) : string.Empty,
            r.Error,
            string.Join(',', r.Sensitive),
            r.SensitiveRule
        };

        writer.Write(string.Join(',', fields.Select(Escape)) + "\r\n");
    }

    private static string FormatTimestamp(DateTime value)
        => value == default
            ? string.Empty
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC 4180 escaping. Quoting is only applied where it is actually needed, so the
    /// common case (a path without a comma) stays readable in a plain text editor.
    /// A leading '=', '+', '-' or '@' is additionally prefixed with a single quote:
    /// AI summaries are free text, and Excel executes such a cell as a formula.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = value;
        if (text[0] is '=' or '+' or '-' or '@') text = "'" + text;

        var needsQuotes = text.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!needsQuotes) return text;

        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
