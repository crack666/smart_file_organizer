using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>Where the advice of a review came from.</summary>
public static class AdviceStatus
{
    /// <summary>Asked now.</summary>
    public const string Ok = "ok";

    /// <summary>Stored answer to the same facts; <c>--refresh</c> asks again.</summary>
    public const string Cached = "cached";

    /// <summary><c>--no-model</c>: the rules alone.</summary>
    public const string Skipped = "skipped";

    /// <summary>Ollama did not answer at all.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The model answered with an error or with nothing usable.</summary>
    public const string Failed = "failed";
}

/// <summary>A folder the model calls good as it is, with the reason; it stays.</summary>
public sealed record AdviceKeep(string Path, string Reason);

/// <summary>The role the model sees in a folder, and in its children (what makes siblings comparable).</summary>
public sealed record AdviceRole(string Path, FolderRole Role, FolderRole? ChildrenRole, string? Reason);

/// <summary>A proposed change. Data only: nothing is carried out; accepted changes become plan actions later.</summary>
public sealed record AdviceChange(ProposalAction Action, string Path, string? Target, string Reason, LibraryFindingKind? Finding);

/// <summary>One level of the target outline: where something belongs, and how its folders are named.</summary>
public sealed record AdviceOutline(string Path, string Purpose, string? Pattern);

/// <summary>What the model said about a library, checked against the tree.</summary>
public sealed record LibraryAdvice(
    string Status,
    string? Model,
    DateTime? CreatedAt,
    IReadOnlyList<AdviceKeep> Good,
    IReadOnlyList<AdviceRole> Roles,
    IReadOnlyList<AdviceChange> Changes,
    IReadOnlyList<AdviceOutline> Outline,
    int Dropped,
    string? Error)
{
    public static LibraryAdvice Without(string status, string? error = null, string? model = null) =>
        new(status, model, null, [], [], [], [], 0, error);
}

/// <summary>
/// Reads the model's answer to a review defensively: unknown codes, paths that are not in the tree and
/// entries without a reason are dropped and counted, never guessed.
/// </summary>
public static class LibraryAdviceParser
{
    public static LibraryAdvice Parse(string? content, ISet<string> knownPaths, string status, string? model, DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(content))
            return LibraryAdvice.Without(AdviceStatus.Failed, "The model returned no content.", model);

        JsonElement root;
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
                return LibraryAdvice.Without(AdviceStatus.Failed, "The model's answer holds no JSON object.", model);
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return LibraryAdvice.Without(AdviceStatus.Failed, $"The model's answer is not valid JSON: {ex.Message}", model);
        }

        var dropped = 0;
        string? Known(string? path)
        {
            var p = NormalizePath(path);
            return p is not null && knownPaths.Contains(p) ? p : null;
        }

        var good = new List<AdviceKeep>();
        foreach (var e in Array(root, "good"))
        {
            if (Known(Str(e, "path")) is { } p && Str(e, "reason") is { } r)
                good.Add(new AdviceKeep(p, r));
            else dropped++;
        }

        var roles = new List<AdviceRole>();
        foreach (var e in Array(root, "roles"))
        {
            if (Known(Str(e, "path")) is { } p && VerdictCodes.TryParse<FolderRole>(Str(e, "role"), out var role))
            {
                FolderRole? children = VerdictCodes.TryParse<FolderRole>(Str(e, "children_role"), out var c) ? c : null;
                roles.Add(new AdviceRole(p, role, children, Str(e, "reason")));
            }
            else dropped++;
        }

        var changes = new List<AdviceChange>();
        foreach (var e in Array(root, "changes"))
        {
            var reason = Str(e, "reason");
            if (VerdictCodes.TryParse<ProposalAction>(Str(e, "action"), out var action)
                && Known(Str(e, "path")) is { } p && reason is not null
                && (action is not (ProposalAction.Rename or ProposalAction.Move or ProposalAction.Merge or ProposalAction.Create)
                    || Str(e, "target") is not null))
            {
                LibraryFindingKind? finding = VerdictCodes.TryParse<LibraryFindingKind>(Str(e, "finding"), out var k) ? k : null;
                changes.Add(new AdviceChange(action, p, Str(e, "target"), reason, finding));
            }
            else dropped++;
        }

        var outline = new List<AdviceOutline>();
        foreach (var e in Array(root, "outline"))
        {
            if (NormalizePath(Str(e, "path")) is { } p && Str(e, "purpose") is { } purpose)
                outline.Add(new AdviceOutline(p, purpose, Str(e, "pattern")));
            else dropped++;
        }

        if (good.Count + roles.Count + changes.Count + outline.Count == 0)
            return new LibraryAdvice(AdviceStatus.Failed, model, createdAt, [], [], [], [], dropped, "The model's answer holds nothing usable.");
        return new LibraryAdvice(status, model, createdAt, good, roles, changes, outline, dropped, null);
    }

    /// <summary><c>/</c> as separator, no leading or trailing separator, <c>.</c> for the root.</summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/').Trim('/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Length == 0 ? "." : p;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;
}
