using System.Text;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Collects the evidence for one file while the readers run: facts, text sections, images and
/// the two lists that tell the model what the evidence holds and what is missing.
/// </summary>
public sealed class Evidence
{
    private readonly List<FileFact> _facts = [];
    private readonly List<string> _contains = [];
    private readonly List<string> _missing = [];
    private readonly List<byte[]> _images = [];
    private readonly StringBuilder _text = new();

    public IReadOnlyList<FileFact> Facts => _facts;
    public IReadOnlyList<string> Contains => _contains;
    public IReadOnlyList<string> Missing => _missing;
    public IReadOnlyList<byte[]> Images => _images;
    public string Text => _text.ToString().TrimEnd();

    /// <summary>Set when a rule decides the file without the model.</summary>
    public FileClassification? RuleResult { get; set; }

    public void Fact(FileFactKey key, string? value, FactSource source = FactSource.Metadata)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _facts.RemoveAll(f => f.Key == key);
        _facts.Add(new FileFact(key, value.Trim(), source));
    }

    public void Fact(FileFactKey key, long value, FactSource source = FactSource.Metadata) =>
        Fact(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), source);

    public void Fact(FileFactKey key, bool value, FactSource source = FactSource.Metadata) =>
        Fact(key, value ? "true" : "false", source);

    public void Fact<TEnum>(FileFactKey key, TEnum value, FactSource source = FactSource.Rule)
        where TEnum : struct, Enum => Fact(key, value.ToCode(), source);

    public bool HasFact(FileFactKey key) => _facts.Any(f => f.Key == key);

    public string? FactValue(FileFactKey key) => _facts.FirstOrDefault(f => f.Key == key)?.Value;

    public void Has(string what) => _contains.Add(what);

    public void Lacks(string what) => _missing.Add(what);

    public void Image(byte[] bytes, string what)
    {
        _images.Add(bytes);
        _contains.Add(what);
    }

    /// <summary>Adds a titled text section. Returns false when the text is empty.</summary>
    public bool Section(string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (_text.Length > 0) _text.AppendLine();
        _text.AppendLine($"{title}:");
        _text.AppendLine(text.TrimEnd());
        return true;
    }

    public OllamaClassificationInput ToInput(FileNode file) => new()
    {
        File = file,
        ExtractedText = Text,
        Base64Images = _images.Select(Convert.ToBase64String).ToList(),
        Facts = _facts.ToList(),
        Contains = _contains.ToList(),
        Missing = _missing.ToList(),
        RuleResult = RuleResult
    };
}
