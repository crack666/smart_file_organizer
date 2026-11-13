using System.Text.Json;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Decisions;

/// <summary>
/// The rules of stage 2 (https://github.com/crack666/smart_file_organizer/issues/37): the worth of a file from its
/// stored facts, the result of the analysis, the verdict of its folder and its bundle, with the inputs it rests on.
/// Pure: no database, no model. The findings use the same worth, so a finding and <c>sfo decide</c> never disagree.
/// </summary>
public static class DecisionRules
{
    /// <summary>Kinds of papers about the person: kept whatever importance the same answer gave.</summary>
    private static readonly HashSet<string> KeptDocumentKinds = new(StringComparer.Ordinal)
    {
        DocumentKind.Contract.ToCode(), DocumentKind.Statement.ToCode(), DocumentKind.Tax.ToCode(),
        DocumentKind.Payslip.ToCode(), DocumentKind.Certificate.ToCode(), DocumentKind.Insurance.ToCode(),
        DocumentKind.Medical.ToCode(), DocumentKind.Official.ToCode()
    };

    /// <summary>Occasions people look for again.</summary>
    private static readonly HashSet<string> KeptOccasions = new(StringComparer.Ordinal)
    {
        OccasionKind.Holiday.ToCode(), OccasionKind.Celebration.ToCode(), OccasionKind.Outing.ToCode(), OccasionKind.Sport.ToCode()
    };

    /// <summary>
    /// The worth of a file. First the rules of the findings (<see cref="FindingRules.ValueOf"/>: a person's override,
    /// the result of the analysis, folder verdict, bundle, file type); then the facts may keep a file those rules give
    /// up: a paper about the person (<c>document_kind</c> contract, statement, tax, payslip, certificate, insurance,
    /// medical, official), a picture with people or of an occasion (<c>people</c>, <c>occasion</c>), not a screenshot.
    /// Such a keep is <c>likely</c>, and <c>uncertain</c> where it contradicts the result (disposable) or lies among noise.
    /// A person's override always stands.
    /// </summary>
    public static FileValue ValueOf(FileNode file, FilePlace place, IReadOnlyList<FileFact> facts)
    {
        var value = FindingRules.ValueOf(file, place);
        if (value.Basis == "human" || value.Worth == FileWorth.Keep || facts.Count == 0) return value;
        if (KeepingFact(file, facts) is null) return value;
        var unsure = value.Worth == FileWorth.Disposable || place.NoiseByBundle || place.NoiseByDirectory;
        return new FileValue(FileWorth.Keep, unsure ? ResultCertainty.Uncertain : ResultCertainty.Likely, "facts");
    }

    /// <summary>The fact that keeps a file by itself, or null.</summary>
    public static FileFact? KeepingFact(FileNode file, IReadOnlyList<FileFact> facts)
    {
        if (Of(facts, FileFactKey.DocumentKind) is { } kind && KeptDocumentKinds.Contains(kind.Value)) return kind;
        if (file.FileType is not (FileType.Image or FileType.Video)) return null;
        if (Of(facts, FileFactKey.ImageOrigin)?.Value == ImageOrigin.Screenshot.ToCode()) return null;
        if (Of(facts, FileFactKey.Occasion) is { } occasion && KeptOccasions.Contains(occasion.Value)) return occasion;
        if (Of(facts, FileFactKey.People) is { Value: "true" } people) return people;
        return null;
    }

