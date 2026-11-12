using System.Text;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// One test per rule of <see cref="SensitiveFileRules"/>. All inputs are made up: file names
/// only, and header bytes assembled at run time from a header line plus filler text, so the
/// repository contains no key material (and no secret scanner has anything to find).
/// </summary>
public class SensitiveFileRulesTests
{
    private const SensitiveMarker Z = SensitiveMarker.Credentials;
    private const SensitiveMarker S = SensitiveMarker.Keys;
    private const SensitiveMarker N = SensitiveMarker.PrivateMessage;
    private const SensitiveMarker A = SensitiveMarker.Identity;
    private const SensitiveMarker F = SensitiveMarker.Finance;
    private const SensitiveMarker G = SensitiveMarker.Health;

    // ------------------------------------------------------------------ marker tokens

    [Fact]
    public void Tokens_AreTheFixedGermanStrings()
    {
        Assert.Equal(
            ["credentials", "keys", "private_message", "identity", "finance", "health"],
            SensitiveMarkerNames.AllTokens);
    }

    [Fact]
    public void Format_And_Parse_RoundTrip()
    {
        var all = Z | S | N | A | F | G;
        Assert.Equal("credentials,keys,private_message,identity,finance,health",
            SensitiveMarkerNames.Format(all));
        Assert.Equal(all, SensitiveMarkerNames.Parse(SensitiveMarkerNames.Format(all)));
        Assert.Null(SensitiveMarkerNames.Format(SensitiveMarker.None));
        Assert.Equal(SensitiveMarker.None, SensitiveMarkerNames.Parse(null));
    }

    [Fact]
    public void Parse_IgnoresUnknownTokens_AndCase()
        => Assert.Equal(S | F, SensitiveMarkerNames.Parse(" Keys; geheim ,FINANCE "));

    // ------------------------------------------------------------------ exact names

    [Theory]
    [InlineData("id_rsa", S)]
    [InlineData("id_dsa", S)]
    [InlineData("id_ecdsa", S)]
    [InlineData("id_ed25519", S)]
    [InlineData("id_ecdsa_sk", S)]
    [InlineData("id_ed25519_sk", S)]
    [InlineData("secring.gpg", S)]
    [InlineData("secring.skr", S)]
    [InlineData("wallet.dat", S | F)]
    [InlineData(".env", Z)]
    [InlineData(".netrc", Z)]
    [InlineData("_netrc", Z)]
    [InlineData(".pgpass", Z)]
    [InlineData(".git-credentials", Z)]
    [InlineData(".htpasswd", Z)]
    [InlineData("credentials", Z)]
    [InlineData("Login Data", Z)]
    [InlineData("Login Data For Account", Z)]
    [InlineData("Web Data", Z)]
    [InlineData("Cookies", Z)]
    [InlineData("logins.json", Z)]
    [InlineData("key3.db", Z)]
    [InlineData("key4.db", Z)]
    [InlineData("signons.sqlite", Z)]
    [InlineData("msgstore.db", N)]
    [InlineData("ChatStorage.sqlite", N)]
    public void ExactName_SetsMarker(string name, SensitiveMarker expected)
    {
        var match = SensitiveFileRules.MatchName(name, "/data/old");
        Assert.True((match.Markers & expected) == expected, $"{name}: got {match.Markers}");
        Assert.Contains("name:", match.Rule);
    }

    [Fact]
    public void EveryExactNameRule_IsCoveredByATest()
    {
        var tested = typeof(SensitiveFileRulesTests).GetMethod(nameof(ExactName_SetsMarker))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(SensitiveFileRules.ExactNames.Keys, k => Assert.Contains(k, tested));
    }

    [Fact]
    public void ExactName_IsCaseInsensitive()
        => Assert.Equal(S, SensitiveFileRules.MatchName("ID_RSA", null).Markers);

    [Theory]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    public void DotEnvVariants_AreCredentials(string name)
    {
        var match = SensitiveFileRules.MatchName(name, null);
        Assert.Equal(Z, match.Markers);
        Assert.Contains("name:.env.*", match.Rule);
    }

