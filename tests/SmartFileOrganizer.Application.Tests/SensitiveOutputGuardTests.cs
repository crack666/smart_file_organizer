using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The output guard is the boundary between the local model and everything outside the
/// machine. These tests pin what a marked file may and may not show. All data is invented.
/// </summary>
public class SensitiveOutputGuardTests
{
    // Distinct markers so a leak is found by a plain substring search.
    private const string ModelSummary = "MODEL-SUMMARY-Erika-Musterfrau-Kontonummer-0000";
    private const string Excerpt = "TEXT-EXCERPT-passwort=FAKE-NOT-A-SECRET";
    private const string RawResponse = "{\"message\":{\"content\":\"RAW-RESPONSE-FAKE-0000\"}}";
    private const string Target = "TARGET-Erika-Musterfrau-Privat";
    private const string ErrorText = "ERROR-ECHO-OF-PROMPT";

    private static FileNode MakeFile(string name, string dir = "/Scan/Docs") => new()
    {
        Id = 7,
        JobId = 1,
        FullPath = dir + "/" + name,
        Name = name,
        ParentPath = dir,
        RelativeDir = "Docs",
        Extension = Path.GetExtension(name),
        FileType = FileType.Document,
        Status = FileNodeStatus.AiAnalyzed,
        Size = 512,
        LastWriteTime = new DateTime(2015, 3, 1, 8, 0, 0, DateTimeKind.Utc),
        Classification = new FileClassification
        {
            FileNodeId = 7,
            Category = FileCategory.TrashCandidate,
            Importance = Importance.Low,
            Confidence = 0.7,
            Summary = ModelSummary,
            SuggestedTarget = Target,
            ModelUsed = "fake-model",
            AnalyzedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            RawResponse = RawResponse,
            Error = ErrorText,
            IsAiResult = true
        }
    };

    private static void AssertNoContent(string output)
    {
        Assert.DoesNotContain(Excerpt, output);
        Assert.DoesNotContain("TEXT-EXCERPT", output);
        Assert.DoesNotContain("RAW-RESPONSE", output);
        Assert.DoesNotContain("MODEL-SUMMARY", output);
        Assert.DoesNotContain(Target, output);
        Assert.DoesNotContain(ErrorText, output);
    }

    // ------------------------------------------------------------------ JSON (done-when test)

