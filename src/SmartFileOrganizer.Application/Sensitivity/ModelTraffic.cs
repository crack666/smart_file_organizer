namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// Logger category of the model traffic: prompts sent to the local model and its answers, on level Debug.
/// Use it as <c>ILogger&lt;ModelTraffic&gt;</c>.
/// </summary>
/// <remarks>
/// <para>Prompts carry text excerpts of the files, so this category must only reach local sinks: the CLI
/// writes it to a debug log file next to the database and never to stderr
/// (https://github.com/crack666/smart_file_organizer/issues/33).</para>
/// <para>For a marked file even the local sink gets no excerpt, no facts and no answer text: the entry says
/// only that the content was withheld (<see cref="Withheld"/>).</para>
/// </remarks>
public sealed class ModelTraffic
{
    private ModelTraffic()
    {
    }

    /// <summary>The logger category name, for filters and providers.</summary>
    public static string Category { get; } = typeof(ModelTraffic).FullName!;

    /// <summary>Text logged instead of a prompt or an answer of a marked file.</summary>
    public const string Withheld = "(withheld: the file is marked sensitive)";
}
