using System.Globalization;
using System.Text;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>Result of the rules: the markers found and the ids of the rules that fired.</summary>
public readonly record struct SensitiveMatch(SensitiveMarker Markers, string? Rule)
{
    public static SensitiveMatch None => default;

    public bool IsMatch => Markers != SensitiveMarker.None;

    /// <summary>Union of two matches; rule ids are joined with <c>;</c>.</summary>
    public SensitiveMatch Combine(SensitiveMatch other)
    {
        if (!other.IsMatch) return this;
        if (!IsMatch) return other;
        return new SensitiveMatch(Markers | other.Markers, Rule + ";" + other.Rule);
    }
}

/// <summary>
/// Detects sensitive files without a model: file name, extension, folder (browser and mail
/// profiles, <c>.ssh</c>) and the first bytes of small files (private-key header lines,
/// KeePass signature).
/// </summary>
/// <remarks>
/// <para>Runs during the scan (see <c>ScanEngine</c>), so it also covers files the model never
/// sees: type <c>Other</c>, or skipped by the directory sampling. The name rules are pure and
/// cheap, and <see cref="SensitiveOutputGuard"/> re-evaluates them on every output, which also
/// protects databases scanned before these rules existed.</para>
/// <para>Rule ids (<c>name:id_rsa</c>, <c>header:private-key</c>) describe the rule, never the
/// content, so they may leave the machine.</para>
/// <para>False positives are cheap (the file is shown as "review" and its content is withheld);
/// false negatives are not. When in doubt a rule marks.</para>
/// </remarks>
public static class SensitiveFileRules
{
    /// <summary>
    /// Version of the rules that read the file (<see cref="MatchHeader"/>), stored per file in
    /// <c>file_nodes.sensitive_rules_version</c>. Raise it when a header rule is added: the next analysis
    /// then runs the rules again for older rows before their content reaches a model (<c>SensitiveRulesBackfill</c>).
    /// </summary>
    public const int Version = 1;

    /// <summary>Bytes read from the start of a file for the header rules.</summary>
    public const int HeaderProbeBytes = 8 * 1024;

    /// <summary>Larger files are not probed: key files are a few KB, and the scan must stay fast.</summary>
    public const long MaxHeaderProbeFileSize = 256 * 1024;

    private const SensitiveMarker Z = SensitiveMarker.Credentials;
    private const SensitiveMarker S = SensitiveMarker.Keys;
    private const SensitiveMarker N = SensitiveMarker.PrivateMessage;
    private const SensitiveMarker A = SensitiveMarker.Identity;
    private const SensitiveMarker F = SensitiveMarker.Finance;
    private const SensitiveMarker G = SensitiveMarker.Health;

    /// <summary>Exact file names (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, SensitiveMarker> ExactNames { get; } =
        new Dictionary<string, SensitiveMarker>(StringComparer.OrdinalIgnoreCase)
        {
            // SSH private keys (the matching *.pub files are public and not listed)
            ["id_rsa"] = S, ["id_dsa"] = S, ["id_ecdsa"] = S, ["id_ed25519"] = S,
            ["id_ecdsa_sk"] = S, ["id_ed25519_sk"] = S,
            // GnuPG secret keyrings
            ["secring.gpg"] = S, ["secring.skr"] = S,
            // Crypto wallets
            ["wallet.dat"] = S | F,
            // Credential files
            [".env"] = Z, [".netrc"] = Z, ["_netrc"] = Z, [".pgpass"] = Z,
            [".git-credentials"] = Z, [".htpasswd"] = Z, ["credentials"] = Z,
            // Browser profile stores (Chromium family and Firefox)
            ["Login Data"] = Z, ["Login Data For Account"] = Z, ["Web Data"] = Z, ["Cookies"] = Z,
            ["logins.json"] = Z, ["key3.db"] = Z, ["key4.db"] = Z, ["signons.sqlite"] = Z,
            // Messenger databases
            ["msgstore.db"] = N, ["ChatStorage.sqlite"] = N
        };

    /// <summary>Extensions including the dot (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, SensitiveMarker> Extensions { get; } =
        new Dictionary<string, SensitiveMarker>(StringComparer.OrdinalIgnoreCase)
        {
            // Keys and certificates with private key. ".key" also matches Apple Keynote files;
            // accepted, a false positive only costs a manual review.
            [".pem"] = S, [".key"] = S, [".ppk"] = S, [".pfx"] = S, [".p12"] = S,
            [".jks"] = S, [".keystore"] = S, [".p8"] = S,
            // Password databases
            [".kdbx"] = Z, [".kdb"] = Z, [".psafe3"] = Z,
            // Mail archives and single mails
            [".pst"] = N, [".ost"] = N, [".mbox"] = N, [".mbx"] = N, [".eml"] = N,
            [".msg"] = N, [".dbx"] = N
        };

