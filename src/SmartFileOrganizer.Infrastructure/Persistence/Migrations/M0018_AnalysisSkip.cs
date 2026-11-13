namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 18: <c>analysis_skip</c> on files and directories (https://github.com/crack666/smart_file_organizer/issues/42).
/// Set by <c>sfo analyze --against</c> to the comparison state that let the analysis leave the item out; today the
/// only such state is <c>backed_up</c>. Null means the item takes part in the analysis as before. Each run sets the
/// column anew from the comparison it was given, so a run without one clears it.
/// </summary>
internal sealed class M0018_AnalysisSkip : SqlSchemaMigration
{
    public override int Version => 18;
    public override string Description => "Analysis skip of backed-up files and folders";

    protected override string Sql => """
        ALTER TABLE file_nodes ADD COLUMN analysis_skip TEXT CHECK (analysis_skip IN ('backed_up'));
        ALTER TABLE directory_nodes ADD COLUMN analysis_skip TEXT CHECK (analysis_skip IN ('backed_up'));
        """;
}
