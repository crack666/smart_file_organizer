using System.Text;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Draws and evaluates the sample of a uniform directory. Pure functions: no database, no model.
/// </summary>
/// <remarks>
/// <para><b>Draw.</b> Sensitive files are always members and come on top of the sample size: they never
/// inherit. Of the other files, the outliers come first (files the pre-assessment named as anomalous,
/// smallest and largest, oldest and newest, sizes ten times off the median), at most a third of the sample.
/// The rest is filled per extension group, every group at least once and otherwise in proportion to its
/// size. Within a group the order is a hash of the seed and the file name, so the same directory in the
/// same job always draws the same files, regardless of listing order. A directory with fewer files than
/// the sample size is drawn completely: every file is judged, and the sample still checks the verdict.</para>
/// <para><b>Evaluate.</b> Members are counted by purpose (<see cref="PurposeOf"/>): photos and videos of one
/// occasion are one purpose, and so are the kinds of documents. The majority purpose is the most frequent one
/// among the judged members; failed members count as deviating. The sample confirms only if there is a single
/// clear majority, it is a real category (not <c>Unknown</c> or <c>ReviewNeeded</c>), and the deviating members
/// are no more than <see cref="DirectoryAnalysisOptions.MaxDeviationShare"/> of the sample, at least one. Anything
/// else raises the directory to mixed. Two exceptions
/// (https://github.com/crack666/smart_file_organizer/issues/50):</para>
/// <list type="bullet">
/// <item>A directory with no more eligible files than <see cref="DirectoryAnalysisOptions.SmallDirectoryMaxFiles"/>
/// keeps the verdict of its pre-assessment: every file is judged on its own, nothing inherits, and two or three
/// files make no majority worth the name.</item>
/// <item>A member the model marks sensitive although the rules did not keeps its marker, and the directory keeps
/// its verdict; but nothing inherits (<see cref="Evaluation.Inherit"/> is false), because other files may hold
/// more of the same, so every other file is judged on its own.</item>
/// </list>
/// </remarks>
public static class DirectorySampler
{
    /// <summary>Hand-picked outliers take at most this share of the sample, so the strata still dominate.</summary>
    private const int OutlierShareDivisor = 3;

    /// <summary>A size this many times above or below the median counts as an outlier.</summary>
    private const double SizeOutlierFactor = 10;

    public sealed record DrawResult(IReadOnlyList<SampleMember> Members, int DeferredCount, string? NotSampleableReason);

    /// <param name="Confirmed">The uniform verdict stands.</param>
    /// <param name="Majority">The most frequent category of the majority purpose: what the other files inherit.</param>
    /// <param name="Agreeing">Members of the majority purpose.</param>
    /// <param name="Inherit">Whether the other files may inherit; false when the model found sensitive content the
    /// rules had missed, so every other file is judged on its own.</param>
    public sealed record Evaluation(
        bool Confirmed,
        FileCategory? Majority,
        int Agreeing,
        int Deviating,
        int Failed,
        double Share,
        Importance InheritedImportance,
        string? InheritedTarget,
        double InheritedConfidence,
        string Reason,
        bool Inherit = true);

    /// <summary>
    /// The purpose a category counts for when a sample is evaluated. Photos and videos taken at one occasion
    /// belong together; invoices, letters, personal documents, scans and other documents are kinds of documents
    /// and together make a document folder. Every other category is a purpose of its own.
    /// </summary>
    public static FileCategory PurposeOf(FileCategory category) => category switch
    {
        FileCategory.Video => FileCategory.Photo,
        FileCategory.Invoice or FileCategory.Letter or FileCategory.PersonalDocument or FileCategory.DocumentScan => FileCategory.Document,
        _ => category
    };

    /// <summary>
    /// Deviating or failed members a sample of <paramref name="members"/> tolerates: the share
    /// <see cref="DirectoryAnalysisOptions.MaxDeviationShare"/>, rounded down, but at least one when the share is
    /// above zero, so a single outlier never tips a small sample (one in six used to be 17 %, above 10 %).
    /// </summary>
    public static int ToleratedDeviations(int members, DirectoryAnalysisOptions options) =>
        options.MaxDeviationShare <= 0 ? 0 : Math.Max(1, (int)Math.Floor(options.MaxDeviationShare * members + 1e-9));