    /// <summary>
    /// Folder fragments matched against the file's directory, with <c>/</c> as separator,
    /// case-insensitive. A fragment matches whole path segments only.
    /// </summary>
    public static IReadOnlyList<(string Fragment, SensitiveMarker Markers, string Id)> FolderRules { get; } =
    [
        ("/.ssh/", S, "path:ssh"),
        ("/.gnupg/", S, "path:gnupg"),
        ("/.aws/", Z, "path:aws"),
        ("/Firefox/Profiles/", Z, "path:browser-profile"),
        ("/Chrome/User Data/", Z, "path:browser-profile"),
        ("/Chromium/User Data/", Z, "path:browser-profile"),
        ("/Edge/User Data/", Z, "path:browser-profile"),
        ("/BraveSoftware/", Z, "path:browser-profile"),
        ("/Opera Software/", Z, "path:browser-profile"),
        ("/Thunderbird/Profiles/", N, "path:mail-profile")
    ];

    /// <summary>
    /// Words in the file name. Matched against the lower-cased name with umlauts spelled out
    /// (<c>ä</c> → <c>ae</c>, <c>ß</c> → <c>ss</c>).
    /// </summary>
    public static IReadOnlyList<(string Keyword, SensitiveMarker Markers)> NameKeywords { get; } =
    [
        ("passw", Z), ("kennwort", Z), ("zugangsdaten", Z), ("credentials", Z),
        ("ausweis", A), ("reisepass", A), ("passport", A), ("fuehrerschein", A),
        ("kontoauszug", F), ("steuererklaerung", F), ("lohnabrechnung", F),
        ("gehaltsabrechnung", F), ("lohnsteuer", F), ("kreditkarte", F),
        ("arztbrief", G), ("befund", G), ("krankenkasse", G), ("krankmeldung", G), ("impfpass", G),
        ("chatverlauf", N)
    ];

    /// <summary>
    /// Names of folders that hold keys, logins or messages, for folders the scan skips without entering
    /// (dot folders, <c>AppData</c>). Matched case-insensitively against the folder name. Rule ids start
    /// with <c>dir:</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, (SensitiveMarker Markers, string Id)> SkippedFolderNames { get; } =
        new Dictionary<string, (SensitiveMarker, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [".ssh"] = (S, "dir:ssh"),
            [".gnupg"] = (S, "dir:gnupg"),
            [".aws"] = (Z, "dir:aws"),
            [".azure"] = (Z, "dir:azure"),
            [".kube"] = (Z, "dir:kube"),
            [".docker"] = (Z, "dir:docker"),
            [".password-store"] = (Z, "dir:password-store"),
            // Linux: browser profiles, tokens of many tools (Chromium, gcloud, gh, ...)
            [".config"] = (Z, "dir:config"),
            [".mozilla"] = (Z | N, "dir:browser-profile"),
            [".thunderbird"] = (N, "dir:mail-profile"),
            [".electrum"] = (S | F, "dir:wallet"),
            [".bitcoin"] = (S | F, "dir:wallet"),
            [".ethereum"] = (S | F, "dir:wallet"),
            // Windows: browser and mail profiles, saved logins
            ["AppData"] = (Z | N, "dir:appdata"),
            ["Application Data"] = (Z | N, "dir:appdata")
        };

    /// <summary>
    /// Rules for a folder the scan skips: its name (<see cref="SkippedFolderNames"/>) and the folder
    /// fragments (<see cref="FolderRules"/>) applied to the folder itself. Needs no file access, so the
    /// folder is never entered.
    /// </summary>
    /// <param name="folderName">Name of the skipped folder.</param>
    /// <param name="folderPath">Its path (absolute or relative); may be null.</param>
    public static SensitiveMatch MatchSkippedFolder(string folderName, string? folderPath)
    {
        var result = SensitiveMatch.None;
        if (!string.IsNullOrEmpty(folderName) && SkippedFolderNames.TryGetValue(folderName, out var byName))
            result = result.Combine(new SensitiveMatch(byName.Markers, byName.Id));

        if (!string.IsNullOrEmpty(folderPath))
        {
            var dir = "/" + folderPath.Replace('\\', '/').Trim('/') + "/";
            foreach (var (fragment, markers, id) in FolderRules)
            {
                var dirId = id.Replace("path:", "dir:", StringComparison.Ordinal);
                if (dir.Contains(fragment, StringComparison.OrdinalIgnoreCase) && result.Rule?.Contains(dirId, StringComparison.Ordinal) != true)
                    result = result.Combine(new SensitiveMatch(markers, dirId));
            }
        }

        return result;
    }

    /// <summary>Runs <see cref="MatchSkippedFolder"/> and stores the result on the node.</summary>
    public static void ApplyToSkippedFolder(DirectoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var match = MatchSkippedFolder(node.Name, node.FullPath);
        node.SensitiveMarkers = match.Markers;
        node.SensitiveRule = match.Rule;
    }

    private static readonly FileType[] NeverProbedTypes =
    [
        FileType.Image, FileType.Video, FileType.Audio, FileType.Archive,
        FileType.Executable, FileType.Font, FileType.Database
    ];

    private static readonly byte[] KeePassSignature = [0x03, 0xD9, 0xA2, 0x9A];