    // ------------------------------------------------------------------ extensions

    [Theory]
    [InlineData("server.pem", S)]
    [InlineData("server.key", S)]
    [InlineData("putty.ppk", S)]
    [InlineData("cert.pfx", S)]
    [InlineData("cert.p12", S)]
    [InlineData("store.jks", S)]
    [InlineData("release.keystore", S)]
    [InlineData("AuthKey.p8", S)]
    [InlineData("Passwoerter-alt.KDBX", Z)]
    [InlineData("old.kdb", Z)]
    [InlineData("safe.psafe3", Z)]
    [InlineData("Outlook.pst", N)]
    [InlineData("Outlook.ost", N)]
    [InlineData("Inbox.mbox", N)]
    [InlineData("Inbox.mbx", N)]
    [InlineData("Antwort.eml", N)]
    [InlineData("Antwort.msg", N)]
    [InlineData("Posteingang.dbx", N)]
    public void Extension_SetsMarker(string name, SensitiveMarker expected)
    {
        var match = SensitiveFileRules.MatchName(name, null);
        Assert.True((match.Markers & expected) == expected, $"{name}: got {match.Markers}");
        Assert.Contains("ext:", match.Rule);
    }

    [Fact]
    public void EveryExtensionRule_IsCoveredByATest()
    {
        var tested = typeof(SensitiveFileRulesTests).GetMethod(nameof(Extension_SetsMarker))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => Path.GetExtension((string)a.GetData(null!).First()[0]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(SensitiveFileRules.Extensions.Keys, k => Assert.Contains(k, tested));
    }

    // ------------------------------------------------------------------ folders

    [Theory]
    [InlineData("/home/someone/.ssh", "config", S, "path:ssh")]
    [InlineData("C:\\Users\\someone\\.ssh", "known_hosts", S, "path:ssh")]
    [InlineData("/home/someone/.gnupg/private-keys-v1.d", "ABC.key", S, "path:gnupg")]
    [InlineData("/home/someone/.aws", "config", Z, "path:aws")]
    [InlineData("/backup/AppData/Roaming/Mozilla/Firefox/Profiles/abc.default", "places.sqlite", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Local/Google/Chrome/User Data/Default", "History", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Local/Chromium/User Data/Default", "History", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Local/Microsoft/Edge/User Data/Default", "History", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Local/BraveSoftware/Brave-Browser", "Local State", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Roaming/Opera Software/Opera Stable", "History", Z, "path:browser-profile")]
    [InlineData("/backup/AppData/Roaming/Thunderbird/Profiles/xyz.default/Mail", "Inbox", N, "path:mail-profile")]
    public void Folder_SetsMarker(string dir, string name, SensitiveMarker expected, string rule)
    {
        var match = SensitiveFileRules.MatchName(name, dir);
        Assert.True((match.Markers & expected) == expected, $"{dir}/{name}: got {match.Markers}");
        Assert.Contains(rule, match.Rule);
    }

    [Fact]
    public void EveryFolderRule_IsCoveredByATest()
    {
        var tested = typeof(SensitiveFileRulesTests).GetMethod(nameof(Folder_SetsMarker))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => "/" + ((string)a.GetData(null!).First()[0]).Replace('\\', '/').Trim('/') + "/")
            .ToList();
        Assert.All(SensitiveFileRules.FolderRules, r =>
            Assert.Contains(tested, d => d.Contains(r.Fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Folder_MatchesWholeSegmentsOnly()
        => Assert.False(SensitiveFileRules.MatchName("notes.txt", "/data/my.sshstuff").IsMatch);

    // ------------------------------------------------------------------ keywords

    [Theory]
    [InlineData("Passwort-Liste.txt", Z)]
    [InlineData("passwords_2009.xlsx", Z)]
    [InlineData("Kennwort WLAN.txt", Z)]
    [InlineData("Zugangsdaten Bank.docx", Z)]
    [InlineData("aws-credentials.csv", Z)]
    [InlineData("Personalausweis_Max_Muster.pdf", A)]
    [InlineData("Reisepass Scan.jpg", A)]
    [InlineData("passport-scan.png", A)]
    [InlineData("Führerschein.jpg", A)]
    [InlineData("Kontoauszug_2014_03.pdf", F)]
    [InlineData("Steuererklärung 2019.pdf", F)]
    [InlineData("Lohnabrechnung Mai.pdf", F)]
    [InlineData("Gehaltsabrechnung_2020.pdf", F)]
    [InlineData("Lohnsteuerbescheinigung.pdf", F)]
    [InlineData("Kreditkarte Rückseite.jpg", F)]
    [InlineData("Arztbrief Klinik.pdf", G)]
    [InlineData("Befund MRT.pdf", G)]
    [InlineData("Krankenkasse Antrag.pdf", G)]
    [InlineData("Krankmeldung.pdf", G)]
    [InlineData("Impfpass.jpg", G)]
    [InlineData("Chatverlauf export.txt", N)]
    public void Keyword_SetsMarker(string name, SensitiveMarker expected)
    {
        var match = SensitiveFileRules.MatchName(name, "/data");
        Assert.True((match.Markers & expected) == expected, $"{name}: got {match.Markers}");
        Assert.Contains("word:", match.Rule);
    }

    [Fact]
    public void EveryKeywordRule_IsCoveredByATest()
    {
        var names = typeof(SensitiveFileRulesTests).GetMethod(nameof(Keyword_SetsMarker))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0])
            .ToList();
        Assert.All(SensitiveFileRules.NameKeywords, k =>
            Assert.Contains(names, n => SensitiveFileRules.MatchName(n, null).Rule?.Contains("word:" + k.Keyword) == true));
    }

    [Theory]
    [InlineData("id_rsa.pub")]
    [InlineData("IMG_2041.jpg")]
    [InlineData("Urlaub Strand.mp4")]
    [InlineData("readme.txt")]
    [InlineData("environment.yml")]
    [InlineData("setup.exe")]
    public void OrdinaryFiles_AreNotMarked(string name)
        => Assert.False(SensitiveFileRules.MatchName(name, "/data/Fotos").IsMatch, name);

    // ------------------------------------------------------------------ header lines

    /// <summary>Header line plus obviously fake filler. Never valid key material.</summary>
    private static byte[] FakeKeyFile(string headerKind, string preamble = "")
    {
        var text = preamble
            + "-----BEGIN " + headerKind + "PRIVATE KEY-----\n"
            + "THIS-IS-NOT-A-KEY-THIS-IS-TEST-FILLER-ONLY\n"
            + "-----END " + headerKind + "PRIVATE KEY-----\n";
        return Encoding.ASCII.GetBytes(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RSA ")]
    [InlineData("EC ")]
    [InlineData("DSA ")]
    [InlineData("OPENSSH ")]
    [InlineData("ENCRYPTED ")]
    public void Header_PemPrivateKey_IsKey(string kind)
    {
        var match = SensitiveFileRules.MatchHeader(FakeKeyFile(kind));
        Assert.Equal(S, match.Markers);
        Assert.Equal("header:private-key", match.Rule);
    }

    [Fact]
    public void Header_PemPrivateKey_AfterPreamble_IsKey()
        => Assert.Equal(S, SensitiveFileRules.MatchHeader(FakeKeyFile("RSA ", "Bag Attributes\nfriendlyName: test\n")).Markers);

    [Fact]
    public void Header_PgpPrivateKeyBlock_IsKey()
    {
        var bytes = Encoding.ASCII.GetBytes("-----BEGIN PGP " + "PRIVATE KEY BLOCK-----\n\nTEST-FILLER\n");
        var match = SensitiveFileRules.MatchHeader(bytes);
        Assert.Equal(S, match.Markers);
        Assert.Contains("header:pgp-private-key", match.Rule);
    }

    [Fact]
    public void Header_PuttyKeyFile_IsKey()
    {
        var bytes = Encoding.ASCII.GetBytes("PuTTY-User-Key-File-" + "3: ssh-ed25519\nEncryption: none\nComment: test filler\n");
        var match = SensitiveFileRules.MatchHeader(bytes);
        Assert.Equal(S, match.Markers);
        Assert.Equal("header:putty-key", match.Rule);
    }

    [Theory]
    [InlineData((byte)0x67)] // KDBX 2.x-4.x
    [InlineData((byte)0x65)] // KDB 1.x
    public void Header_KeePassSignature_IsCredentials(byte variant)
    {
        byte[] bytes = [0x03, 0xD9, 0xA2, 0x9A, variant, 0xFB, 0x4B, 0xB5, 0x00, 0x00, 0x04, 0x00];
        var match = SensitiveFileRules.MatchHeader(bytes);
        Assert.Equal(Z, match.Markers);
        Assert.Equal("header:keepass", match.Rule);
    }

    [Fact]
    public void Header_CertificateOrPublicKey_IsNotMarked()
    {
        var text = "-----BEGIN CERTIFICATE-----\nFILLER\n-----END CERTIFICATE-----\n"
                 + "-----BEGIN PUBLIC KEY-----\nFILLER\n-----END PUBLIC KEY-----\n";
        Assert.False(SensitiveFileRules.MatchHeader(Encoding.ASCII.GetBytes(text)).IsMatch);
    }

    [Fact]
    public void Header_SplitAcrossLines_IsNotMarked()
    {
        var text = "-----BEGIN RSA\nPRIVATE KEY-----\n";
        Assert.False(SensitiveFileRules.MatchHeader(Encoding.ASCII.GetBytes(text)).IsMatch);
    }

    [Fact]
    public void Header_EmptyOrBinary_IsNotMarked()
    {
        Assert.False(SensitiveFileRules.MatchHeader([]).IsMatch);
        Assert.False(SensitiveFileRules.MatchHeader([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]).IsMatch);
    }

    // ------------------------------------------------------------------ probing

    [Theory]
    [InlineData(FileType.Image)]
    [InlineData(FileType.Video)]
    [InlineData(FileType.Audio)]
    [InlineData(FileType.Archive)]
    [InlineData(FileType.Executable)]
    [InlineData(FileType.Font)]
    [InlineData(FileType.Database)]
    public void MediaAndBinaryTypes_AreNotProbed(FileType type)
        => Assert.False(SensitiveFileRules.ShouldProbeHeader(type, 1024));

    [Fact]
    public void LargeOrEmptyFiles_AreNotProbed()
    {
        Assert.False(SensitiveFileRules.ShouldProbeHeader(FileType.Other, SensitiveFileRules.MaxHeaderProbeFileSize + 1));
        Assert.False(SensitiveFileRules.ShouldProbeHeader(FileType.Other, 0));
        Assert.True(SensitiveFileRules.ShouldProbeHeader(FileType.Other, 2048));
        Assert.True(SensitiveFileRules.ShouldProbeHeader(FileType.Unknown, 2048));
        Assert.True(SensitiveFileRules.ShouldProbeHeader(FileType.Document, 2048));
    }

    [Fact]
    public void Detect_CombinesNameAndHeader()
    {
        var match = SensitiveFileRules.Detect("server.key", "/etc", FileType.Other, 300, _ => FakeKeyFile("EC "));
        Assert.Equal(S, match.Markers);
        Assert.Equal("ext:.key;header:private-key", match.Rule);
    }

    [Fact]
    public void Detect_FindsKeyInInnocentlyNamedFile()
    {
        var match = SensitiveFileRules.Detect("notes", "/data", FileType.Unknown, 300, _ => FakeKeyFile("OPENSSH "));
        Assert.Equal(S, match.Markers);
    }

    [Fact]
    public void Detect_UnreadableFile_DoesNotThrow()
    {
        var match = SensitiveFileRules.Detect("notes", "/data", FileType.Unknown, 300,
            _ => throw new UnauthorizedAccessException("denied"));
        Assert.False(match.IsMatch);
    }

    [Fact]
    public void Detect_DoesNotReadMedia()
    {
        var reads = 0;
        SensitiveFileRules.Detect("photo.jpg", "/data", FileType.Image, 300, _ => { reads++; return []; });
        Assert.Equal(0, reads);
    }
}
