using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Decisions;

/// <summary>
/// Proposes a target for a kept file from what stage 1 stored about it. The seam of stage 2
/// (https://github.com/crack666/smart_file_organizer/issues/37): a proposal from a target structure
/// (<see cref="TargetStructureProposer"/>) today; a choice by content against the library folders
/// (https://github.com/crack666/smart_file_organizer/issues/56) plugs in here. Every proposal names its inputs.
/// </summary>
public interface ITargetProposer
{
    /// <summary>The target of a kept file, or null when none fits. Must not call a model on the file's content.</summary>
    TargetProposal? Propose(FileNode file, IReadOnlyList<FileFact> facts);
}

/// <summary>
/// Targets from a target structure (<c>sfo library propose --out</c>, edited by hand; format in
/// docs/library-review.md): the first target whose <c>holds</c> names the file type, its pattern filled from the
/// facts (<c>{occasion} {year}</c> becomes <c>holiday 2019</c>). A placeholder no fact fills leaves the folder level
/// of the pattern out and is named in <see cref="TargetProposal.Unresolved"/>. A changed structure gives new
/// targets on the next <c>sfo decide</c>; the facts stay as they are.
/// </summary>
public sealed partial class TargetStructureProposer(TargetStructure structure) : ITargetProposer
{
    public const string RuleCode = "target_structure";

    /// <summary>Placeholders by name, German names of <c>sfo library propose --lang de</c> included.</summary>
    private static readonly Dictionary<string, FileFactKey> Placeholders = BuildPlaceholders();

    public TargetStructure Structure { get; } = structure;

    public TargetProposal? Propose(FileNode file, IReadOnlyList<FileFact> facts)
    {
        var type = DecisionRules.FileTypeCode(file.FileType);
        var target = Structure.Targets.FirstOrDefault(t => (t.Holds ?? []).Contains(type, StringComparer.Ordinal));
        if (target is null) return null;

        var basis = new List<DecisionInput> { DecisionRules.FileTypeBasis(file) };
        var unresolved = new List<string>();
        var path = target.Path.Replace('\\', '/').Trim('/');
        if (!string.IsNullOrWhiteSpace(target.Pattern))
        {
            var filled = PlaceholderRegex().Replace(target.Pattern, m =>
            {
                var name = m.Groups[1].Value;
                if (Resolve(name, facts) is { } fact)
                {
                    if (!basis.Contains(fact.Basis)) basis.Add(fact.Basis);
                    return Clean(fact.Value);
                }
                unresolved.Add(name);
                return string.Empty;
            });
            if (unresolved.Count == 0)
            {
                var segments = filled.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().TrimEnd('.', ' ')).Where(s => s.Length > 0).ToList();
                if (segments.Count > 0) path = path + "/" + string.Join('/', segments);
            }
        }
        return new TargetProposal(target.Id, path, RuleCode, basis, unresolved);
    }

    private sealed record Filled(string Value, DecisionInput Basis);

    private static Filled? Resolve(string placeholder, IReadOnlyList<FileFact> facts)
    {
        if (!Placeholders.TryGetValue(placeholder.ToLowerInvariant(), out var key)) return null;
        if (key == FileFactKey.Year)
        {
            // The year a document is dated, else the year a photo was taken, else the creation date of the properties.
            if (DecisionRules.Of(facts, FileFactKey.Year) is { } year) return new Filled(year.Value, DecisionRules.Of(year));
            foreach (var dated in new[] { FileFactKey.TakenAt, FileFactKey.CreatedAt })
            {
                if (DecisionRules.Of(facts, dated) is { Value.Length: >= 4 } d
                    && int.TryParse(d.Value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2100)
                    return new Filled(d.Value[..4], DecisionRules.Of(d));
            }
            return null;
        }
        var fact = DecisionRules.Of(facts, key);
        if (fact is null || string.IsNullOrWhiteSpace(fact.Value)) return null;
        // "No occasion" names no folder.
        if (key == FileFactKey.Occasion && fact.Value == OccasionKind.None.ToCode()) return null;
        return new Filled(fact.Value, DecisionRules.Of(fact));
    }

    /// <summary>A fact as one folder name: no separators or characters a file system refuses.</summary>
    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        return sb.ToString().Trim();
    }

    private static Dictionary<string, FileFactKey> BuildPlaceholders()
    {
        var map = new Dictionary<string, FileFactKey>(StringComparer.Ordinal);
        foreach (var key in Enum.GetValues<FileFactKey>()) map[key.ToCode()] = key;
        map["area"] = FileFactKey.DocumentKind;
        map["bereich"] = FileFactKey.DocumentKind;
        map["anlass"] = FileFactKey.Occasion;
        map["jahr"] = FileFactKey.Year;
        map["titel"] = FileFactKey.Title;
        map["interpret"] = FileFactKey.Artist;
        map["gerät"] = FileFactKey.Device;
        map["geraet"] = FileFactKey.Device;
        map["aussteller"] = FileFactKey.Issuer;
        map["jahreszeit"] = FileFactKey.Season;
        map["szene"] = FileFactKey.Scene;
        map["art"] = FileFactKey.DocumentKind;
        return map;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