    /// <summary>
    /// Name, extension, folder and keyword rules. Needs no file access.
    /// </summary>
    /// <param name="fileName">File name with extension.</param>
    /// <param name="directoryPath">Directory that contains the file (absolute or relative); may be null.</param>
    public static SensitiveMatch MatchName(string fileName, string? directoryPath)
    {
        if (string.IsNullOrEmpty(fileName)) return SensitiveMatch.None;

        var result = SensitiveMatch.None;

        if (ExactNames.TryGetValue(fileName, out var exact))
            result = result.Combine(new SensitiveMatch(exact, "name:" + fileName.ToLowerInvariant()));

        // .env.local, .env.production, ...
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            result = result.Combine(new SensitiveMatch(Z, "name:.env.*"));

        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && Extensions.TryGetValue(ext, out var byExt))
            result = result.Combine(new SensitiveMatch(byExt, "ext:" + ext.ToLowerInvariant()));

        if (!string.IsNullOrEmpty(directoryPath))
        {
            var dir = "/" + directoryPath.Replace('\\', '/').Trim('/') + "/";
            foreach (var (fragment, markers, id) in FolderRules)
            {
                if (dir.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    result = result.Combine(new SensitiveMatch(markers, id));
            }
        }

        var normalized = NormalizeName(fileName);
        foreach (var (keyword, markers) in NameKeywords)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
                result = result.Combine(new SensitiveMatch(markers, "word:" + keyword));
        }

        return result;
    }

    /// <summary>Whether the header rules should read the first bytes of this file.</summary>
    public static bool ShouldProbeHeader(FileType fileType, long size)
        => size > 0 && size <= MaxHeaderProbeFileSize && !NeverProbedTypes.Contains(fileType);

    /// <summary>
    /// Header rules on the first bytes of a file: PEM/OpenSSH private-key header lines,
    /// PGP private key blocks, PuTTY key files and the KeePass signature.
    /// </summary>
    public static SensitiveMatch MatchHeader(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty) return SensitiveMatch.None;

        if (prefix.Length >= 8 && prefix[..4].SequenceEqual(KeePassSignature) &&
            prefix[5] == 0xFB && prefix[6] == 0x4B && prefix[7] == 0xB5 &&
            (prefix[4] == 0x67 || prefix[4] == 0x65))
        {
            return new SensitiveMatch(Z, "header:keepass");
        }

        // Latin-1 maps every byte to one char, so binary garbage cannot throw.
        var text = Encoding.Latin1.GetString(prefix);
        var result = SensitiveMatch.None;

        if (ContainsPemPrivateKeyHeader(text))
            result = result.Combine(new SensitiveMatch(S, "header:private-key"));
        if (text.Contains("-----BEGIN PGP PRIVATE KEY BLOCK-----", StringComparison.Ordinal))
            result = result.Combine(new SensitiveMatch(S, "header:pgp-private-key"));
        if (text.Contains("PuTTY-User-Key-File-", StringComparison.Ordinal))
            result = result.Combine(new SensitiveMatch(S, "header:putty-key"));

        return result;
    }

    /// <summary>
    /// All rules for one file. <paramref name="readPrefix"/> is only called when
    /// <see cref="ShouldProbeHeader"/> allows it; a failing read is treated as "no header".
    /// </summary>
    public static SensitiveMatch Detect(
        string fileName, string? directoryPath, FileType fileType, long size, Func<int, byte[]>? readPrefix)
    {
        var result = MatchName(fileName, directoryPath);

        if (readPrefix is not null && ShouldProbeHeader(fileType, size))
        {
            byte[] prefix;
            try
            {
                prefix = readPrefix(HeaderProbeBytes);
            }
            catch (IOException) { prefix = []; }
            catch (UnauthorizedAccessException) { prefix = []; }

            result = result.Combine(MatchHeader(prefix));
        }

        return result;
    }

    /// <summary>Runs <see cref="Detect"/> and stores the result on the node.</summary>
    public static void Apply(FileNode node, Func<int, byte[]>? readPrefix)
    {
        ArgumentNullException.ThrowIfNull(node);
        var match = Detect(node.Name, node.ParentPath, node.FileType, node.Size, readPrefix);
        node.SensitiveMarkers = match.Markers;
        node.SensitiveRule = match.Rule;
        node.SensitiveRulesVersion = Version;
    }

    private static bool ContainsPemPrivateKeyHeader(string text)
    {
        // "-----BEGIN [RSA |EC |DSA |OPENSSH |ENCRYPTED ]PRIVATE KEY-----"
        const string begin = "-----BEGIN ";
        const string end = "PRIVATE KEY-----";

        var index = 0;
        while ((index = text.IndexOf(begin, index, StringComparison.Ordinal)) >= 0)
        {
            var lineEnd = text.IndexOfAny(['\r', '\n'], index);
            var line = lineEnd < 0 ? text[index..] : text[index..lineEnd];
            if (line.Contains(end, StringComparison.Ordinal)) return true;
            index += begin.Length;
        }
        return false;
    }

    private static string NormalizeName(string fileName)
    {
        var lower = fileName.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture);
        return lower.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
    }
}
