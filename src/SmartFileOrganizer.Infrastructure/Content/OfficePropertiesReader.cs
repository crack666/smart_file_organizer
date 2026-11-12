using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace SmartFileOrganizer.Infrastructure.Content;

public sealed record OfficeProperties(string? Title, string? Author, DateTime? Created, DateTime? Modified, int? Pages);

/// <summary>
/// Reads the document properties of Office Open XML (<c>docProps/core.xml</c>, <c>docProps/app.xml</c>)
/// and OpenDocument files (<c>meta.xml</c>). Both are ZIP packages; only these small entries are read.
/// </summary>
public static class OfficePropertiesReader
{
    public static bool Supports(string extension) => extension.ToLowerInvariant() is
        ".docx" or ".xlsx" or ".pptx" or ".docm" or ".xlsm" or ".pptm" or ".odt" or ".ods" or ".odp";

    public static OfficeProperties? Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var core = archive.GetEntry("docProps/core.xml");
        if (core is not null)
        {
            var doc = Load(core);
            var app = archive.GetEntry("docProps/app.xml") is { } appEntry ? Load(appEntry) : null;
            return new OfficeProperties(
                Value(doc, "title"),
                Value(doc, "creator") ?? Value(doc, "lastModifiedBy"),
                Date(Value(doc, "created")),
                Date(Value(doc, "modified")),
                int.TryParse(app is null ? null : Value(app, "Pages") ?? Value(app, "Slides"), out var pages) && pages > 0 ? pages : null);
        }

        var meta = archive.GetEntry("meta.xml");
        if (meta is not null)
        {
            var doc = Load(meta);
            var stats = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "document-statistic");
            var pageCount = stats?.Attributes().FirstOrDefault(a => a.Name.LocalName == "page-count")?.Value;
            return new OfficeProperties(
                Value(doc, "title"),
                Value(doc, "initial-creator") ?? Value(doc, "creator"),
                Date(Value(doc, "creation-date")),
                Date(Value(doc, "date")),
                int.TryParse(pageCount, out var pages) && pages > 0 ? pages : null);
        }

        return null;
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        // Property parts are a few KB; refuse anything large instead of inflating it.
        if (entry.Length > 1024 * 1024) return new XDocument(new XElement("empty"));
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string? Value(XDocument doc, string localName)
    {
        var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTime? Date(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
        && d.Year > 1970 ? d : null;
}
