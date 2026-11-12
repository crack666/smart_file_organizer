using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a bundle is: the unit the person decides once for many files
/// (https://github.com/crack666/smart_file_organizer/issues/39). The code in
/// <see cref="JsonStringEnumMemberNameAttribute"/> is the fixed value in <c>bundles.kind</c>;
/// read and write it with <see cref="VerdictCodes"/>.
/// </summary>
public enum BundleKind
{
    /// <summary>A copy of a whole device or computer (phone storage, user profile, system drive, USB stick,
    /// unpacked software). Left out of every other statistic; its parts are its children.</summary>
    [JsonStringEnumMemberName("dump")] Dump,

    /// <summary>The part of a dump worth keeping: camera folder, documents, chat media, ...
    /// (<see cref="DumpPart"/>).</summary>
    [JsonStringEnumMemberName("dump_keep")] DumpKeep,

    /// <summary>Everything else of a dump: app data, caches, system folders, empty folders.</summary>
    [JsonStringEnumMemberName("dump_noise")] DumpNoise,

    /// <summary>An existing folder with a meaningful name outside dumps. Taken as it is: never split, never renamed.</summary>
    [JsonStringEnumMemberName("folder")] Folder,

    /// <summary>Loose photos and videos taken close together in time: one occasion.</summary>
    [JsonStringEnumMemberName("event")] Event,

    /// <summary>Loose photos and videos of one year that formed no group large enough for an event.</summary>
    [JsonStringEnumMemberName("scattered")] Scattered
}

/// <summary>Where a dump comes from. Stored in <c>bundles.origin</c>.</summary>
public enum DumpOrigin
{
    /// <summary>Phone or tablet storage: <c>DCIM</c>, <c>Android</c>, <c>LOST.DIR</c>, app package names.</summary>
    [JsonStringEnumMemberName("phone")] Phone,

    /// <summary>A user profile or a whole system drive of a computer.</summary>
    [JsonStringEnumMemberName("computer")] Computer,

    /// <summary>A copied USB stick or memory card: volume system folders such as <c>System Volume Information</c>.</summary>
    [JsonStringEnumMemberName("removable_drive")] RemovableDrive,

    /// <summary>Unpacked software or a code project: <c>.git</c>, <c>node_modules</c>, programs with their libraries.</summary>
    [JsonStringEnumMemberName("software")] Software
}

/// <summary>Which worth-keeping part of a dump a <see cref="BundleKind.DumpKeep"/> bundle is. Stored in <c>bundles.part</c>.</summary>
public enum DumpPart
{
    [JsonStringEnumMemberName("camera")] Camera,
    [JsonStringEnumMemberName("screenshots")] Screenshots,
    [JsonStringEnumMemberName("chat_media")] ChatMedia,
    [JsonStringEnumMemberName("received")] Received,
    [JsonStringEnumMemberName("recordings")] Recordings,
    [JsonStringEnumMemberName("documents")] Documents,
    [JsonStringEnumMemberName("pictures")] Pictures,
    [JsonStringEnumMemberName("videos")] Videos,
    [JsonStringEnumMemberName("music")] Music,
    [JsonStringEnumMemberName("downloads")] Downloads,
    [JsonStringEnumMemberName("desktop")] Desktop
}

/// <summary>What the times of a bundle rest on. Stored in <c>bundles.time_source</c>.</summary>
public enum TimeSource
{
    /// <summary>Capture date from the photo or video metadata.</summary>
    [JsonStringEnumMemberName("capture_date")] CaptureDate,

    /// <summary>A date and time in the file name (<c>IMG_20150712_143000.jpg</c>, <c>WhatsApp Image 2015-07-12 at ...</c>).</summary>
    [JsonStringEnumMemberName("file_name")] FileName,

    /// <summary>Modification time from the file system. Changes with every copy, so the weakest source.</summary>
    [JsonStringEnumMemberName("modified_date")] ModifiedDate,

    /// <summary>The files of the bundle rest on more than one of the sources above.</summary>
    [JsonStringEnumMemberName("mixed")] Mixed
}
