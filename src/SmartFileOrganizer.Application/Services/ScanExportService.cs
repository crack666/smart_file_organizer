using System.Globalization;
using System.Text;
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
        "error"
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
    public async Task<int> ExportJobAsync(
        long jobId, TextWriter writer, CancellationToken ct = default)
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
                WriteRow(writer, file);
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return await ExportJobAsync(jobId, writer, ct);
    }

    public static void WriteHeader(TextWriter writer)
        => writer.Write(string.Join(',', Columns) + "\r\n");

    public static void WriteRow(TextWriter writer, FileNode file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var ai = file.Classification;
        var ovr = file.Override;

        // A human correction outranks the model. Whoever reads the sheet wants the
        // category that is currently true, not the one the model guessed first.
        var category = ovr is not null
            ? ovr.OverriddenCategory.ToString()
            : ai?.Category.ToString() ?? string.Empty;
        var categorySource = ovr is not null ? "user" : ai is not null ? "ai" : string.Empty;
        var suggestedTarget = ovr?.OverriddenTarget ?? ai?.SuggestedTarget;

        var fields = new[]
        {
            file.FullPath,
            file.Name,
            file.RelativeDir,
            file.Extension,
            file.FileType.ToString(),
            file.Size.ToString(CultureInfo.InvariantCulture),
            FormatTimestamp(file.LastWriteTime),
            file.Status.ToString(),
            category,
            categorySource,
            ai?.Importance.ToString() ?? string.Empty,
            ai is not null ? ai.Confidence.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
            ai?.Summary,
            suggestedTarget,
            ai?.ModelUsed,
            ai is not null ? FormatTimestamp(ai.AnalyzedAt) : string.Empty,
            ai?.Error
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
