namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 29: the duplicate report over a whole holding (https://github.com/crack666/smart_file_organizer/issues/87).
/// The head-and-tail checksum of <c>content_fingerprints</c> gives way to the sample checksum, which also reads six
/// fixed places between head and tail: <c>head_tail_hash</c> becomes <c>sample_hash</c>. Values of files above 128 KiB
/// were computed the old way and are cleared; they are computed anew when needed. For smaller files both checksums are
/// the SHA-256 of the whole file and stay. Full checksums stay as they are.
/// </summary>
internal sealed class M0029_SampleChecksum : SqlSchemaMigration
{
    public override int Version => 29;
    public override string Description => "Sample checksum in content fingerprints";

    protected override string Sql => """
        ALTER TABLE content_fingerprints RENAME COLUMN head_tail_hash TO sample_hash;
        UPDATE content_fingerprints SET sample_hash = NULL WHERE size > 131072;
        """;
}