    public static FileFact? Of(IReadOnlyList<FileFact> facts, FileFactKey key) => facts.FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// The decision of one file: worth, certainty, the rule and the inputs it rests on, and a target from
    /// <paramref name="proposer"/> for a kept file. A marked file rests on its marker alone (no category, no fact,
    /// no target): it is kept, never disposable, and decided one by one.
    /// </summary>
    public static FileDecision Decide(FileValueRow row, ITargetProposer? proposer, DateTime decidedAt)
    {
        var file = row.File;
        var match = SensitiveOutputGuard.GetMatch(file);
        if (match.IsMatch)
        {
            return new FileDecision
            {
                JobId = file.JobId,
                FileNodeId = file.Id,
                Worth = RetentionValue.Keep,
                Certainty = FindingRules.SensitiveCertainty(match.Rule, file.Classification?.Certainty),
                Rule = "sensitive",
                Basis = [new DecisionInput("sensitive", SensitiveMarkerNames.Format(match.Markers) ?? string.Empty, match.Rule ?? "rule")],
                DecidedAt = decidedAt
            };
        }

        var value = row.Value;
        var worth = value.Worth switch
        {
            FileWorth.Keep => RetentionValue.Keep,
            FileWorth.Disposable => RetentionValue.Disposable,
            _ => (RetentionValue?)null
        };
        return new FileDecision
        {
            JobId = file.JobId,
            FileNodeId = file.Id,
            Worth = worth,
            Certainty = value.Certainty,
            Rule = value.Basis,
            Basis = BasisOf(file, row.Place, value, row.Facts),
            Target = worth == RetentionValue.Keep ? proposer?.Propose(file, row.Facts) : null,
            DecidedAt = decidedAt
        };
    }

    /// <summary>The inputs a worth rests on, named by the rule that decided it.</summary>
    public static IReadOnlyList<DecisionInput> BasisOf(FileNode file, FilePlace place, FileValue value, IReadOnlyList<FileFact> facts)
    {
        var basis = new List<DecisionInput>();
        switch (value.Basis)
        {
            case "human" when file.Override is { } human:
                basis.Add(new DecisionInput("category", CategoryCode(human.OverriddenCategory), ResultOrigin.Human.ToCode()));
                break;
            case "facts":
                if (KeepingFact(file, facts) is { } keeping) basis.Add(Of(keeping));
                AddResult(basis, file);
                break;
            case "directory" when place.Directory is { } dir:
                basis.Add(new DecisionInput("directory_retention", dir.Retention ?? string.Empty, dir.VerdictSource ?? "rule"));
                break;
            case "bundle" when (place.Part ?? place.Bundle) is { } bundle:
                basis.Add(new DecisionInput("bundle", bundle.Kind.ToCode() + (bundle.Part is { } part ? ":" + part.ToCode() : string.Empty), "bundle"));
                basis.Add(FileTypeBasis(file));
                break;
            case "file_type":
                basis.Add(FileTypeBasis(file));
                break;
            case "name":
                basis.Add(new DecisionInput("file_name", file.Name, "scan"));
                break;
            case "none":
                break;
            default:
                AddResult(basis, file);
                break;
        }
        return basis;
    }

    private static void AddResult(List<DecisionInput> basis, FileNode file)
    {
        if (file.Classification is not { Error: null } c) return;
        var origin = c.Origin is { } o ? o.ToCode() : ResultOrigin.Model.ToCode();
        basis.Add(new DecisionInput("category", CategoryCode(c.Category), origin));
        if (c.KeepHint is { } hint) basis.Add(new DecisionInput("keep_hint", hint.ToCode(), origin));
        else if (c.Importance != Importance.Unknown) basis.Add(new DecisionInput("importance", c.Importance.ToString().ToLowerInvariant(), origin));
    }

    public static DecisionInput Of(FileFact fact) => new(fact.Key.ToCode(), fact.Value, fact.Source.ToCode());

    public static DecisionInput FileTypeBasis(FileNode file) => new("file_type", FileTypeCode(file.FileType), "scan");

    public static string FileTypeCode(FileType type) => type.ToString().ToLowerInvariant();

    /// <summary>Categories in <c>snake_case</c>, as the CLI writes them.</summary>
    public static string CategoryCode(FileCategory category) => JsonNamingPolicy.SnakeCaseLower.ConvertName(category.ToString());
}
