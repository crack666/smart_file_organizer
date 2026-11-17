using System.Text.Json.Nodes;
using SmartFileOrganizer.Infrastructure.ModelServer;

namespace SmartFileOrganizer.Infrastructure.Ollama;

public class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4:26b";
    /// <summary>Time limit of one attempt of a model call. A fraction is allowed (tests).</summary>
    public double TimeoutSeconds { get; set; } = 180;

    /// <summary>Attempts after the first when the server does not answer (no connection, time limit, 429, 502, 503, 504).</summary>
    public int MaxRetries { get; set; } = 2;
    public string KeepAlive { get; set; } = "60m";
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string SummaryLanguage { get; set; } = "English";
    public long MaxImageUploadBytes { get; set; } = 10 * 1024 * 1024;   // 10 MB
    public long MaxDocumentUploadBytes { get; set; } = 5 * 1024 * 1024; // 5 MB
    public int MaxSamplesPerDirectory { get; set; } = 15;
    /// <summary>
    /// Number of concurrent classification requests sent by the app.
    /// Keep aligned with Ollama's NUM_PARALLEL to maximize GPU utilization.
    /// </summary>
    public int MaxParallelRequests { get; set; } = 1;

    // Upper bounds of an answer in tokens (Ollama num_predict), well above what a sound answer needs, so that a
    // derailed answer stops early instead of running into the time limit. Measured 13.11.2026: a classification
    // answer averaged 108 tokens before the answer was shortened.
    /// <summary>Token limit of a classification answer (also the second check).</summary>
    public int ClassificationMaxAnswerTokens { get; set; } = 512;
    /// <summary>Token limit of a pre-assessment answer; it lists anomalous files with a reason each.</summary>
    public int PreAssessmentMaxAnswerTokens { get; set; } = 2048;
    /// <summary>Token limit of a directory summary answer.</summary>
    public int SummaryMaxAnswerTokens { get; set; } = 1024;

    /// <summary><see cref="ModelProviders.Ollama"/> (default) or <see cref="ModelProviders.OpenAiCompatible"/>.</summary>
    public string Provider { get; set; } = ModelProviders.Ollama;

    /// <summary>Key for the broker. Prefer <see cref="ApiKeyEnv"/>; a key here never goes into a command output.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variable that holds the key. The variable's value is never written out.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>Context size sent with every call (<c>num_ctx</c> on Ollama). Null: the model's own default.</summary>
    public int? NumCtx { get; set; }

    /// <summary>Sampling temperature. Null: the model's own default, which can differ between tags of one model.</summary>
    public double? Temperature { get; set; }

    /// <summary>Caller fields a broker can read (for example who reserved the GPU). Not a secret.</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Fields merged into each request, for what a broker does not pass through on its own. Not a secret.</summary>
    public JsonObject? ExtraBody { get; set; }

    /// <summary>Named sets applied by <c>--profile</c>. Not sent to the server.</summary>
    public Dictionary<string, ModelProfile>? Profiles { get; set; }

    /// <summary>The key from <see cref="ApiKey"/> or from <see cref="ApiKeyEnv"/>, or null when neither is set.</summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            return ApiKey;
        if (string.IsNullOrWhiteSpace(ApiKeyEnv))
            return null;
        var value = Environment.GetEnvironmentVariable(ApiKeyEnv);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Applies one named profile, then an explicit provider. The provider wins over the profile.</summary>
    public void ApplySelection(string? profile, string? provider)
    {
        if (!string.IsNullOrWhiteSpace(profile))
        {
            if (Profiles is null || !Profiles.TryGetValue(profile, out var chosen))
                throw new ModelSettingsException($"Unknown model profile '{profile}'.");
            chosen.ApplyTo(this);
        }
        if (!string.IsNullOrWhiteSpace(provider))
            Provider = provider;
        Provider = ModelProviders.Canonical(Provider);
    }

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

/// <summary>One named set of provider, model, context and parallelism. Empty fields leave the current setting.</summary>
public sealed class ModelProfile
{
    public string? Provider { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKeyEnv { get; set; }
    public int? NumCtx { get; set; }
    public double? Temperature { get; set; }
    public int? Parallel { get; set; }

    public void ApplyTo(OllamaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(Provider)) options.Provider = Provider;
        if (!string.IsNullOrWhiteSpace(BaseUrl)) options.BaseUrl = BaseUrl;
        if (!string.IsNullOrWhiteSpace(Model)) options.Model = Model;
        if (!string.IsNullOrWhiteSpace(ApiKeyEnv)) options.ApiKeyEnv = ApiKeyEnv;
        if (NumCtx is int ctx) options.NumCtx = ctx;
        if (Temperature is double temperature) options.Temperature = temperature;
        if (Parallel is int parallel) options.MaxParallelRequests = parallel;
    }
}

/// <summary>The <c>Model</c> section of settings. It overrides <c>Ollama</c> where it names a value. <c>ExtraBody</c> is read separately.</summary>
public sealed class ModelSection
{
    public string? Provider { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiKeyEnv { get; set; }
    public int? NumCtx { get; set; }
    public double? Temperature { get; set; }
    public int? Parallel { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public Dictionary<string, ModelProfile>? Profiles { get; set; }

    public void ApplyTo(OllamaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(Provider)) options.Provider = Provider;
        if (!string.IsNullOrWhiteSpace(BaseUrl)) options.BaseUrl = BaseUrl;
        if (!string.IsNullOrWhiteSpace(Model)) options.Model = Model;
        if (!string.IsNullOrWhiteSpace(ApiKey)) options.ApiKey = ApiKey;
        if (!string.IsNullOrWhiteSpace(ApiKeyEnv)) options.ApiKeyEnv = ApiKeyEnv;
        if (NumCtx is int ctx) options.NumCtx = ctx;
        if (Temperature is double temperature) options.Temperature = temperature;
        if (Parallel is int parallel) options.MaxParallelRequests = parallel;
        if (Metadata is { Count: > 0 }) options.Metadata = Metadata;
        if (Profiles is { Count: > 0 }) options.Profiles = Profiles;
    }
}
