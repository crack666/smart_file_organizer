using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Application.Evaluation;

namespace SmartFileOrganizer.Infrastructure.Evaluation;

/// <summary>
/// The findings the small corpus should give (<c>findings.json</c>), measured without a library: every one a person
/// would want to see, and none else. Kept apart from <see cref="SmallCorpusGenerator"/> so the truth of the tree and
/// the truth of the findings change on their own; a change of the tree that changes the findings changes this list.
/// </summary>
public static class SmallCorpusFindings
{
    public static IReadOnlyList<ExpectedFinding> Expected { get; } =
    [
        // Worth keeping among the noise of the mixed folders, one finding per kind of file.
        new("in_noise", "Desktop", Group: "documents", Note: "A shopping list and a letter to a gym on a mixed desktop."),
        new("in_noise", "Downloads", Group: "documents", Note: "An invoice and a router manual in a mixed download folder."),
        new("in_noise", "Downloads", Group: "images", Note: "A camera photo in the download folder."),
        new("in_noise", "Import", Group: "documents", Note: "Two invoices waiting for a place under a generic name (version 5)."),
        new("in_noise", "Import", Group: "images", Note: "Photos of three occasions waiting for a place under a generic name (version 5)."),
        new("in_noise", "Import", Group: "videos", Note: "Own clips of an evening waiting for a place under a generic name (version 6)."),
        new("in_noise", "Import/Neuer Ordner", Group: "images", Note: "Photos of two different things under a placeholder name (version 7)."),
        new("in_noise", "alt", Group: "documents", Note: "A scanned letter and a table in an old mixed folder."),
        new("in_noise", "alt", Group: "images", Note: "An application photo in the old mixed folder."),

        // Sensitive places, one per folder and marker set; skipped folders by name or place.
        new("sensitive", ".ssh", Markers: ["keys"]),
        new("sensitive", "Desktop", Markers: ["credentials"]),
        new("sensitive", "Desktop", Markers: ["keys"]),
        new("sensitive", "Desktop", Markers: ["private_message"]),
        new("sensitive", "Dokumente/Bank", Markers: ["finance"]),
        new("sensitive", "Dokumente/Briefe", Markers: ["private_message"]),
        new("sensitive", "Dokumente/Gesundheit", Markers: ["health"]),
        new("sensitive", "Dokumente/Persoenliches", Markers: ["identity"]),
        new("sensitive", "Dokumente/Steuer 2022", Markers: ["finance"]),
        new("sensitive", "Downloads", Markers: ["finance"]),
        new("sensitive", "Profile alter PC/lisa", Markers: ["credentials"], Note: "The password store of the old profile, in its app data."),
        new("sensitive", "Profile alter PC/max/AppData", Markers: ["credentials", "private_message"], Note: "Skipped by the scan rules, marked by its place."),
        new("sensitive", "Profile alter PC/max/Desktop", Markers: ["credentials"]),
        new("sensitive", "Profile alter PC/max/Documents", Markers: ["finance"],
            Note: "Version 4: the zip of bank and card statements under a number, marked by its entry names (issue 67)."),
        new("sensitive", "Profile alter PC/max/Documents/Steuer 2014", Markers: ["finance"]),
        new("sensitive", "Projekte/haushaltsbuch-app", Markers: ["credentials"], Note: "The .env file of a project.")
    ];

    public static string Write(string corpusDirectory)
    {
        var path = Path.Combine(corpusDirectory, EvalFindingTruth.FileName);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new EvalFindingTruthDocument(EvalFindingTruth.SchemaVersion, Expected), EvalJson.Options) + "\n",
            new UTF8Encoding(false));
        return path;
    }
}
