namespace SmartFileOrganizer.Cli.Contract;

/// <summary>Process exit codes. Part of the machine contract (docs/cli.md); do not renumber.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The command ran but could not do its job (job not found, scan failed, model missing, ...).</summary>
    public const int Failure = 1;

    /// <summary>Wrong invocation: unknown command or option, missing or invalid argument.</summary>
    public const int Usage = 2;

    /// <summary>Ollama is not reachable.</summary>
    public const int OllamaUnavailable = 3;

    /// <summary>The model server went away during the run and stayed away longer than <c>ModelServer.MaxWaitMinutes</c>;
    /// items not judged stay open and the command can be run again.</summary>
    public const int ModelServerLost = 4;

    /// <summary>Interrupted by Ctrl+C; persisted work stays resumable.</summary>
    public const int Cancelled = 130;
}

/// <summary>Values of <c>error.code</c> in the JSON envelope. Stable strings for machine callers.</summary>
public static class ErrorCodes
{
    public const string Usage = "usage";
    public const string Cancelled = "cancelled";
    public const string OllamaUnavailable = "ollama_unavailable";
    public const string ModelServerLost = "model_server_lost";
    public const string JobNotFound = "job_not_found";
    public const string PathNotFound = "path_not_found";
    public const string ScanFailed = "scan_failed";
    public const string ScanNotCompleted = "scan_not_completed";
    public const string AnalysisFailed = "analysis_failed";
    public const string DatabaseError = "database_error";
    public const string ModelMissing = "model_missing";
    public const string RulesInvalid = "rules_invalid";
    public const string ExportFailed = "export_failed";
    public const string WrongRole = "wrong_role";
    public const string CompareIncomplete = "compare_incomplete";
    public const string ComparisonNotFound = "comparison_not_found";
    public const string WordsInvalid = "words_invalid";
    public const string OutputInsideJob = "output_inside_job";
    public const string TruthInvalid = "truth_invalid";
    public const string RunNotFound = "run_not_found";
    public const string OutputNotEmpty = "output_not_empty";
    public const string BundleNotFound = "bundle_not_found";
    public const string PatternsInvalid = "patterns_invalid";
    public const string LibraryPatternsInvalid = "library_patterns_invalid";
    public const string SettingsInvalid = "settings_invalid";
    public const string FindingNotFound = "finding_not_found";
    public const string TargetsInvalid = "targets_invalid";
    public const string InternalError = "internal_error";
}
