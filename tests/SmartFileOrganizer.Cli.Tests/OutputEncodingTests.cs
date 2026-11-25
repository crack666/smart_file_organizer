using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Hosting;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The encoding of redirected output (https://github.com/crack666/smart_file_organizer/issues/127): starts the
/// shipped <c>sfo</c> as a process, because the console encoding only exists there, and reads the raw bytes.
/// </summary>
public sealed class OutputEncodingTests : IDisposable
{
    /// <summary>Characters in no OEM code page at once (850 and 437 included), one of them outside the BMP.</summary>
    private const string Name = "Übergröße Łódź 日本語 😀.txt";

    /// <summary>Throws on a byte sequence that is not UTF-8 instead of replacing it.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_encoding_{Guid.NewGuid():N}");
    private readonly string _root;

    public OutputEncodingTests()
    {
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, Name), "Dear landlord");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Output(int ExitCode, byte[] Stdout, byte[] Stderr);

    private async Task<Output> SfoAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // A console of its own, so the code page of the machine applies and not one the test runner has set.
            CreateNoWindow = true
        };
        // .NET takes the console encoding from the code page on Windows and from the locale elsewhere.
        start.Environment["LC_ALL"] = "en_US.ISO-8859-1";
        start.Environment[SmartFileOrganizerHostOptions.OllamaUrlVariable] = NoRealModelServer.DeadAddress;
        foreach (var arg in (string[])[Path.Combine(AppContext.BaseDirectory, "sfo.dll"), .. args, "--db", Path.Combine(_dir, "state", "data.db")])
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        await Task.WhenAll(
            process.StandardOutput.BaseStream.CopyToAsync(stdout),
            process.StandardError.BaseStream.CopyToAsync(stderr),
            process.WaitForExitAsync());
        return new Output(process.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    private static string Utf8WithoutBom(byte[] bytes)
    {
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The output starts with a byte order mark.");
        return StrictUtf8.GetString(bytes);
    }

    [Fact]
    public async Task RedirectedOutput_IsUtf8AndKeepsEveryCharacter()
    {
        var scan = await SfoAsync("scan", _root, "--json", "--progress", "jsonl");
        Assert.True(scan.ExitCode == 0, Encoding.UTF8.GetString(scan.Stderr));
        using (var doc = JsonDocument.Parse(Utf8WithoutBom(scan.Stdout)))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        // stderr is a machine channel with jsonl; every line is a document of its own.
        foreach (var line in Utf8WithoutBom(scan.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            JsonDocument.Parse(line).Dispose();

        var files = await SfoAsync("files", "1", "--json");
        using (var doc = JsonDocument.Parse(Utf8WithoutBom(files.Stdout)))
        {
            var path = Assert.Single(doc.RootElement.GetProperty("result").GetProperty("items").EnumerateArray()).GetProperty("path").GetString();
            Assert.Equal(Path.Combine(_root, Name), path);
        }

        // Text is redirected to files too (sfo rules show > profile.json).
        var text = await SfoAsync("files", "1");
        Assert.Contains(Name, Utf8WithoutBom(text.Stdout), StringComparison.Ordinal);

        // An error on stderr names the path.
        var missing = await SfoAsync("scan", Path.Combine(_dir, "fehlt " + Name));
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains(Name, Utf8WithoutBom(missing.Stderr), StringComparison.Ordinal);
    }
}