    /// <summary>Start value of a draw: job and directory path relative to the scan root.</summary>
    public static string Seed(long jobId, string relativePath) =>
        Fnv1a($"{jobId}\n{relativePath.Replace('\\', '/')}").ToString("x16");

    /// <summary>Extension group: lower case, with common spellings of the same format merged.</summary>
    public static string ExtensionGroup(string extension)
    {
        var e = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return e switch
        {
            ".jpeg" or ".jpe" or ".jfif" => ".jpg",
            ".tif" => ".tiff",
            ".htm" => ".html",
            ".mpeg" => ".mpg",
            _ => e
        };
    }

    /// <summary>
    /// Draws the sample from the eligible files of one directory.
    /// <paramref name="sampleSize"/> limits the members that are not sensitive.
    /// </summary>
    public static DrawResult Draw(
        IReadOnlyList<FileNode> eligible,
        string seed,
        int sampleSize,
        IReadOnlySet<string> anomalousNames)
    {
        var members = new List<SampleMember>();
        var chosen = new HashSet<long>();

        void Add(FileNode f, SampleRole role)
        {
            if (chosen.Add(f.Id))
                members.Add(new SampleMember { FileNodeId = f.Id, Name = f.Name, Role = role });
        }

        foreach (var f in eligible.Where(SensitiveOutputGuard.IsSensitive).OrderBy(f => f.Name, StringComparer.Ordinal))
            Add(f, SampleRole.Sensitive);

        var pool = eligible.Where(f => !chosen.Contains(f.Id)).ToList();
        if (pool.Count <= sampleSize)
        {
            foreach (var f in pool.OrderBy(f => f.Name, StringComparer.Ordinal))
                Add(f, SampleRole.Stratum);
            return new DrawResult(members, 0, null);
        }

        var rank = pool.ToDictionary(f => f.Id, f => Fnv1a(seed + "\n" + f.Name));
        var groups = pool
            .GroupBy(f => ExtensionGroup(f.Extension))
            .Select(g => new Group(g.Key, g.OrderBy(f => rank[f.Id]).ThenBy(f => f.Name, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Files.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (groups.Count > sampleSize)
        {
            return new DrawResult(members, 0,
                $"{groups.Count} extension groups, more than the sample size of {sampleSize}; every group could not be drawn once.");
        }

        // Outliers first, within their budget; the budget leaves one slot for every extension group.
        var outlierBudget = Math.Min(sampleSize - groups.Count, Math.Max(2, sampleSize / OutlierShareDivisor));
        var outliers = 0;
        void AddOutlier(FileNode? f, SampleRole role)
        {
            if (f is null || outliers >= outlierBudget || chosen.Contains(f.Id)) return;
            Add(f, role);
            outliers++;
        }

        foreach (var f in pool.Where(f => anomalousNames.Contains(f.Name)).OrderBy(f => rank[f.Id]))
            AddOutlier(f, SampleRole.Anomaly);

        var bySize = pool.OrderBy(f => f.Size).ThenBy(f => rank[f.Id]).ToList();
        var byDate = pool.OrderBy(f => f.LastWriteTime).ThenBy(f => rank[f.Id]).ToList();
        AddOutlier(bySize[0], SampleRole.Size);
        AddOutlier(bySize[^1], SampleRole.Size);
        AddOutlier(byDate[0], SampleRole.Date);
        AddOutlier(byDate[^1], SampleRole.Date);

        var median = Math.Max(1, bySize[bySize.Count / 2].Size);
        foreach (var f in pool
                     .Select(f => (File: f, Ratio: Math.Abs(Math.Log(Math.Max(1, f.Size) / (double)median))))
                     .Where(x => x.Ratio >= Math.Log(SizeOutlierFactor))
                     .OrderByDescending(x => x.Ratio).ThenBy(x => rank[x.File.Id])
                     .Select(x => x.File))
        {
            AddOutlier(f, SampleRole.Size);
        }

        // Strata: every group once, then the remaining slots by largest deficit against its proportional share.
        var taken = groups.ToDictionary(g => g.Key, g => g.Files.Count(f => chosen.Contains(f.Id)));
        var nonSensitiveTarget = sampleSize;
        int Remaining() => nonSensitiveTarget - (members.Count - members.Count(m => m.Role == SampleRole.Sensitive));

        foreach (var g in groups.Where(g => taken[g.Key] == 0))
        {
            Add(g.Files.First(f => !chosen.Contains(f.Id)), SampleRole.Stratum);
            taken[g.Key]++;
        }

        while (Remaining() > 0)
        {
            var next = groups
                .Where(g => g.Files.Any(f => !chosen.Contains(f.Id)))
                .Select(g => (Group: g, Deficit: sampleSize * (double)g.Files.Count / pool.Count - taken[g.Key]))
                .OrderByDescending(x => x.Deficit)
                .ThenByDescending(x => x.Group.Files.Count)
                .ThenBy(x => x.Group.Key, StringComparer.Ordinal)
                .Select(x => x.Group)
                .FirstOrDefault();
            if (next is null) break;

            Add(next.Files.First(f => !chosen.Contains(f.Id)), SampleRole.Stratum);
            taken[next.Key]++;
        }

        return new DrawResult(members, pool.Count(f => !chosen.Contains(f.Id)), null);
    }

    /// <summary>
    /// Evaluates a sample whose members are all done. <paramref name="results"/> holds the stored result of
    /// each member, or null when there is none. Fills <see cref="SampleMember.Category"/>,
    /// <see cref="SampleMember.Deviates"/> and <see cref="SampleMember.Error"/> on the members.
    /// </summary>
    /// <param name="eligibleCount">Eligible files of the directory when the sample was drawn; null when unknown,
    /// which never counts as a small directory.</param>
    public static Evaluation Evaluate(
        IReadOnlyList<SampleMember> members,
        IReadOnlyDictionary<long, FileClassification?> results,
        DirectoryAnalysisOptions options,
        int? eligibleCount = null)
    {
        var judged = new List<(SampleMember Member, FileClassification Result)>();
        foreach (var m in members)
        {
            var r = results.GetValueOrDefault(m.FileNodeId);
            if (r is null || r.Error is not null)
            {
                m.Category = null;
                m.Error = r?.Error ?? "no result";
                m.Deviates = true;
                continue;
            }
            m.Category = r.Category;
            m.Error = null;
            judged.Add((m, r));
        }

        var failed = members.Count - judged.Count;
        var small = eligibleCount is { } n && n <= options.SmallDirectoryMaxFiles;
        Evaluation Escalate(FileCategory? majority, int agreeing, int deviating, double share, string reason) =>
            small
                // Every file of a small directory is a member and judged on its own; the sample only describes it.
                ? new(true, majority, agreeing, deviating, failed, share, Importance.Unknown, null, 0,
                    $"Kept: {eligibleCount} file(s), each judged on its own; a directory of at most " +
                    $"{options.SmallDirectoryMaxFiles} files keeps the verdict of its pre-assessment ({reason.TrimEnd('.')}).",
                    Inherit: false)
                : new(false, majority, agreeing, deviating, failed, share, Importance.Unknown, null, 0, reason);

        if (members.Count == 0)
            return Escalate(null, 0, 0, 1, "The sample is empty.");
        if (judged.Count == 0)
            return Escalate(null, 0, 0, 1, $"None of the {members.Count} sample files could be judged.");

        var purposes = judged.GroupBy(j => PurposeOf(j.Result.Category))
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
        var purpose = purposes[0].Category;
        var agreeing = purposes[0].Count;
        foreach (var (m, r) in judged)
            m.Deviates = PurposeOf(r.Category) != purpose;

        // What the other files inherit: the most frequent category of the majority purpose; on a tie the general one.
        var kindsOfPurpose = judged.Where(j => PurposeOf(j.Result.Category) == purpose)
            .GroupBy(j => j.Result.Category)
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Category == purpose)
            .ThenBy(x => x.Category)
            .ToList();
        var majority = kindsOfPurpose[0].Category;

        var deviating = judged.Count - agreeing;
        var share = (deviating + failed) / (double)members.Count;
        var tolerated = ToleratedDeviations(members.Count, options);
        var kinds = kindsOfPurpose.Count > 1 ? $" or a related kind ({Describe(kindsOfPurpose)})" : string.Empty;
        var summary = $"{agreeing} of {members.Count} sample files are {majority}{kinds}, {deviating} deviate, {failed} failed " +
                      $"({share:P0} off; at most {tolerated} tolerated)";

        if (purposes.Count > 1 && purposes[1].Count == agreeing)
            return Escalate(null, agreeing, deviating, share, $"No clear majority: {Describe(purposes)}.");

        if (majority is FileCategory.Unknown or FileCategory.ReviewNeeded)
            return Escalate(majority, agreeing, deviating, share,
                $"The sample gives no usable category to inherit: {summary}.");

        if (deviating + failed > tolerated)
        {
            var off = judged.Where(j => j.Member.Deviates == true)
                .Select(j => $"{j.Member.Name} ({j.Result.Category})")
                .Concat(members.Where(m => m.Category is null).Select(m => $"{m.Name} (failed)"))
                .Take(8);
            return Escalate(majority, agreeing, deviating, share, $"{summary}. Off: {string.Join(", ", off)}.");
        }

        if (small)
        {
            return new Evaluation(true, majority, agreeing, deviating, failed, share, Importance.Unknown, null, 0,
                $"Confirmed: {summary}. A directory of at most {options.SmallDirectoryMaxFiles} files keeps the verdict " +
                "of its pre-assessment; each file is judged on its own.", Inherit: false);
        }

        // A marker the rules missed marks the file, not the directory; but other files may hold more of the same,
        // so none of them inherits.
        var unexpected = judged
            .Where(j => j.Member.Role != SampleRole.Sensitive && j.Result.SensitiveMarkers != SensitiveMarker.None)
            .Select(j => j.Member.Name)
            .ToList();

        var majorityResults = judged.Where(j => j.Result.Category == majority).Select(j => j.Result).ToList();
        var importance = majorityResults.GroupBy(r => r.Importance)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;
        // The target is model text about the file it was suggested for; unmarked files inherit it, so a
        // marked member does not take part (https://github.com/crack666/smart_file_organizer/issues/33).
        var target = judged
            .Where(j => j.Result.Category == majority && j.Member.Role != SampleRole.Sensitive &&
                        j.Result.SensitiveMarkers == SensitiveMarker.None)
            .Select(j => j.Result.SuggestedTarget?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .GroupBy(t => t!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        var confidence = Math.Min(options.InheritedConfidenceCap, majorityResults.Min(r => r.Confidence));

        if (unexpected.Count > 0)
        {
            return new Evaluation(true, majority, agreeing, deviating, failed, share, importance, target, confidence,
                $"Confirmed: {summary}. The model found sensitive content the rules had not marked " +
                $"({string.Join(", ", unexpected.Take(5))}); other files may hold more, so they are judged one by one " +
                "instead of inheriting.", Inherit: false);
        }

        return new Evaluation(true, majority, agreeing, deviating, failed, share, importance, target, confidence,
            $"Confirmed: {summary}.");
    }

    private static string Describe(IEnumerable<(FileCategory Category, int Count)> counts) =>
        string.Join(", ", counts.Select(c => $"{c.Category} {c.Count}"));

    /// <summary>FNV-1a, 64 bit: stable across processes and runtimes, unlike <see cref="string.GetHashCode()"/>.</summary>
    internal static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private sealed record Group(string Key, List<FileNode> Files);
}
