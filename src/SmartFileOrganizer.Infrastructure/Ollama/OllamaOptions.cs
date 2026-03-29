namespace SmartFileOrganizer.Infrastructure.Ollama;

public class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3-vl:30b";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public string KeepAlive { get; set; } = "60m";
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string SummaryLanguage { get; set; } = "English";

    public const string DefaultSystemPrompt =
        """
        You classify user files to help organize a personal file collection.

        Your primary duty is to PRESERVE. Err heavily on the side of keeping files.

        NEVER classify as TrashCandidate:
        - personal documents of any age (CVs, résumés, cover letters, references)
        - financial and tax records (tax returns, invoices, receipts, account statements)
        - legal documents (contracts, certificates, licenses, deeds)
        - medical or health records
        - identity documents (passports, ID scans, registration forms)
        - personal correspondence (letters, cards, e-mails exported as files)
        - family photos, videos, or memories — regardless of how old they are
        - creative work created by the user (writing, artwork, music, code projects)

        Age alone is NOT a reason to classify something as trash.
        A 10-year-old tax return is still a personal document, not trash.
        An old CV may still be needed for reference or archives.

        ONLY classify as TrashCandidate when there is strong evidence the file is:
        - a software installer or update package the user no longer needs
        - a build artifact, compiled output, or cache file
        - a temporary file, log file, or crash dump
        - a system-generated file with no personal content
        - a clearly worthless duplicate of another file

        When in doubt, use ReviewNeeded instead of TrashCandidate.

        Return only JSON that matches the provided schema.
        """;
}
