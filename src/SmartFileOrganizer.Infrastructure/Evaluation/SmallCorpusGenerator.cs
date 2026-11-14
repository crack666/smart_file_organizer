using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Application.Evaluation;

namespace SmartFileOrganizer.Infrastructure.Evaluation;

/// <summary>What <see cref="SmallCorpusGenerator.Generate"/> wrote.</summary>
/// <param name="RootPath">The folder to scan (<c>tree</c> below <paramref name="Directory"/>).</param>
public sealed record GeneratedCorpus(string Directory, string TruthPath, string RootPath, EvalCorpusInfo Corpus, int Directories, int Files);

/// <summary>
/// Generates the small evaluation corpus of https://github.com/crack666/smart_file_organizer/issues/36:
/// about forty folders of the kinds a private drive really has (mixed downloads and desktop,
/// photo folders per occasion, documents by topic, software projects, programs and a game, a
/// phone dump, music, videos, a hidden key folder) plus dummies of sensitive files. The truth
/// for every folder and file is written to <c>truth.json</c> next to the scanned folder.
/// </summary>
/// <remarks>
/// <para>Everything is invented: names, texts, pictures. Images are small painted PNGs (also the
/// ones named <c>.jpg</c>, because scan rules and profiles work on names and image decoders sniff
/// the content); PDFs are minimal text or scan pages; media, programs and archives are headers
/// with filler. Sensitive dummies have a real header line and random filler, assembled at run time,
/// so no valid key or secret exists in the repository or the corpus.</para>
/// <para>The output depends only on the seed: same seed, same bytes, same timestamps.</para>
/// <para>The truth is meant to be unambiguous. Where the categories of the prompt allow two
/// answers (a password list or a shopping list is <c>document</c>, <c>review_needed</c> or
/// <c>trash_candidate</c>), the category is <c>null</c> and only the markers are measured. Files inside a unit (project, program,
/// game) have no category, because units are not classified file by file. Verdict codes follow
/// the rules of docs/architecture.md: a folder of uniform collections of one file type is
/// <c>uniform</c>, a folder of units or of dumps is <c>uniform</c>, everything inside a unit is <c>unit</c>.
/// A dump (phone backup, user profile) is <c>dump</c>; its worth-keeping folders have verdicts of their own and its
/// noise (app data, caches) is <c>system</c>, with no category for the files there.</para>
/// </remarks>
public static class SmallCorpusGenerator
{
    public const string Name = "small";

    /// <summary>Raise when the content or the truth changes, so stored runs of different corpora are not compared as equal.</summary>
    /// <remarks>Version 2 (https://github.com/crack666/smart_file_organizer/issues/50): counter-samples for the
    /// finance and private_message boundaries, categories of money, medical and scanned papers; see docs/evaluation.md.
    /// Version 3 (https://github.com/crack666/smart_file_organizer/issues/59): a dump is no unit any more (the phone
    /// backup's camera is judged), and a folder with two user profiles full of noise.
    /// Version 4 (https://github.com/crack666/smart_file_organizer/issues/67): what rules decide without the model (the
    /// public profile with learning material, account pictures and shortcuts, a package manager, system files, a folder
    /// with one file) and an archive of bank statements under a number.
    /// Version 5 (https://github.com/crack666/smart_file_organizer/issues/56): a folder <c>Import</c> with photos of two
    /// occasions of one day, a lake outing and two invoices, and a library next to the tree with <c>targets.json</c>.
    /// Version 6 (https://github.com/crack666/smart_file_organizer/issues/85): closed units in the library (a film
    /// folder under an everyday word, a season, a game cache, a program) with own pictures and clips of their time in
    /// <c>Import</c> that belong into none of them, and a library that is gone.</remarks>
    public const int GeneratorVersion = 6;

    public const int DefaultSeed = 36;
    public const string RootFolder = "tree";

    /// <summary>The library of the corpus, next to <see cref="RootFolder"/>; see <c>targets.json</c>.</summary>
    public const string LibraryFolder = "ablage";

    public static GeneratedCorpus Generate(string outputDirectory, int seed = DefaultSeed)
    {
        var dir = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"Output folder is not empty: {dir}");

        var root = Path.Combine(dir, RootFolder);
        var b = new Builder(root, new Random(seed));
        Populate(b);

        var corpus = new EvalCorpusInfo(Name, GeneratorVersion, seed,
            "Small invented corpus: mixed downloads and desktop, photo folders per occasion, documents by topic, " +
            "projects, programs, a phone dump, two user profiles with noise, media and sensitive dummies.");
        var doc = new EvalTruthDocument(EvalTruth.SchemaVersion, corpus, RootFolder, b.Directories, b.Files);
        var truthPath = Path.Combine(dir, EvalTruth.FileName);
        File.WriteAllText(truthPath, JsonSerializer.Serialize(doc, EvalJson.Options) + "\n", new UTF8Encoding(false));
        SmallCorpusFindings.Write(dir);
        Library(dir, new Random(seed + 1));