    [Fact]
    public void Json_OfMarkedFile_ContainsNeitherExcerptNorRawResponse()
    {
        var file = MakeFile("notes.txt");
        file.SensitiveMarkers = SensitiveMarker.Keys;
        file.SensitiveRule = "header:private-key";

        var json = SensitiveOutputGuard.ToJson(SensitiveOutputGuard.Project(file, textExcerpt: Excerpt));

        AssertNoContent(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("/Scan/Docs/notes.txt", root.GetProperty("full_path").GetString());
        Assert.Equal("Document", root.GetProperty("file_type").GetString());
        Assert.Equal("keys", root.GetProperty("sensitive")[0].GetString());
        Assert.True(root.GetProperty("protected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("text_excerpt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("raw_response").ValueKind);
        Assert.Contains("private key material", root.GetProperty("summary").GetString());
    }

    [Fact]
    public void Json_WithIncludeSensitive_ShowsEverything()
    {
        var file = MakeFile("notes.txt");
        file.SensitiveMarkers = SensitiveMarker.Keys;

        var record = SensitiveOutputGuard.Project(
            file, new SensitiveOutputOptions { IncludeSensitive = true }, Excerpt);
        var json = SensitiveOutputGuard.ToJson(record);

        Assert.False(record.Protected);
        Assert.Contains("TEXT-EXCERPT", json);
        Assert.Contains("RAW-RESPONSE", json);
        Assert.Contains(ModelSummary, json);
        // Still marked, and still never proposed as disposable.
        Assert.Equal(["keys"], record.Sensitive);
        Assert.Equal(nameof(FileCategory.ReviewNeeded), record.Category);
        Assert.Equal("sensitive", record.CategorySource);
    }

    [Fact]
    public void Json_OfUnmarkedFile_IsUnchanged()
    {
        var file = MakeFile("Urlaubsplanung.txt");

        var record = SensitiveOutputGuard.Project(file, textExcerpt: Excerpt);

        Assert.False(record.Protected);
        Assert.Empty(record.Sensitive);
        Assert.Equal(Excerpt, record.TextExcerpt);
        Assert.Equal(RawResponse, record.RawResponse);
        Assert.Equal(ModelSummary, record.Summary);
        Assert.Equal(nameof(FileCategory.TrashCandidate), record.Category);
        Assert.Equal("ai", record.CategorySource);
    }

    // ------------------------------------------------------------------ marker sources

    [Fact]
    public void ModelMarker_Alone_Protects()
    {
        var file = MakeFile("scan_0042.pdf");
        file.Classification!.SensitiveMarkers = SensitiveMarker.Identity;

        var record = SensitiveOutputGuard.Project(file, textExcerpt: Excerpt);

        Assert.True(record.Protected);
        Assert.Equal(["identity"], record.Sensitive);
        Assert.Equal(SensitiveOutputGuard.ModelRule, record.SensitiveRule);
        AssertNoContent(SensitiveOutputGuard.ToJson(record));
    }

    [Fact]
    public void NameRule_ProtectsRowsScannedBeforeTheRulesExisted()
    {
        // Stored markers are empty (old database), but the name is unmistakable.
        var file = MakeFile("id_rsa", "/Scan/home/.ssh");

        var record = SensitiveOutputGuard.Project(file, textExcerpt: Excerpt);

        Assert.True(record.Protected);
        Assert.Contains("keys", record.Sensitive);
        AssertNoContent(SensitiveOutputGuard.ToJson(record));
    }

    [Fact]
    public void ProtectedRecord_IsReviewNeeded_UnlessTheUserDecided()
    {
        var file = MakeFile("Kontoauszug_2014.pdf");
        Assert.Equal(nameof(FileCategory.ReviewNeeded), SensitiveOutputGuard.Project(file).Category);

        file.Override = new UserOverride { FileNodeId = 7, OverriddenCategory = FileCategory.Invoice, OverriddenTarget = Target };
        var record = SensitiveOutputGuard.Project(file);
        Assert.Equal(nameof(FileCategory.Invoice), record.Category);
        Assert.Equal("user", record.CategorySource);
        Assert.Null(record.SuggestedTarget);
    }

    [Fact]
    public void ArrayJson_ProtectsEachRecord()
    {
        var marked = MakeFile("Passwoerter.txt");
        var plain = MakeFile("Einkaufsliste.txt");

        var json = SensitiveOutputGuard.ToJson(
            new[] { marked, plain }.Select(f => SensitiveOutputGuard.Project(f)));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement[0].GetProperty("protected").GetBoolean());
        Assert.False(doc.RootElement[1].GetProperty("protected").GetBoolean());
    }

    // ------------------------------------------------------------------ review rule

    [Fact]
    public void ReviewRule_ReplacesTrashCandidateOnlyForMarkedFiles()
    {
        Assert.Equal(FileCategory.ReviewNeeded,
            SensitiveOutputGuard.ApplyReviewRule(FileCategory.TrashCandidate, SensitiveMarker.Finance));
        Assert.Equal(FileCategory.TrashCandidate,
            SensitiveOutputGuard.ApplyReviewRule(FileCategory.TrashCandidate, SensitiveMarker.None));
        Assert.Equal(FileCategory.Photo,
            SensitiveOutputGuard.ApplyReviewRule(FileCategory.Photo, SensitiveMarker.Finance));
    }

    [Fact]
    public void ClassificationService_NeverStoresTrashCandidateForMarkedFile()
    {
        var byModel = new FileClassification { Category = FileCategory.TrashCandidate, SensitiveMarkers = SensitiveMarker.Credentials };
        ClassificationService.ApplySensitiveReviewRule(MakeFile("tmp.txt"), byModel);
        Assert.Equal(FileCategory.ReviewNeeded, byModel.Category);

        var byRule = new FileClassification { Category = FileCategory.TrashCandidate };
        var keyFile = MakeFile("tmp.txt");
        keyFile.SensitiveMarkers = SensitiveMarker.Keys;
        ClassificationService.ApplySensitiveReviewRule(keyFile, byRule);
        Assert.Equal(FileCategory.ReviewNeeded, byRule.Category);

        var plain = new FileClassification { Category = FileCategory.TrashCandidate };
        ClassificationService.ApplySensitiveReviewRule(MakeFile("tmp.txt"), plain);
        Assert.Equal(FileCategory.TrashCandidate, plain.Category);
    }

    // ------------------------------------------------------------------ CSV and directory summary

    [Fact]
    public void CsvRow_OfMarkedFile_HasNoContentFields()
    {
        var file = MakeFile("Befund_2018.pdf");

        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, file);
        var row = sw.ToString();

        AssertNoContent(row);
        Assert.Contains("health", row);
        Assert.Contains(nameof(FileCategory.ReviewNeeded), row);
        Assert.DoesNotContain(nameof(FileCategory.TrashCandidate), row);
    }

    [Fact]
    public void CsvRow_WithIncludeSensitive_HasModelSummary()
    {
        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, MakeFile("Befund_2018.pdf"), new SensitiveOutputOptions { IncludeSensitive = true });
        Assert.Contains(ModelSummary, sw.ToString());
    }

    [Fact]
    public void DirectorySummaryLine_OfMarkedFile_UsesNeutralSummary()
    {
        var line = DirectorySummaryService.BuildFileSummaryLine(MakeFile("Reisepass.pdf"));
        Assert.DoesNotContain("MODEL-SUMMARY", line);
        Assert.Contains("identity document", line);

        var plain = DirectorySummaryService.BuildFileSummaryLine(MakeFile("Rezept.pdf"));
        Assert.Contains("MODEL-SUMMARY", plain);
    }

    [Fact]
    public void NeutralSummary_NamesEveryMarker()
    {
        var text = SensitiveOutputGuard.NeutralSummary(
            SensitiveMarker.Credentials | SensitiveMarker.Keys | SensitiveMarker.PrivateMessage |
            SensitiveMarker.Identity | SensitiveMarker.Finance | SensitiveMarker.Health, FileType.Other);

        foreach (var kind in new[] { "credentials", "private key", "private messages", "identity", "financial", "health" })
            Assert.Contains(kind, text);
    }
}
