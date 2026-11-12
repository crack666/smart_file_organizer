using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// Runs the file rules again for rows scanned with an older rule version (or before versions were stored),
/// so the rules that read the file (private-key header lines, KeePass signature) have run before any of its
/// content reaches a model. The name rules need no catching up: <see cref="SensitiveOutputGuard"/> evaluates
/// them on every output.
/// </summary>
/// <remarks>
/// Markers are only ever added, never removed: a file a user or an earlier rule marked stays marked.
/// For a file that becomes marked here, stored facts (evidence written before the marker) are dropped.
/// A file that no longer exists keeps its name markers and gets the current version, so it is not read again.
/// </remarks>
public sealed class SensitiveRulesBackfill
{
    private readonly IFileRepository _files;
    private readonly IFileFactRepository _facts;
    private readonly IFileSystemAccessor _fs;
    private readonly ILogger<SensitiveRulesBackfill> _logger;

    public SensitiveRulesBackfill(
        IFileRepository files,
        IFileFactRepository facts,
        IFileSystemAccessor fs,
        ILogger<SensitiveRulesBackfill> logger)
    {
        _files = files;
        _facts = facts;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>Catches up all files of the job; returns how many became newly marked.</summary>
    public async Task<int> RunAsync(long jobId, CancellationToken ct = default)
    {
        var stale = await _files.GetBelowRulesVersionAsync(jobId, SensitiveFileRules.Version, ct);
        if (stale.Count == 0) return 0;

        var newlyMarked = new List<FileNode>();
        foreach (var file in stale)
        {
            ct.ThrowIfCancellationRequested();

            var path = file.FullPath;
            Func<int, byte[]>? readPrefix = _fs.FileExists(path) ? maxBytes => _fs.ReadPrefix(path, maxBytes) : null;
            var match = SensitiveFileRules.Detect(file.Name, file.ParentPath, file.FileType, file.Size, readPrefix);

            var before = file.SensitiveMarkers;
            var added = match.Markers & ~before;
            if (added != SensitiveMarker.None)
            {
                file.SensitiveMarkers = before | match.Markers;
                file.SensitiveRule = string.IsNullOrEmpty(file.SensitiveRule) || before == SensitiveMarker.None
                    ? match.Rule
                    : file.SensitiveRule + ";" + match.Rule;
                if (before == SensitiveMarker.None)
                    newlyMarked.Add(file);
            }

            file.SensitiveRulesVersion = SensitiveFileRules.Version;
        }

        await _files.UpdateSensitiveAsync(stale, ct);

        foreach (var file in newlyMarked)
        {
            var facts = await _facts.GetByFileNodeIdAsync(file.Id, ct);
            if (facts.Count > 0)
                await _facts.ReplaceAsync(file.Id, [], ct);
        }

        _logger.LogInformation(
            "File rules version {Version} applied to {Count} older file(s) of job {JobId}; {Marked} newly marked.",
            SensitiveFileRules.Version, stale.Count, jobId, newlyMarked.Count);
        return newlyMarked.Count;
    }
}
