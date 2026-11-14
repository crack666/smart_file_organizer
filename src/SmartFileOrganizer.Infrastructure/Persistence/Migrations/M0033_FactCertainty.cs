namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 33: the certainty of a file fact (https://github.com/crack666/smart_file_organizer/issues/103). The kind and origin
/// of a video (<c>video_kind</c>, <c>recording_device</c>, ...) are read from the container, the name, files next to it or
/// the model, and each says how far it can be relied on: <c>certain</c>, <c>likely</c> or <c>uncertain</c>, the codes of
/// <c>ResultCertainty</c>. Facts stored before keep <c>NULL</c>, which means "read directly, no certainty given". The new
/// sources <c>name</c> and <c>companion</c> need no change: <c>file_facts.source</c> has no CHECK. Existing analyses stay
/// valid; <c>sfo facts refresh &lt;job&gt;</c> adds the video facts to them without the model.
/// </summary>
internal sealed class M0033_FactCertainty : SqlSchemaMigration
{
    public override int Version => 33;
    public override string Description => "Certainty of file facts";

    protected override string Sql => """
        ALTER TABLE file_facts ADD COLUMN certainty TEXT;
        """;
}