        b.ApplyDirectoryTimes();
        return new GeneratedCorpus(dir, truthPath, root, corpus, b.Directories.Count, b.Files.Count);
    }

    // Codes of issue 35 (English). Compared through EvalCodes, never as strings.
    private const string Unit = "unit", Uniform = "uniform", Mixed = "mixed", Empty = "empty", SystemDir = "system", Dump = "dump";
    private const string Photo = "photo", Screenshot = "screenshot", Video = "video", Invoice = "invoice", Document = "document",
        DocumentScan = "document_scan", PersonalDocument = "personal_document", Letter = "letter";
    private const string Credentials = "credentials", Keys = "keys", PrivateMessage = "private_message", Identity = "identity",
        Finance = "finance", Health = "health";

    private static readonly string[] None = [];

    private static void Populate(Builder b)
    {
        b.Dir(".", Mixed, "Drive root: folders with different purposes.");

        // ── Downloads: mixed, from #29 "lose Dateien", installers next to documents ──
        b.Dir("Downloads", Mixed, "Download folder without a common purpose.");
        b.File("Downloads/setup_fotoeditor_3.2.exe", Exe(b), D(2024, 1, 14), null, None, "Installer; not classified (executable).");
        b.File("Downloads/Rechnung_2024-03_Stromanbieter.pdf", MiniPdf.Text("Rechnung Nr. 2024-0317", [
            "Stadtwerke Beispielstadt GmbH, Musterweg 1, 12345 Beispielstadt",
            "Kundennummer 4711-0815",
            "Stromlieferung Maerz 2024: 212 kWh",
            "Arbeitspreis 0,32 EUR/kWh: 67,84 EUR",
            "Grundpreis: 12,50 EUR",
            "Rechnungsbetrag: 80,34 EUR, faellig am 15.04.2024"]), D(2024, 4, 2), Invoice, None,
            "An invoice is not a finance marker: the marker list names bank statements, tax papers, payslips, card data.");
        b.File("Downloads/Screenshot_2024-05-02_101500.png", Screen(b, 0), D(2024, 5, 2), Screenshot, None);
        b.File("Downloads/IMG_20230812_143012.jpg", PhotoOf(b, Scene.Lake), D(2023, 8, 12), Photo, None);
        b.File("Downloads/Bedienungsanleitung_Router.pdf", MiniPdf.Text("Bedienungsanleitung WLAN-Router X200", [
            "1. Router mit dem Stromnetz verbinden.",
            "2. LAN-Kabel in die gelbe Buchse stecken.",
            "3. Im Browser die Adresse des Routers oeffnen.",
            "4. Den Einrichtungsassistenten durchlaufen.",
            "Technische Daten: 4 LAN-Anschluesse, WLAN 2,4 und 5 GHz."]), D(2022, 11, 3), Document, None);
        b.File("Downloads/fotos_export.zip", EmptyZip(), D(2023, 9, 1), null, None, "Archive; not classified.");
        b.File("Downloads/Kontoauszug_2024_03.pdf", MiniPdf.Text("Kontoauszug Maerz 2024", [
            "Beispielbank eG, Konto 1234 5678 90",
            "01.03. Gehalt Musterfirma        +2.310,00",
            "03.03. Miete                       -780,00",
            "12.03. Supermarkt                   -64,13",
            "Neuer Saldo: 3.118,52 EUR"]), D(2024, 4, 1), PersonalDocument, [Finance],
            "Bank statement: a record about the person, not correspondence (version 2; was null in version 1).");
        b.File("Downloads/install.log", Text("2024-01-14 10:02:11 Installation started\n2024-01-14 10:02:40 Installation finished\n"),
            D(2024, 1, 14), null, None, "Log file; not classified (no document type).");

        // ── Desktop: mixed, with credentials lying around ──
        b.Dir("Desktop", Mixed, "Desktop: notes, screenshots and things put down for later.");
        b.File("Desktop/Passwoerter.txt", Text(Filler(b, "Zugang ", 6)), D(2023, 3, 9), null, [Credentials],
            "Password list; category ambiguous (document or review_needed).");
        b.File("Desktop/wiederherstellungscodes.txt", Text("Wiederherstellungscodes\n" + Codes(b, 8)), D(2023, 6, 21), null, [Credentials],
            "Recovery codes; no name rule matches, only the model can find it.");
        b.File("Desktop/Einkaufsliste.txt", Text("Milch\nBrot\nAepfel\nKaffee\nSpuelmittel\n"), D(2024, 6, 1), null, None,
            "Category ambiguous (document, review_needed, trash_candidate).");
        b.File("Desktop/Screenshot_2024-06-11_084512.png", Screen(b, 1), D(2024, 6, 11), Screenshot, None);
        b.File("Desktop/Kuendigung_Fitnessstudio.txt", Text(
            "Max Beispiel\nMusterweg 2\n12345 Beispielstadt\n\nFitnessstudio Aktiv\n\nKuendigung meiner Mitgliedschaft\n\n" +
            "Sehr geehrte Damen und Herren,\nhiermit kuendige ich meine Mitgliedschaft (Nr. 20931) zum naechstmoeglichen Termin.\n" +
            "Bitte bestaetigen Sie mir die Kuendigung schriftlich.\n\nMit freundlichen Gruessen\nMax Beispiel\n"),
            D(2024, 2, 27), Letter, None, "Formal letter to a company; not a private message.");
        b.File("Desktop/Brief_an_Lisa.txt", Text(
            "Max Beispiel\nMusterweg 2\n12345 Beispielstadt\n\nLisa Muster\nAm Bach 7\n54321 Nachbarort\n\n" +
            "Liebe Lisa,\nseit du weggezogen bist, ist es hier ziemlich still. Mit der Trennung geht es mir inzwischen besser,\n" +
            "auch wenn ich nachts oft wach liege. Danke, dass du am Telefon so lange zugehoert hast.\n" +
            "Kommst du im Herbst vorbei? Ich wuerde mich sehr freuen.\n\nDein Max\n"),
            D(2024, 3, 12), Letter, [PrivateMessage],
            "Counter-sample of version 2: a personal letter laid out like a formal one (addresses on top); must stay private_message.");
        b.File("Desktop/Tresor.kdbx", Kdbx(b), D(2024, 5, 30), null, [Credentials], "Password store dummy (header and filler).");
        b.File("Desktop/vpn-zugang.pfx", Pfx(b), D(2022, 10, 4), null, [Keys], "Certificate with key, dummy.");
        b.File("Desktop/Steuer-Programm.lnk", Binary(b, [0x4C, 0x00, 0x00, 0x00], 420), D(2023, 2, 2), null, None, "Shortcut.");

        // ── Photos: one folder per occasion, inconsistent names as in #29 ──
        b.Dir("Fotos", Uniform, "Only photo collections below (empty folders are ignored).");
        b.Dir("Fotos/2019 Skifahrt", Uniform, "Photos of one trip.");
        for (var i = 0; i < 6; i++)
            b.File($"Fotos/2019 Skifahrt/IMG_20190212_{101530 + i * 217}.jpg", PhotoOf(b, Scene.Snow), D(2019, 2, 12, 10, 15 + i * 3), Photo, None);
        b.Dir("Fotos/Sommerfest 2022", Uniform, "Photos and a phone clip of one occasion; a clip there is no mix.");
        for (var i = 0; i < 5; i++)
            b.File($"Fotos/Sommerfest 2022/IMG_20220716_{180112 + i * 305}.jpg", PhotoOf(b, Scene.Garden), D(2022, 7, 16, 18, 1 + i * 5), Photo, None);
        b.File("Fotos/Sommerfest 2022/VID_20220716_183001.mp4", Mp4(b, 48_000), D(2022, 7, 16, 18, 30), Video, None);
        b.Dir("Fotos/hochzeit anna und ben", Uniform, "Photos of one occasion; lower-case name without a date.");
        for (var i = 0; i < 5; i++)
            b.File($"Fotos/hochzeit anna und ben/DSC_{412 + i:0000}.jpg", PhotoOf(b, Scene.Wedding), D(2021, 9, 4, 14, 10 + i * 7), Photo, None);
        b.Dir("Fotos/100MEDIA", Uniform, "Camera folder name; photos of one card.");
        for (var i = 0; i < 4; i++)
            b.File($"Fotos/100MEDIA/IMG_{i + 1:0000}.JPG", PhotoOf(b, Scene.Lake), D(2020, 5, 23, 9, 2 + i * 11), Photo, None);
        b.Dir("Fotos/Urlaub Italien", Uniform, "Photos of one trip plus a thumbnail cache file.");
        for (var i = 0; i < 4; i++)
            b.File($"Fotos/Urlaub Italien/P{1010231 + i * 4}.jpg", PhotoOf(b, Scene.Coast), D(2018, 6, 3 + i, 11, 0), Photo, None);
        b.File("Fotos/Urlaub Italien/Thumbs.db", Binary(b, [0xD0, 0xCF, 0x11, 0xE0], 2048), D(2018, 6, 10), null, None, "Thumbnail cache.");
        b.Dir("Fotos/Neuer Ordner", Empty, "Placeholder name, nothing inside.");

        // ── Documents by topic ──
        b.Dir("Dokumente", Uniform, "Only document collections below.");
        b.Dir("Dokumente/Rechnungen 2023", Uniform, "Invoices of one year.");
        string[][] invoices =
        [
            ["2023-01_Internet.pdf", "Rechnung Internetanschluss Januar 2023", "Tarif Surf 100: 39,99 EUR"],
            ["2023-04_Handwerker.pdf", "Rechnung Malerbetrieb Pinsel", "Wohnzimmer streichen, 14 m2: 412,00 EUR"],
            ["2023-07_Moebelhaus.pdf", "Rechnung Moebelhaus Holzweg", "Regal Eiche, Lieferung: 289,00 EUR"],
            ["2023-11_Autowerkstatt.pdf", "Rechnung Autowerkstatt Schraube", "Inspektion und Oelwechsel: 247,30 EUR"]
        ];
        for (var i = 0; i < invoices.Length; i++)
            b.File($"Dokumente/Rechnungen 2023/{invoices[i][0]}", MiniPdf.Text(invoices[i][1],
                [$"Rechnungsnummer R-2023-{100 + i * 37}", invoices[i][2], "Zahlbar innerhalb von 14 Tagen ohne Abzug."]),
                D(2023, 1 + i * 3, 20), Invoice, None);
        b.Dir("Dokumente/Steuer 2022", Uniform, "Tax papers of one year.");
        b.File("Dokumente/Steuer 2022/Steuererklaerung_2022.pdf", MiniPdf.Text("Einkommensteuererklaerung 2022", [
            "Steuernummer 12/345/67890", "Einkuenfte aus nichtselbstaendiger Arbeit", "Werbungskosten: Fahrten zur Arbeit"]),
            D(2023, 5, 14), PersonalDocument, [Finance], "Tax return: tax papers are a personal document (version 2; was null).");
        b.File("Dokumente/Steuer 2022/Lohnsteuerbescheinigung_2022.pdf", MiniPdf.Text("Ausdruck der elektronischen Lohnsteuerbescheinigung 2022", [
            "Arbeitgeber: Musterfirma GmbH", "Bruttoarbeitslohn: 33.720,00 EUR", "Einbehaltene Lohnsteuer: 4.912,00 EUR"]),
            D(2023, 2, 10), PersonalDocument, [Finance], "Tax certificate of the employer: tax papers are a personal document (version 2; was null).");
        // Counter-samples of version 2 (https://github.com/crack666/smart_file_organizer/issues/50): money papers whose
        // names no rule knows, so only the model can mark them; a too narrow finance boundary shows here.
        b.Dir("Dokumente/Bank", Uniform, "Money papers of the person.");
        b.File("Dokumente/Bank/Umsaetze_Girokonto_2024_05.pdf", MiniPdf.Text("Umsatzuebersicht Girokonto Mai 2024", [
            "Beispielbank eG, IBAN DE00 0000 0000 0000 0000 00",
            "02.05. Gutschrift Musterfirma Lohn    +2.310,00",
            "04.05. Lastschrift Vermieterin          -780,00",
            "17.05. Kartenzahlung Baumarkt            -38,90",
            "Kontostand am 31.05.: 2.904,17 EUR"]), D(2024, 6, 1), PersonalDocument, [Finance],
            "Bank statement whose name no rule knows; must stay finance, not be taken for an invoice.");
        b.File("Dokumente/Bank/Verdienstnachweis_2023_05.pdf", MiniPdf.Text("Verdienstnachweis Mai 2023", [
            "Musterfirma GmbH, Personalnummer 00417",
            "Grundgehalt: 2.810,00 EUR",
            "Lohnsteuer, Solidaritaetszuschlag, Sozialversicherung: -1.024,37 EUR",
            "Auszahlungsbetrag: 1.785,63 EUR"]), D(2023, 5, 31), PersonalDocument, [Finance],
            "Payslip whose name no rule knows; must stay finance.");
        b.Dir("Dokumente/Gesundheit", Uniform, "Medical papers.");
        b.File("Dokumente/Gesundheit/Arztbrief_Orthopaedie_2023.pdf", MiniPdf.Text("Arztbrief", [
            "Praxis fuer Orthopaedie Dr. Knie", "Diagnose: Reizung der Patellasehne rechts", "Empfehlung: Physiotherapie, 6 Termine"]),
            D(2023, 10, 5), PersonalDocument, [Health],
            "Doctor letter: a medical record about the person, not correspondence of the person (version 2; was null).");
        b.File("Dokumente/Gesundheit/Befund_Blutbild.pdf", MiniPdf.Text("Laborbefund", [
            "Labor Beispielstadt", "Haemoglobin 14,2 g/dl (Referenz 13,5-17,5)", "Leukozyten 6,1 /nl (Referenz 4,0-10,0)"]),
            D(2023, 10, 2), PersonalDocument, [Health], "Lab finding: a medical finding is a personal document (version 2; was null).");
        b.Dir("Dokumente/Persoenliches", Uniform, "Personal papers.");
        b.File("Dokumente/Persoenliches/Lebenslauf_2023.pdf", MiniPdf.Text("Lebenslauf", [
            "Max Beispiel, geboren 1990 in Beispielstadt", "2016-2023 Sachbearbeiter, Musterfirma GmbH",
            "2010-2016 Studium der Betriebswirtschaft", "Sprachen: Deutsch, Englisch"]),
            D(2023, 8, 30), PersonalDocument, None);
        b.File("Dokumente/Persoenliches/Mietvertrag_Wohnung.pdf", MiniPdf.Text("Mietvertrag ueber Wohnraum", [
            "Zwischen Vermieterin Erika Muster und Mieter Max Beispiel", "Wohnung Musterweg 2, 2. OG links",
            "Kaltmiete 650,00 EUR, Nebenkosten 130,00 EUR", "Mietbeginn 01.04.2019"]),
            D(2019, 3, 12), PersonalDocument, None, "A contract is a personal document, not a letter.");
        b.File("Dokumente/Persoenliches/Personalausweis_Scan.pdf", MiniPdf.Scan(IdCardScan(b)), D(2021, 1, 18), DocumentScan, [Identity],
            "Scan of an identity card: a PDF of a page image is a document scan, whatever the paper says (version 2; was null).");
        b.Dir("Dokumente/Briefe", Uniform, "Letters.");
        b.File("Dokumente/Briefe/Kuendigung_Handyvertrag.txt", Text(
            "Max Beispiel\nMusterweg 2\n12345 Beispielstadt\n\nMobilfunk Beispiel AG\n\nKuendigung Vertrag 0170-000000\n\n" +
            "Sehr geehrte Damen und Herren,\nich kuendige den genannten Vertrag fristgerecht zum Ende der Laufzeit.\n\nMit freundlichen Gruessen\nMax Beispiel\n"),
            D(2022, 9, 1), Letter, None);
        b.File("Dokumente/Briefe/Widerspruch_Bescheid.txt", Text(
            "Max Beispiel\nMusterweg 2\n12345 Beispielstadt\n\nStadtverwaltung Beispielstadt\n\nWiderspruch gegen den Bescheid vom 03.05.2022\n\n" +
            "Sehr geehrte Damen und Herren,\nhiermit lege ich Widerspruch gegen den oben genannten Bescheid ein. Begruendung folgt.\n\nMit freundlichen Gruessen\nMax Beispiel\n"),
            D(2022, 5, 20), Letter, None);
        b.File("Dokumente/Briefe/Brief_an_Oma.txt", Text(
            "Liebe Oma,\n\nwir denken oft an dich. Der Kleine kann jetzt schon laufen und lacht den ganzen Tag.\n" +
            "Im Sommer kommen wir dich besuchen, versprochen. Pass auf dich auf!\n\nDein Max\n"),
            D(2021, 12, 18), Letter, [PrivateMessage], "Private letter; only the model can mark it.");

        // ── Projects: software, recognised by markers ──
        b.Dir("Projekte", Uniform, "Only units below: a collection of projects.");
        b.Dir("Projekte/haushaltsbuch-app", Unit, "Software project (package.json).");
        b.File("Projekte/haushaltsbuch-app/package.json", Text("{\n  \"name\": \"haushaltsbuch-app\",\n  \"version\": \"0.3.0\",\n  \"main\": \"src/index.js\"\n}\n"),
            D(2024, 3, 3), null, None);
        b.File("Projekte/haushaltsbuch-app/README.md", Text("# Haushaltsbuch\n\nKleine App fuer Einnahmen und Ausgaben.\n"), D(2024, 3, 3), null, None,
            "Inside a unit: no category.");
        b.File("Projekte/haushaltsbuch-app/.env", Text("API_" + "TOKEN=" + "dummy-" + Hex(b, 24) + "\nPORT=8080\n"), D(2024, 3, 5), null, [Credentials],
            "Environment file with a dummy token.");
        b.Dir("Projekte/haushaltsbuch-app/src", Unit, "Inside a unit.");
        b.File("Projekte/haushaltsbuch-app/src/index.js", Text("import { summe } from './budget.js';\nconsole.log(summe([12, 30]));\n"), D(2024, 3, 4), null, None);
        b.File("Projekte/haushaltsbuch-app/src/budget.js", Text("export const summe = (werte) => werte.reduce((a, b) => a + b, 0);\n"), D(2024, 3, 4), null, None);
        b.Dir("Projekte/haushaltsbuch-app/node_modules", Unit, "Dependencies inside a unit; skipped by the scan.");
        b.File("Projekte/haushaltsbuch-app/node_modules/.package-lock.json", Text("{\n  \"lockfileVersion\": 3\n}\n"), D(2024, 3, 3), null, None);
        b.Dir("Projekte/haushaltsbuch-app/.git", Unit, "Version control inside a unit; skipped by the scan.");
        b.File("Projekte/haushaltsbuch-app/.git/HEAD", Text("ref: refs/heads/main\n"), D(2024, 3, 5), null, None);
        b.Dir("Projekte/vereinswebsite", Unit, "Website project under version control (.git).");
        b.File("Projekte/vereinswebsite/index.html", Text("<!doctype html>\n<title>Kleingartenverein Sonnenblick</title>\n<h1>Willkommen</h1>\n"), D(2023, 4, 9), null, None);
        b.File("Projekte/vereinswebsite/style.css", Text("body { font-family: sans-serif; }\n"), D(2023, 4, 9), null, None);
        b.Dir("Projekte/vereinswebsite/bilder", Unit, "Inside a unit.");
        b.File("Projekte/vereinswebsite/bilder/logo.png", Logo(b), D(2023, 4, 8), null, None, "Inside a unit: no category.");
        b.Dir("Projekte/vereinswebsite/.git", Unit, "Version control inside a unit; skipped by the scan.");
        b.File("Projekte/vereinswebsite/.git/HEAD", Text("ref: refs/heads/main\n"), D(2023, 4, 9), null, None);

        // ── A phone dump: recognised as a whole, its camera judged, its app data skipped (version 3) ──
        b.Dir("Handy-Backup 2021", Dump, "Copy of a phone. Recognised as a whole, but not a unit: its camera roll is judged.");
        b.Dir("Handy-Backup 2021/DCIM", Uniform, "Worth-keeping part of the dump (camera); only a photo collection below.");
        b.Dir("Handy-Backup 2021/DCIM/Camera", Uniform, "Camera roll of the phone: the photos the dump is kept for.");
        for (var i = 0; i < 5; i++)
            b.File($"Handy-Backup 2021/DCIM/Camera/IMG_20210{3 + i}14_{120000 + i * 1111}.jpg", PhotoOf(b, Scene.Garden),
                D(2021, 3 + i, 14, 12, i), Photo, None, "Camera photo inside a dump: judged like any photo (version 3; was inside a unit).");
        b.Dir("Handy-Backup 2021/LOST.DIR", Empty, "Empty system folder of the phone.");
        b.Dir("Handy-Backup 2021/Android", SystemDir, "Only app data below.");
        b.Dir("Handy-Backup 2021/Android/data", SystemDir, "Noise of the dump (app data); skipped without a model call.");
        b.Dir("Handy-Backup 2021/Android/data/com.example.notes", SystemDir, "App data folder named after an app package; noise.");
        b.Dir("Handy-Backup 2021/Android/data/com.example.notes/files", SystemDir, "Noise of the dump.");
        b.File("Handy-Backup 2021/Android/data/com.example.notes/files/notes.db", Binary(b, Encoding.ASCII.GetBytes("SQLite format 3\0"), 4096),
            D(2021, 7, 30), null, None, "App data: skipped, no category.");
        b.File("Handy-Backup 2021/Android/data/com.example.notes/files/settings.xml", Text("<settings><theme>dark</theme></settings>\n"),
            D(2021, 7, 30), null, None, "App data: skipped, no category.");

        UserProfiles(b);

        // ── Programs and a game ──
        b.Dir("Programme", Uniform, "Only units below: a collection of programs.");
        b.Dir("Programme/PortableTool", Unit, "Portable program (exe next to dll).");
        b.File("Programme/PortableTool/tool.exe", Exe(b), D(2020, 2, 2), null, None);
        b.File("Programme/PortableTool/tool.dll", Exe(b), D(2020, 2, 2), null, None);
        b.File("Programme/PortableTool/liesmich.txt", Text("PortableTool 1.4\nStarten mit tool.exe.\n"), D(2020, 2, 2), null, None);
        b.Dir("Programme/MiniGolf", Unit, "Game (exe next to its _Data folder).");
        b.File("Programme/MiniGolf/MiniGolf.exe", Exe(b), D(2021, 12, 24), null, None);
        b.File("Programme/MiniGolf/UnityPlayer.dll", Exe(b), D(2021, 12, 24), null, None);
        b.Dir("Programme/MiniGolf/MiniGolf_Data", Unit, "Inside the game.");
        b.File("Programme/MiniGolf/MiniGolf_Data/level0", Binary(b, [0x00, 0x00, 0x00, 0x16], 3000), D(2021, 12, 24), null, None);
        b.File("Programme/MiniGolf/MiniGolf_Data/globalgamemanagers", Binary(b, [0x00, 0x00, 0x00, 0x16], 2000), D(2021, 12, 24), null, None);

        // ── Media ──
        b.Dir("Musik", Uniform, "Music files; audio is not classified.");
        string[] songs = ["01 - Sommerregen.mp3", "02 - Am Hafen.mp3", "03 - Leise Nacht.mp3", "04 - Rueckweg.mp3"];
        foreach (var song in songs)
            b.File($"Musik/{song}", Mp3(b), D(2017, 8, 1), null, None);
        b.Dir("Videos", Uniform, "Family videos.");
        b.File("Videos/Geburtstag_Opa_2018.mp4", Mp4(b, 64_000), D(2018, 10, 7), Video, None);
        b.File("Videos/Schulauffuehrung_2019.mp4", Mp4(b, 64_000), D(2019, 6, 28), Video, None);
        b.File("Videos/VID_20200101_000512.mp4", Mp4(b, 64_000), D(2020, 1, 1), Video, None);
        b.Dir("Screenshots", Uniform, "Screenshots only.");
        for (var i = 0; i < 4; i++)
            b.File($"Screenshots/Screenshot_2023-0{i + 2}-1{i}_09{10 + i}00.png", Screen(b, 2 + i), D(2023, 2 + i, 10 + i), Screenshot, None);

        // ── Leftovers under a placeholder name ──
        b.Dir("alt", Mixed, "Placeholder name; unrelated leftovers.");
        b.File("alt/scan001.pdf", MiniPdf.Scan(LetterScan(b)), D(2016, 11, 2), DocumentScan, None, "Scanned page of a letter.");
        b.File("alt/bewerbungsfoto.jpg", PhotoOf(b, Scene.Portrait), D(2016, 3, 1), Photo, None);
        b.File("alt/Tabelle1.csv", Text("Monat;Betrag\nJanuar;120\nFebruar;95\nMaerz;143\n"), D(2016, 4, 4), null, None, "Category ambiguous.");

        Import(b);

        // ── Hidden key folder ──
        b.Dir(".ssh", SystemDir, "Hidden tool folder.");
        b.File(".ssh/id_ed25519", PrivateKey(b), D(2022, 1, 9), null, [Keys], "Private key dummy (header and random filler).");
        b.File(".ssh/id_ed25519.pub", Text("ssh-ed25519 " + Convert.ToBase64String(Bytes(b, 32)) + " max@laptop\n"), D(2022, 1, 9), null, None,
            "A public key is not secret.");
    }

    // Descriptions (Issue 37) of the version 5 pictures and papers: the stub's answer for a source file and for a library file.
    private static readonly TruthDescription SkiDay = new("snowy slope", "skiing", "outdoor", "winter", "holiday", true);
    private static readonly TruthDescription BirthdayEvening = new("living room with cake", "blowing out candles", "indoor", "winter", "birthday", true);
    private static readonly TruthDescription LakeDay = new("lake shore", "swimming", "outdoor", "summer", "outing", true);
    private static readonly TruthDescription GardenParty = new("garden party", "eating together", "outdoor", "summer", "celebration", true);
    private static readonly TruthDescription EasterGarden = new("garden lawn", "egg hunt", "outdoor", "spring", "easter", true);
    private static TruthDescription InvoiceOf(string issuer, int year) => new(DocumentKind: "invoice", Issuer: issuer, Year: year);
    private const string PowerCompany = "Stadtwerke Beispielstadt", PhoneCompany = "Telefon Beispiel AG";

    /// <summary>
    /// Version 5 (Issue 56): a folder under a generic name with what has no place yet. Two occasions of the same day (a
    /// ski trip in the morning, a birthday in the evening, more than eight hours apart, so two bundles), a lake outing on
    /// the day of a garden party, and two invoices of two providers. <c>targets.json</c> says where each belongs in the
    /// library of the corpus (<see cref="Library"/>).
    /// </summary>
    private static void Import(Builder b)
    {
        b.Dir("Import", Mixed, "Generic name; photos of three occasions, two invoices and own pictures and clips (version 6) waiting for a place.");
        // The name carries the time (the grouping reads it), so it must be a valid one.
        static string Named(DateTime t) => $"Import/IMG_{t:yyyyMMdd_HHmmss}.jpg";
        for (var i = 0; i < 3; i++)
        {
            var t = D(2021, 2, 6, 9, 0).AddMinutes(i * 40).AddSeconds(12);
            b.File(Named(t), PhotoOf(b, Scene.Snow), t, Photo, None, "Ski trip in the morning: belongs into the ski trip of the library.", SkiDay);
        }
        for (var i = 0; i < 3; i++)
        {
            var t = D(2021, 2, 6, 19, 30).AddMinutes(i * 25).AddSeconds(15);
            b.File(Named(t), PhotoOf(b, Scene.Portrait), t, Photo, None,
                "Birthday in the evening of the same day: same period as the ski trip, other occasion; no folder for it yet.", BirthdayEvening);
        }
        for (var i = 0; i < 3; i++)
            b.File(Named(D(2021, 8, 14, 11, i * 15)), PhotoOf(b, Scene.Lake), D(2021, 8, 14, 11, i * 15), Photo, None,
                "Lake outing on the day of the garden party of the library: same day, other occasion; no folder for it yet.", LakeDay);
        b.File("Import/Rechnung_2023_11.pdf", MiniPdf.Text("Rechnung Nr. 2023-1120", [
            PowerCompany + " GmbH, Musterweg 1, 12345 Beispielstadt",
            "Stromlieferung November 2023: 198 kWh",
            "Rechnungsbetrag: 76,10 EUR"]), D(2023, 11, 20), Invoice, None, "Belongs to the invoices of its provider.", InvoiceOf(PowerCompany, 2023));
        b.File("Import/Rechnung_2023_12.pdf", MiniPdf.Text("Rechnung Dezember 2023", [
            PhoneCompany + ", Postfach 10, 54321 Nachbarort",
            "Festnetz und Internet Dezember 2023",
            "Rechnungsbetrag: 34,99 EUR"]), D(2023, 12, 20), Invoice, None, "Belongs to the invoices of its provider.", InvoiceOf(PhoneCompany, 2023));

        // Version 6 (Issue 85): own pictures and clips at the time of closed units of the library (a film folder, the
        // cache of a game, an episode folder, a program); none of them is a target.
        for (var i = 1; i <= 2; i++)
            b.File($"Import/Sommer am Teich {i}.jpg", PhotoOf(b, Scene.Lake), D(2022, 7, 10, 20, i * 3), Photo, None,
                "Own picture with an everyday word of a film title in its name; no folder for it yet.", PondEvening);
        for (var i = 0; i < 2; i++)
            b.File($"Import/IMG_20220305_18{15 + i * 5}00.jpg", PhotoOf(b, Scene.Garden), D(2022, 3, 5, 18, 15 + i * 5), Photo, None,
                "Taken while a game wrote its cache; no folder for it yet.", GardenDay);
        for (var i = 0; i < 2; i++)
            b.File($"Import/VID_20200510_21{15 + i * 5}00.mp4", Mp4(b, 64_000), D(2020, 5, 10, 21, 15 + i * 5), Video, None,
                "Own clip of the evening an episode was saved; no folder for it yet.", EveningClip);
        for (var i = 0; i < 2; i++)
            b.File($"Import/IMG_20180901_12{05 + i * 5:00}00.jpg", PhotoOf(b, Scene.Coast), D(2018, 9, 1, 12, 5 + i * 5), Photo, None,
                "Taken the day a program was installed; no folder for it yet.", CoastDay);
    }

    // Version 6 (Issue 85): what the traps and the closed units of the library show.
    private static readonly TruthDescription PondEvening = new("pond at dusk", "walking", "outdoor", "summer", "outing", true);
    private static readonly TruthDescription GardenDay = new("garden lawn", "gardening", "outdoor", "spring", null, true);
    private static readonly TruthDescription CoastDay = new("rocky coast", "hiking", "outdoor", "summer", "holiday", true);
    private static readonly TruthDescription FilmPoster = new("film poster", null, "indoor", null, null, false);
    private static readonly TruthDescription GameTexture = new("game texture", null, null, null, null, false);
    private static readonly TruthDescription SamplePicture = new("sample picture", null, "outdoor", null, null, false);
    private static readonly TruthDescription NotDescribed = new();
    private static readonly TruthDescription EveningClip = new("living room", "dancing", "indoor", "spring", "party", true);

    /// <summary>
    /// Version 5 (Issue 56): the library of the corpus, a folder next to <c>tree</c> (never inside it), and
    /// <c>targets.json</c> with what every file there shows and where the waiting things of <c>Import</c> belong.
    /// </summary>
    private static void Library(string dir, Random random)
    {
        var b = new Builder(Path.Combine(dir, LibraryFolder), random);
        var files = new List<LibraryFileEntry>();
        void Photos(string folder, string name, Scene scene, DateTime first, TruthDescription description)
        {
            b.Dir(folder, null);
            for (var i = 0; i < 4; i++)
            {
                var when = first.AddMinutes(i * 95);
                var path = $"{folder}/IMG_{when:yyyyMMdd_HHmmss}.jpg";
                b.File(path, PhotoOf(b, scene), when, null, None);
                files.Add(new LibraryFileEntry(path, description, name));
            }
        }
        void Invoices(string folder, string issuer)
        {
            b.Dir(folder, null);
            for (var m = 8; m <= 10; m++)
            {
                var path = $"{folder}/Rechnung_2023_{m:00}.pdf";
                b.File(path, MiniPdf.Text($"Rechnung {m:00}/2023", [issuer, $"Rechnungsbetrag: {40 + m},00 EUR"]), D(2023, m, 20), null, None);
                files.Add(new LibraryFileEntry(path, InvoiceOf(issuer, 2023)));
            }
        }

        b.Dir(".", null);
        b.Dir("Bilder", null);
        Photos("Bilder/Skifahrt 2021", "ski trip", Scene.Snow, D(2021, 2, 5, 10, 0), SkiDay);
        Photos("Bilder/Sommerfest 2021", "garden party", Scene.Garden, D(2021, 8, 14, 15, 0), GardenParty);
        Photos("Bilder/Ostern 2020", "easter", Scene.Garden, D(2020, 4, 12, 11, 0), EasterGarden);
        b.Dir("Unterlagen", null);
        Invoices("Unterlagen/Rechnungen Stadtwerke", PowerCompany);
        Invoices("Unterlagen/Rechnungen Telefon", PhoneCompany);

        // Version 6 (Issue 85): closed units, which take no loose files of another kind: a film folder under an
        // everyday word, the folder of a season, the cache of a game and a program with sample pictures.
        void Described(string path, byte[] content, DateTime when, TruthDescription description, string note)
        {
            b.File(path, content, when, null, None);
            files.Add(new LibraryFileEntry(path, description, note));
        }
        b.Dir("Filme", null);
        foreach (var (title, when) in new[] { ("Sommer am See (2009)", D(2022, 7, 10, 20, 0)), ("Ein anderer Film (2011)", D(2022, 7, 11, 20, 0)) })
        {
            b.Dir($"Filme/{title}", null);
            Described($"Filme/{title}/{title}.mp4", Mp4(b, 96_000), when, NotDescribed, "film");
            Described($"Filme/{title}/poster.jpg", PhotoOf(b, Scene.Lake), when.AddMinutes(1), FilmPoster, "film poster");
            Described($"Filme/{title}/fanart.jpg", PhotoOf(b, Scene.Lake), when.AddMinutes(2), FilmPoster, "film poster");
        }
        b.Dir("Serien", null);
        b.Dir("Serien/Beispielserie", null);
        b.Dir("Serien/Beispielserie/Staffel 1", null);
        for (var e = 1; e <= 2; e++)
            Described($"Serien/Beispielserie/Staffel 1/Beispielserie S01E0{e}.mp4", Mp4(b, 80_000), D(2020, 5, 10, 21, e * 10), NotDescribed, "episode");
        b.Dir("Spiele", null);
        b.Dir("Spiele/Beispielspiel", null);
        Described("Spiele/Beispielspiel/spiel.exe", Exe(b), D(2022, 3, 5, 18, 0), NotDescribed, "game");
        b.Dir("Spiele/Beispielspiel/cache", null);
        for (var i = 0; i < 3; i++)
            Described($"Spiele/Beispielspiel/cache/{i:000}.jpg", PhotoOf(b, Scene.Garden), D(2022, 3, 5, 18, 10 + i), GameTexture, "game cache");
        b.Dir("Programme", null);
        b.Dir("Programme/Bildbetrachter", null);
        Described("Programme/Bildbetrachter/betrachter.exe", Exe(b), D(2018, 9, 1, 12, 0), NotDescribed, "program");
        Described("Programme/Bildbetrachter/betrachter.dll", Exe(b), D(2018, 9, 1, 12, 0), NotDescribed, "program");
        for (var i = 1; i <= 2; i++)
            Described($"Programme/Bildbetrachter/beispiel{i}.jpg", PhotoOf(b, Scene.Coast), D(2018, 9, 1, 12, i), SamplePicture, "sample picture of a program");
        b.ApplyDirectoryTimes();

        ExpectedTarget[] targets =
        [
            new("bundle", "Import/IMG_20210206_090012.jpg", "Bilder/Skifahrt 2021", "Same occasion as the ski trip folder."),
            new("bundle", "Import/IMG_20210206_193015.jpg", null,
                "Trap: same week as the ski trip, other occasion; the ski trip folder is wrong, a new folder is right."),
            new("bundle", "Import/IMG_20210814_110000.jpg", null,
                "Trap: same day as the garden party, other occasion; no folder for it yet."),
            new("file", "Import/Rechnung_2023_11.pdf", "Unterlagen/Rechnungen Stadtwerke", "The invoices of its provider, not of the other."),
            new("file", "Import/Rechnung_2023_12.pdf", "Unterlagen/Rechnungen Telefon", "The invoices of its provider, not of the other."),
            new("file", "Import/Sommer am Teich 1.jpg", null,
                "Trap (Issue 85): one everyday word and the time in common with a film folder; a film is a closed unit."),
            new("bundle", "Import/IMG_20220305_181500.jpg", null, "Trap (Issue 85): same time as the cache of a game; a cache takes no photos."),
            new("bundle", "Import/VID_20200510_211500.mp4", null, "Trap (Issue 85): same evening as the folder of a season; an episode folder is closed."),
            new("bundle", "Import/IMG_20180901_120500.jpg", null, "Trap (Issue 85): same time as the sample pictures of a program; a program is closed."),
            new("folder", "Import", null, "Trap (Issue 85): a library that is gone is a named failure, not a silent answer.",
                EvalTargetTruth.LibraryUnreachable)
        ];
        var doc = new EvalTargetTruthDocument(EvalTargetTruth.SchemaVersion, LibraryFolder, files, targets);
        File.WriteAllText(Path.Combine(dir, EvalTargetTruth.FileName), JsonSerializer.Serialize(doc, EvalJson.Options) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Version 3 (https://github.com/crack666/smart_file_organizer/issues/59): the profiles folder of an old
    /// computer with two profiles. Each profile is a dump of its own; its worth-keeping parts (desktop,
    /// documents, downloads, pictures) are judged like any folder, its app data and caches are skipped.
    /// <c>max</c> is a newer profile whose <c>AppData</c> the default scan rules skip; <c>lisa</c> is an older
    /// German one whose <c>Anwendungsdaten</c> and <c>Lokale Einstellungen</c> the scan lists, with most of
    /// the files of the two profiles.
    /// </summary>
    private static void UserProfiles(Builder b)
    {
        b.Dir("Profile alter PC", Uniform, "Only dumps below: the copies of two user profiles.");

        // ── max: newer profile ──
        const string max = "Profile alter PC/max";
        b.Dir(max, Dump, "Copy of a user profile (NTUSER.DAT, AppData). Recognised as a whole; its parts are judged on their own.");
        b.File($"{max}/NTUSER.DAT", Binary(b, Encoding.ASCII.GetBytes("regf"), 8192), D(2015, 9, 30), null, None,
            "Registry hive in the profile root. Files directly in a dump are judged one by one; no document type.");
        b.File($"{max}/ntuser.ini", Text("[.ShellClassInfo]\nLocalizedResourceName=@%SystemRoot%\\system32\\shell32.dll,-21813\n"),
            D(2015, 9, 30), null, None, "Settings file in the profile root; no document type.");
        b.Dir($"{max}/AppData", SystemDir, "App data: skipped by the default scan rules before any verdict; noise of the dump.");
        b.Dir($"{max}/AppData/Local", null, "Inside a folder the scan skips: never listed.");
        b.Dir($"{max}/AppData/Local/Temp", null, "Inside a folder the scan skips: never listed.");

        b.Dir($"{max}/Desktop", Mixed, "Desktop of the profile: worth-keeping part, things put down for later.");
        b.File($"{max}/Desktop/Zugangsdaten_WLAN.txt", Text("WLAN: Beispielnetz\n" + Filler(b, "Schluessel ", 2)), D(2014, 5, 3), null, [Credentials],
            "The sensitive file of the profile; protected inside a dump like everywhere. Category ambiguous.");
        b.File($"{max}/Desktop/Notizen.txt", Text("Reifenwechsel im Oktober\nGeschenk fuer Oma besorgen\n"), D(2015, 9, 2), null, None,
            "Category ambiguous (document, review_needed, trash_candidate).");
        b.File($"{max}/Desktop/Screenshot_2015-08-21_193012.png", Screen(b, 6), D(2015, 8, 21), Screenshot, None);

        b.Dir($"{max}/Documents", Mixed, "Documents of the profile: a contract and a letter without a common purpose.");
        b.File($"{max}/Documents/Mietvertrag_Wohnung.pdf", MiniPdf.Text("Mietvertrag", [
            "Zwischen Vermieterin Erika Beispiel und Mieter Max Beispiel",
            "Wohnung: Musterweg 2, 12345 Beispielstadt, 2. OG links",
            "Mietbeginn: 01.04.2013",
            "Kaltmiete: 520,00 EUR, Nebenkosten: 140,00 EUR",
            "Kaution: drei Kaltmieten"]), D(2013, 3, 20), PersonalDocument, None,
            "A rental contract is a personal document, not finance.");
        b.File($"{max}/Documents/Kuendigung_Zeitung.txt", Text(
            "Max Beispiel\nMusterweg 2\n12345 Beispielstadt\n\nBeispielstadt Kurier, Leserservice\n\nKuendigung meines Abonnements\n\n" +
            "Sehr geehrte Damen und Herren,\nhiermit kuendige ich mein Abonnement (Nr. 77821) zum Monatsende.\n\n" +
            "Mit freundlichen Gruessen\nMax Beispiel\n"), D(2014, 11, 5), Letter, None, "Formal letter to a company.");
        b.Dir($"{max}/Documents/Steuer 2014", Uniform, "Tax papers of one year inside the profile.");
        b.File($"{max}/Documents/Steuer 2014/Bescheid_Finanzamt_2014.pdf", MiniPdf.Text("Bescheid fuer 2014 ueber Einkommensteuer", [
            "Finanzamt Beispielstadt, Steuernummer 12/345/67890",
            "Festgesetzte Einkommensteuer: 4.212,00 EUR",
            "Bereits gezahlt: 4.530,00 EUR",
            "Erstattung: 318,00 EUR"]), D(2015, 6, 18), PersonalDocument, [Finance],
            "Tax assessment; no name rule knows it, the model must mark it.");

        b.Dir($"{max}/Downloads", Mixed, "Downloads of the profile.");
        b.File($"{max}/Downloads/setup_druckertreiber.exe", Exe(b), D(2014, 1, 9), null, None, "Installer; not classified (executable).");
        b.File($"{max}/Downloads/Fahrplan_Linie_7.pdf", MiniPdf.Text("Fahrplan Linie 7", [
            "Bahnhof ab 06:12, 06:42, 07:12",
            "Rathaus ab 06:20, 06:50, 07:20",
            "Gueltig ab 14.12.2014"]), D(2014, 12, 10), Document, None);

        b.Dir($"{max}/Pictures", Uniform, "Photos of the profile.");
        for (var i = 0; i < 3; i++)
            b.File($"{max}/Pictures/IMG_2015071{i + 1}_1{i}3000.jpg", PhotoOf(b, i == 1 ? Scene.Lake : Scene.Garden),
                D(2015, 7, 11 + i, 10 + i), Photo, None);

        // Version 4 (issue 67): what the rules decide without the model, and an archive known only by its entries.
        b.File($"{max}/Documents/desktop.ini", Text("[.ShellClassInfo]\nLocalizedResourceName=@%SystemRoot%\\system32\\shell32.dll,-21770\n"),
            D(2013, 1, 5), null, None, "Folder settings of Windows: a system file by name, no model call; no document type.");
        b.File($"{max}/Documents/~$etvertrag_Wohnung.docx", Binary(b, [0x0B, 0x4D, 0x61, 0x78], 162), D(2015, 9, 29), null, None,
            "Lock file Office leaves next to an open document: a system file by name, no model call.");
        b.File($"{max}/Documents/20190312.zip", ZipOf(D(2019, 3, 12),
                ("Kontoauszug_2019_01.pdf", 2400), ("Kontoauszug_2019_02.pdf", 2300), ("Kreditkartenabrechnung_2019_02.pdf", 1900)),
            D(2019, 3, 12), null, [Finance],
            "Bank and card statements packed under a number: the entry names mark it finance by rule. Category open "
            + "(archive or personal_document); the archive is worth keeping.");
        b.Dir($"{max}/Documents/Garantie", Uniform, "One file only: the folder follows its file, no model call for the folder.");
        b.File($"{max}/Documents/Garantie/Garantie_Waschmaschine.pdf", MiniPdf.Text("Garantiekarte", [
            "Waschmaschine WM 7, Seriennummer 7781-22",
            "Kaufdatum: 03.05.2014, Garantie 2 Jahre",
            "Elektro Beispiel, Marktplatz 3, 12345 Beispielstadt"]), D(2014, 5, 3), Document, None);
        b.Dir($"{max}/scoop", SystemDir, "Package manager in the profile: only system folders below.");
        b.Dir($"{max}/scoop/apps", SystemDir, "Programs installed by scoop: rule system-place, no model call.");
        b.Dir($"{max}/scoop/apps/7zip", SystemDir, "One package: below a system place.");
        b.File($"{max}/scoop/apps/7zip/install.json", Text("{\"bucket\":\"main\",\"architecture\":\"64bit\"}\n"), D(2015, 2, 1), null, None,
            "Package file: skipped, no category.");
        b.File($"{max}/scoop/apps/7zip/7z.exe", Exe(b), D(2015, 2, 1), null, None, "Program of a package: skipped, no category.");

        // ── lisa: older German profile, most of the noise ──
        const string lisa = "Profile alter PC/lisa";
        b.Dir(lisa, Dump, "Copy of an older user profile (NTUSER.DAT, Anwendungsdaten, Lokale Einstellungen).");
        b.File($"{lisa}/NTUSER.DAT", Binary(b, Encoding.ASCII.GetBytes("regf"), 8192), D(2009, 12, 1), null, None,
            "Registry hive in the profile root; no document type.");

        b.Dir($"{lisa}/Anwendungsdaten", SystemDir, "App data of the old profile: noise, skipped without a model call.");
        b.Dir($"{lisa}/Anwendungsdaten/Mozilla", SystemDir, "Browser data: noise.");
        foreach (var name in new[] { "prefs.js", "places.sqlite", "formhistory.sqlite" })
            b.File($"{lisa}/Anwendungsdaten/Mozilla/{name}", Binary(b, Encoding.ASCII.GetBytes("SQLite format 3\0"), 2048),
                D(2009, 11, 28), null, None, "Browser data: skipped, no category.");
        b.File($"{lisa}/Anwendungsdaten/Mozilla/logins.json", Text("{\"logins\":[{\"hostname\":\"https://example.org\",\"encryptedPassword\":\""
            + Hex(b, 32) + "\"}]}\n"), D(2009, 11, 28), null, [Credentials],
            "Saved logins in the noise: skipped, but the name rule still marks them, so the output guard withholds them.");

        b.Dir($"{lisa}/Lokale Einstellungen", SystemDir, "Local settings and caches of the old profile: noise.");
        b.Dir($"{lisa}/Lokale Einstellungen/Temp", SystemDir, "Temporary files: noise.");
        for (var i = 0; i < 15; i++)
            b.File($"{lisa}/Lokale Einstellungen/Temp/~DF{4100 + i * 37:X4}.tmp", Bytes(b, 512 + i * 64), D(2009, 10, 1 + i), null, None,
                "Temporary file: skipped, no category.");
        b.Dir($"{lisa}/Lokale Einstellungen/Temporary Internet Files", SystemDir, "Browser cache: noise.");
        for (var i = 0; i < 15; i++)
        {
            var name = i % 3 == 0 ? $"logo[{i}].gif" : i % 3 == 1 ? $"style[{i}].css" : $"index[{i}].htm";
            var content = i % 3 == 0 ? Binary(b, Encoding.ASCII.GetBytes("GIF89a"), 300) : Text($"/* cached {i} */\n");
            b.File($"{lisa}/Lokale Einstellungen/Temporary Internet Files/{name}", content, D(2009, 11, 1 + i), null, None,
                "Browser cache: skipped, no category.");
        }

        b.Dir($"{lisa}/Cookies", SystemDir,
            "Browser cookies. No noise path names it, so the model judges it (one call) instead of a rule skipping it.");
        for (var i = 0; i < 5; i++)
            b.File($"{lisa}/Cookies/lisa@example{i + 1}[1].txt", Text($"session\n{Hex(b, 16)}\nexample{i + 1}.org/\n"), D(2009, 11, 2 + i),
                null, None, "Cookie file; no category.");

        b.Dir($"{lisa}/Desktop", Mixed, "Desktop of the old profile.");
        b.File($"{lisa}/Desktop/Einladung_Sommerfest.txt", Text("Einladung zum Sommerfest am 18. Juli 2009 im Garten.\nBitte Salat mitbringen.\n"),
            D(2009, 6, 30), Document, None);
        b.File($"{lisa}/Desktop/Urlaubsfoto_Ostsee.jpg", PhotoOf(b, Scene.Lake), D(2009, 8, 14), Photo, None);

        b.Dir($"{lisa}/Eigene Dateien", Mixed, "Documents of the old profile: a CV and a recipe.");
        b.File($"{lisa}/Eigene Dateien/Lebenslauf_2009.txt", Text(
            "Lebenslauf\n\nLisa Muster\nAm Bach 7, 54321 Nachbarort\n\n2001-2004 Ausbildung zur Buchhaendlerin\n" +
            "2004-2009 Buchhandlung am Markt, Nachbarort\n"), D(2009, 3, 2), PersonalDocument, None, "A CV is a personal document.");
        b.File($"{lisa}/Eigene Dateien/Rezept_Apfelkuchen.txt", Text("Apfelkuchen\n\n4 Aepfel\n200 g Mehl\n150 g Butter\n100 g Zucker\n"),
            D(2008, 10, 4), Document, None);
        b.Dir($"{lisa}/Eigene Dateien/Eigene Bilder", Uniform, "Photos of the old profile (part pictures inside part documents).");
        for (var i = 0; i < 3; i++)
            b.File($"{lisa}/Eigene Dateien/Eigene Bilder/DSC0{1201 + i}.JPG", PhotoOf(b, Scene.Portrait), D(2008, 12, 24, 17, i * 7), Photo, None);

        // ── Public: the profile of all users (version 4, issue 67), only what programs and Windows put there ──
        const string pub = "Profile alter PC/Public";
        b.Dir(pub, SystemDir, "Profile of all users without own content: only system places below. No dump (no NTUSER.DAT, no AppData).");
        b.Dir($"{pub}/Documents", SystemDir, "Public documents holding only what an application installed.");
        b.Dir($"{pub}/Documents/Fotostudio Pro", SystemDir, "Folder of an application: only its learning material and samples.");
        b.Dir($"{pub}/Documents/Fotostudio Pro/Tutorials", SystemDir,
            "Learning videos of the application: rule system-place, no model call (they were what two models disagreed on).");
        for (var i = 0; i < 2; i++)
            b.File($"{pub}/Documents/Fotostudio Pro/Tutorials/Lektion_0{i + 1}_Ebenen.mp4", Mp4(b, 6000 + i * 500), D(2012, 4, 2), null, None,
                "Learning video of an application: skipped, no category.");
        b.Dir($"{pub}/Documents/Fotostudio Pro/Samples", SystemDir, "Sample pictures of the application: rule system-place.");
        for (var i = 0; i < 2; i++)
            b.File($"{pub}/Documents/Fotostudio Pro/Samples/Beispiel_{i + 1}.jpg", PhotoOf(b, i == 0 ? Scene.Lake : Scene.Garden), D(2012, 4, 2),
                null, None, "Sample picture of an application: skipped, no category. Not a photo of the person.");
        b.Dir($"{pub}/AccountPictures", SystemDir, "Account pictures of Windows: rule system-place; not worth keeping.");
        foreach (var size in new[] { 40, 192 })
            b.File($"{pub}/AccountPictures/user-{size}.png", Screen(b, size == 40 ? 1 : 2), D(2012, 3, 1), null, None,
                "Account picture: skipped, no category.");
        b.Dir($"{pub}/Desktop", SystemDir, "Desktop of all users: shortcuts the installers put there; rule system-place.");
        foreach (var name in new[] { "Fotostudio Pro.lnk", "Browser.lnk" })
            b.File($"{pub}/Desktop/{name}", Binary(b, [0x4C, 0x00, 0x00, 0x00], 420), D(2012, 4, 2), null, None,
                "Shortcut of an installer: skipped, no category.");
    }

    // ── File contents ────────────────────────────────────────────────────

    private static byte[] Text(string s) => new UTF8Encoding(false).GetBytes(s.Replace("\r\n", "\n"));

    private static byte[] Bytes(Builder b, int n)
    {
        var bytes = new byte[n];
        b.Random.NextBytes(bytes);
        return bytes;
    }

    private static string Hex(Builder b, int n) => Convert.ToHexString(Bytes(b, n / 2)).ToLowerInvariant();

    private static byte[] Binary(Builder b, byte[] header, int size)
    {
        var bytes = Bytes(b, size);
        header.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Exe(Builder b) => Binary(b, [(byte)'M', (byte)'Z', 0x90, 0x00], 1536);

    private static byte[] Mp3(Builder b) => Binary(b, Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0"), 8000);

    private static byte[] Mp4(Builder b, int size) => Binary(b, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2'], size);

    /// <summary>A ZIP archive with the given entries (zero filler, fixed time), so the same seed gives the same bytes.</summary>
    private static byte[] ZipOf(DateTime modified, params (string Name, int Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(modified, TimeSpan.Zero);
                using var s = entry.Open();
                s.Write(new byte[bytes]);
            }
        }
        return stream.ToArray();
    }

    /// <summary>An empty but valid ZIP archive (end-of-central-directory record only).</summary>
    private static byte[] EmptyZip() => [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>KeePass signature with random filler; not a readable database.</summary>
    private static byte[] Kdbx(Builder b) => Binary(b, [0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5], 1024);

    /// <summary>A DER sequence start with random filler; not a readable certificate.</summary>
    private static byte[] Pfx(Builder b) => Binary(b, [0x30, 0x82, 0x09, 0xF1, 0x02, 0x01, 0x03], 900);

    /// <summary>Key header and footer around random base64; assembled here so no key line is in the source.</summary>
    private static byte[] PrivateKey(Builder b)
    {
        var label = string.Join(' ', "OPENSSH", "PRIVATE", "KEY");
        var body = Convert.ToBase64String(Bytes(b, 300), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n");
        return Text($"-----BEGIN {label}-----\n{body}\n-----END {label}-----\n");
    }

    private static string Filler(Builder b, string prefix, int lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
            sb.Append(prefix).Append(i + 1).Append(": nutzer").Append(b.Random.Next(10, 99)).Append(" / ").Append(Hex(b, 12)).Append('\n');
        return sb.ToString();
    }

    private static string Codes(Builder b, int count)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
            sb.Append(b.Random.Next(1000, 9999)).Append('-').Append(b.Random.Next(1000, 9999)).Append('\n');
        return sb.ToString();
    }

    // ── Pictures ─────────────────────────────────────────────────────────

    private enum Scene { Snow, Garden, Wedding, Lake, Coast, Portrait }

    private static byte[] PhotoOf(Builder b, Scene scene)
    {
        var r = b.Random;
        var c = new Canvas(320, 240);
        var horizon = 120 + r.Next(-20, 21);
        switch (scene)
        {
            case Scene.Snow:
                c.VerticalGradient(0, horizon, (90, 140, 220), (190, 215, 245));
                for (var i = 0; i < 3; i++) c.Peak(r.Next(30, 290), horizon - r.Next(50, 100), horizon, r.Next(50, 90), (225, 230, 240));
                c.Fill(0, horizon, 320, 240, (245, 247, 250));
                for (var i = 0; i < 3; i++) { var x = r.Next(40, 280); var y = r.Next(horizon + 30, 215); c.Fill(x, y - 18, x + 6, y, (200, 40, 40)); c.Circle(x + 3, y - 22, 4, (230, 190, 160)); }
                break;
            case Scene.Garden:
                c.VerticalGradient(0, horizon, (110, 170, 230), (200, 225, 245));
                c.Circle(r.Next(40, 280), r.Next(20, 50), 16, (255, 230, 120));
                c.Fill(0, horizon, 320, 240, (70, 150, 60));
                for (var i = 0; i < 25; i++) c.Circle(r.Next(0, 320), r.Next(horizon, 240), 3, (r.Next(180, 255), r.Next(40, 200), r.Next(40, 120)));
                for (var i = 0; i < 4; i++) { var x = r.Next(30, 290); c.Fill(x, horizon - 30, x + 10, horizon + 20, (r.Next(40, 200), r.Next(40, 200), r.Next(40, 200))); c.Circle(x + 5, horizon - 36, 6, (225, 185, 150)); }
                break;
            case Scene.Wedding:
                c.VerticalGradient(0, 240, (250, 235, 215), (215, 190, 160));
                c.Fill(110, 70, 150, 220, (40, 40, 50));
                c.Circle(130, 58, 14, (230, 190, 160));
                c.Fill(170, 80, 215, 220, (250, 250, 248));
                c.Circle(192, 66, 13, (235, 195, 165));
                for (var i = 0; i < 12; i++) c.Circle(r.Next(0, 320), r.Next(0, 60), 3, (255, 255, 255));
                break;
            case Scene.Lake:
                c.VerticalGradient(0, horizon, (120, 175, 235), (215, 230, 245));
                c.Peak(r.Next(80, 240), horizon - 70, horizon, 120, (90, 110, 90));
                c.VerticalGradient(horizon, 240, (60, 110, 160), (30, 70, 120));
                break;
            case Scene.Coast:
                c.VerticalGradient(0, horizon, (60, 140, 230), (170, 210, 245));
                c.Circle(r.Next(200, 290), r.Next(25, 50), 18, (255, 240, 170));
                c.VerticalGradient(horizon, horizon + 50, (30, 110, 170), (60, 150, 190));
                c.Fill(0, horizon + 50, 320, 240, (230, 205, 150));
                for (var i = 0; i < 5; i++) { var x = r.Next(10, 300); c.Fill(x, horizon - 40, x + 18, horizon, (235, 200, 160)); c.Fill(x + 4, horizon - 50, x + 14, horizon - 40, (180, 70, 50)); }
                break;
            case Scene.Portrait:
                c.VerticalGradient(0, 240, (200, 205, 215), (160, 165, 180));
                c.Circle(160, 95, 48, (230, 190, 160));
                c.Fill(95, 150, 225, 240, (50, 60, 90));
                c.Fill(125, 40, 195, 62, (80, 55, 40));
                break;
        }
        c.Noise(r, 6);
        return c.ToPng();
    }

    private static byte[] Screen(Builder b, int variant)
    {
        var r = b.Random;
        var c = new Canvas(400, 260);
        c.Fill(0, 0, 400, 260, (236, 238, 242));
        c.Fill(0, 0, 400, 22, (45, 50, 60));
        c.Fill(368, 5, 380, 17, (220, 80, 70));
        c.Fill(0, 22, 90, 260, (220, 224, 232));
        for (var i = 0; i < 6; i++) c.Fill(10, 36 + i * 22, 75, 44 + i * 22, (120, 125, 135));
        for (var i = 0; i < 9 + variant % 3; i++)
            c.Fill(105, 40 + i * 20, 105 + r.Next(120, 280), 48 + i * 20, (70, 72, 80));
        c.Fill(300, 225, 385, 248, (40, 110, 210));
        return c.ToPng();
    }

    private static byte[] Logo(Builder b)
    {
        var c = new Canvas(96, 96);
        c.Fill(0, 0, 96, 96, (255, 255, 255));
        c.Circle(48, 48, 34, (250, 200, 40));
        c.Circle(48, 48, 14, (120, 80, 30));
        return c.ToPng();
    }

    private static Canvas Paper(Builder b, int width, int height)
    {
        var c = new Canvas(width, height);
        c.Fill(0, 0, width, height, (246, 244, 238));
        return c;
    }

    private static Canvas LetterScan(Builder b)
    {
        var r = b.Random;
        var c = Paper(b, 420, 594);
        c.Fill(40, 40, 170, 50, (60, 60, 60));
        c.Fill(40, 56, 150, 63, (90, 90, 90));
        c.Fill(260, 120, 380, 128, (80, 80, 80));
        for (var i = 0; i < 16; i++)
            c.Fill(40, 190 + i * 16, 40 + r.Next(250, 340), 197 + i * 16, (70, 70, 70));
        c.Fill(40, 480, 140, 488, (60, 60, 60));
        c.Noise(r, 10);
        return c;
    }

    private static Canvas IdCardScan(Builder b)
    {
        var r = b.Random;
        var c = Paper(b, 420, 594);
        c.Fill(60, 60, 360, 250, (205, 215, 225));
        c.Fill(80, 90, 160, 200, (150, 150, 160));
        c.Circle(120, 125, 22, (200, 175, 150));
        for (var i = 0; i < 6; i++) c.Fill(180, 95 + i * 18, 180 + r.Next(90, 160), 102 + i * 18, (50, 60, 80));
        c.Fill(80, 215, 340, 222, (60, 60, 60));
        c.Fill(80, 228, 340, 235, (60, 60, 60));
        c.Noise(r, 8);
        return c;
    }

    private static DateTime D(int y, int m, int d, int h = 12, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    /// <summary>Collects folders and files with their truth while writing them.</summary>
    private sealed class Builder(string root, Random random)
    {
        private readonly List<(string Path, DateTime Time)> _dirTimes = [];

        public Random Random { get; } = random;
        public List<TruthDirectoryEntry> Directories { get; } = [];
        public List<TruthFileEntry> Files { get; } = [];

        public void Dir(string path, string? verdict, string? note = null)
        {
            Directory.CreateDirectory(Full(path));
            Directories.Add(new TruthDirectoryEntry(path, verdict, note));
        }

        public void File(string path, byte[] content, DateTime modified, string? category, string[] sensitive, string? note = null,
            TruthDescription? description = null)
        {
            var full = Full(path);
            System.IO.File.WriteAllBytes(full, content);
            System.IO.File.SetLastWriteTimeUtc(full, modified);
            Files.Add(new TruthFileEntry(path, category, sensitive, note, description));
            _dirTimes.Add((EvalTruth.ParentOf(path), modified));
        }

        /// <summary>Folder times follow their newest file, as on a real drive; set last, after all writes.</summary>
        public void ApplyDirectoryTimes()
        {
            foreach (var group in _dirTimes.GroupBy(t => t.Path))
                Directory.SetLastWriteTimeUtc(Full(group.Key), group.Max(t => t.Time));
        }

        private string Full(string path) => path == "." ? root : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
