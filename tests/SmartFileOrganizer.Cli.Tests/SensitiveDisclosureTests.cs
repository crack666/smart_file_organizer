using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>Sensitive:Disclosure</c> (https://github.com/crack666/smart_file_organizer/issues/99, 14.11.2026): a marker is a hint
/// on something important, not a reason to hide names, structure or descriptions. Default <c>open</c>; <c>restricted</c>
/// keeps the behaviour before. The literal text of a file with an access data marker (<c>credentials</c>, <c>keys</c>) is
/// never shown, whatever the setting. Same generated library and source as <see cref="FindingTargetsCommandTests"/>; no model.
/// </summary>
public partial class FindingTargetsCommandTests
{
    /// <summary>Made up; stands for a token of a code hosting service in a <c>.env</c> file.</summary>
    private static readonly string Secret = "ghp_FAKE" + Guid.NewGuid().ToString("N")[..16];

    /// <summary><c>Sensitive:Disclosure restricted</c>: the behaviour before 14.11.2026.</summary>
    private void Restricted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), """{ "Sensitive": { "Disclosure": "restricted" } }""");
    }

    private async Task MarkFolderAsync(long job, string path, string markers)
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteAsync(
            "UPDATE directory_nodes SET sensitive_markers = @markers WHERE job_id = @job AND full_path = @path", new { job, path, markers }));
    }

    // ------------------------------------------------------------------ done when (1)

    /// <summary>
    /// Done when: with <c>open</c> a library with a marked file in its root gives its top level in <c>sfo findings
    /// candidates</c> and <c>sfo library folder</c>, and the file is there with name and marker. Before 14.11.2026 (now
    /// <c>restricted</c>) the top level stayed empty (<c>root_sensitive: true</c>) and the controller chose blind.
    /// </summary>
    [Fact]
    public async Task Open_ALibraryWithAMarkedFileInItsRoot_ShowsItsTopLevel_AndTheFileWithNameAndMarker()
    {
        Write(_library, "passwords.txt", "user: nobody", new DateTime(2020, 1, 1));
        var (library, source) = await PrepareAsync();
        var job = source.ToString();

        var candidates = await OkAsync("findings", "candidates", job);
        var root = candidates.GetProperty("libraries").EnumerateArray().Single();
        Assert.True(root.GetProperty("root_sensitive").GetBoolean());
        Assert.False(root.GetProperty("top_level_withheld").GetBoolean());
        Assert.Equal(["Dokumente", "Fotos"], root.GetProperty("top_level").EnumerateArray().Select(t => Str(t, "name")).Order().ToList());
        var marked = Assert.Single(root.GetProperty("root_marked_files").EnumerateArray());
        Assert.Equal("passwords.txt", Str(marked, "relative_path"));
        Assert.Equal(["credentials"], marked.GetProperty("markers").EnumerateArray().Select(m => m.GetString()).ToList());

        var look = await OkAsync("library", "folder", library.ToString(), _library);
        var folder = look.GetProperty("folder");
        Assert.True(folder.GetProperty("sensitive").GetBoolean());
        Assert.False(folder.GetProperty("withheld").GetBoolean());
        Assert.Equal(["credentials"], folder.GetProperty("markers").EnumerateArray().Select(m => m.GetString()).ToList());
        Assert.Equal(["Dokumente", "Fotos"], look.GetProperty("subfolders").EnumerateArray().Select(s => s.GetString()).Order().ToList());
        var file = Assert.Single(look.GetProperty("files").EnumerateArray());
        Assert.Equal("passwords.txt", Str(file, "name"));
        Assert.Equal(["credentials"], file.GetProperty("markers").EnumerateArray().Select(m => m.GetString()).ToList());
        Assert.DoesNotContain("nobody", candidates.ToString() + look);

        // Restricted: the behaviour before 14.11.2026.
        Restricted();
        var before = (await OkAsync("findings", "candidates", job)).GetProperty("libraries").EnumerateArray().Single();
        Assert.True(before.GetProperty("root_sensitive").GetBoolean());
        Assert.True(before.GetProperty("top_level_withheld").GetBoolean());
        Assert.Empty(before.GetProperty("top_level").EnumerateArray());
        Assert.Empty(before.GetProperty("root_marked_files").EnumerateArray());
        var hidden = await OkAsync("library", "folder", library.ToString(), _library);
        Assert.True(hidden.GetProperty("folder").GetProperty("withheld").GetBoolean());
        Assert.Empty(hidden.GetProperty("subfolders").EnumerateArray());
        Assert.Equal(["passwords.txt"], hidden.GetProperty("files").EnumerateArray().Select(f => Str(f, "name")).ToList());
    }

    // ------------------------------------------------------------------ done when (2)

    /// <summary>
    /// Done when: with <c>open</c> the description facts of marked files appear in the candidates. For a folder marked
    /// <c>finance</c> all of them; for a folder marked <c>credentials</c> the short classification only (codes such as the
    /// occasion and season), never a free text such as the scene, which could quote what the file shows.
    /// </summary>
    [Fact]
    public async Task Open_MarkedFolders_ShowTheirFacts_ButNeverTheFreeTextsOfAccessData()
    {
        var (_, source) = await PrepareAsync();
        var job = source.ToString();
        var folder = Path.Combine(_source, "Neuer Ordner");

        await MarkFolderAsync(source, folder, "finance");
        var finance = (await OkAsync("findings", "candidates", job)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("relative_path").GetString() == "Neuer Ordner");
        Assert.Equal(4, finance.GetProperty("sensitive_files").GetArrayLength());
        Assert.Equal(4, finance.GetProperty("examples").GetArrayLength());
        var described = finance.GetProperty("groups")[0].GetProperty("described");
        Assert.Equal(4, described.GetProperty("described").GetInt32());
        Assert.Contains("skiing", Str(described, "text"));
        Assert.Contains("snowy slope", Str(described, "text"));

        await MarkFolderAsync(source, folder, "credentials");
        var access = (await OkAsync("findings", "candidates", job)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("relative_path").GetString() == "Neuer Ordner");
        Assert.All(access.GetProperty("sensitive_files").EnumerateArray(),
            f => Assert.Equal(["credentials"], f.GetProperty("markers").EnumerateArray().Select(m => m.GetString()).ToList()));
        Assert.Equal(4, access.GetProperty("examples").GetArrayLength());
        var shortOnly = access.GetProperty("groups")[0].GetProperty("described");
        Assert.Equal(4, shortOnly.GetProperty("described").GetInt32());
        Assert.Contains("holiday", Str(shortOnly, "text"));
        Assert.Contains("winter", Str(shortOnly, "text"));
        Assert.DoesNotContain("skiing", access.ToString());
        Assert.DoesNotContain("snowy slope", access.ToString());
    }

    // ------------------------------------------------------------------ done when (2), access data in every output

    /// <summary>
    /// Done when: the raw text of a file with an access data marker appears in no output, in either setting. The made-up
    /// token lies in the file, in the stored model summary, raw answer, evidence, error and keep reason, and in free-text
    /// facts; marker and name are shown, and so is the short classification.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessData_TheSecretAppearsInNoOutput_MarkerAndNameDo(bool restricted)
    {
        if (restricted) Restricted();
        Write(_source, "Zugang/.env", $"GITHUB_TOKEN={Secret}", new DateTime(2024, 3, 1));
        Write(_library, "Zugang/.env", $"GITHUB_TOKEN={Secret}", new DateTime(2024, 3, 1));
        var (library, source) = await PrepareAsync();
        var job = source.ToString();

        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        long envId;
        await using (var conn = db.CreateConnection())
        {
            await conn.OpenAsync();
            envId = await conn.QuerySingleAsync<long>(
                "SELECT id FROM file_nodes WHERE job_id = @job AND name = '.env'", new { job = source });
        }
        await new ClassificationRepository(db).UpsertAsync(new FileClassification
        {
            FileNodeId = envId,
            Category = FileCategory.PersonalDocument,
            Importance = Importance.High,
            Confidence = 0.9,
            Summary = $"Access token {Secret} for a code hosting service.",
            ModelUsed = "scripted",
            AnalyzedAt = DateTime.UtcNow,
            RawResponse = $"{{\"summary\":\"{Secret}\"}}",
            Evidence = $"text: GITHUB_TOKEN={Secret}",
            Error = $"echo {Secret}",
            PreviousError = $"echo {Secret}",
            KeepReason = $"holds {Secret}",
            IsAiResult = true
        });
        await new FileFactRepository(db).ReplaceAsync(envId,
        [
            F(FileFactKey.Issuer, "Example Code Hosting"), F(FileFactKey.Year, "2024"),
            F(FileFactKey.Scene, $"token {Secret}"), new FileFact(FileFactKey.Title, Secret)
        ]);

        var findings = await OkAsync("findings", job, "--limit", "100");
        var sensitiveKey = findings.GetProperty("items").EnumerateArray()
            .Single(f => Str(f, "kind") == "sensitive" && f.GetProperty("relative_path").GetString() == "Zugang").GetProperty("key").GetString()!;
        var csv = Path.Combine(_dir, "export.csv");

        var outputs = new List<string> { findings.ToString() };
        string[][] commands =
        [
            ["files", job], ["files", job, "--include-sensitive"], ["files", job, "--sensitive", "yes", "--verbose"],
            ["show", job, Path.Combine("Zugang", ".env")], ["show", job, Path.Combine("Zugang", ".env"), "--include-sensitive"], ["show", job, "Zugang"],
            ["dirs", job], ["dirs", job, "--include-sensitive"], ["summary", job],
            ["findings", "show", job, sensitiveKey], ["findings", "show", job, sensitiveKey, "--include-sensitive"],
            ["findings", "candidates", job], ["library", "folder", library.ToString(), "Zugang"],
            ["duplicates", job, library.ToString()],
            ["export", job, "--csv", csv], ["export", job, "--csv", csv + ".all", "--include-sensitive"],
            ["compare", job, "--against", library.ToString(), "--files", "all"]
        ];
        // The full checksum of a short secret would confirm a guess of it: it is withheld like the text.
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"GITHUB_TOKEN={Secret}")));
        foreach (var command in commands)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = await SfoApp.RunAsync([.. command, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
            Assert.True(exit == ExitCodes.Ok, string.Join(" ", command) + ": " + stdout + stderr);
            outputs.Add(stdout + "\n" + stderr);
        }
        outputs.Add(await File.ReadAllTextAsync(csv));
        outputs.Add(await File.ReadAllTextAsync(csv + ".all"));
        var debugLog = Path.Combine(Path.GetDirectoryName(_dbPath)!, CliLogFiles.DebugLogName);
        if (File.Exists(debugLog)) outputs.Add(await File.ReadAllTextAsync(debugLog));

        foreach (var output in outputs)
        {
            Assert.DoesNotContain(Secret, output);
            Assert.DoesNotContain(checksum, output, StringComparison.OrdinalIgnoreCase);
        }

        // Marker and name are shown; so is the classification, unless restricted.
        var files = outputs[1];
        Assert.Contains(".env", files);
        Assert.Contains("\"credentials\"", files);
        var shown = (await OkAsync("show", job, Path.Combine("Zugang", ".env"))).GetProperty("file");
        Assert.Equal(".env", Str(shown, "name"));
        Assert.True(shown.GetProperty("protected").GetBoolean());
        Assert.Equal(restricted ? "review_needed" : "personal_document", Str(shown, "category"));
        Assert.Contains(".env", outputs[10]);
        var duplicates = outputs[14];
        Assert.Contains(".env", duplicates);
    }
}
