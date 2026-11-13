using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>A run of a job that waits for the model server (table <c>model_server_waits</c>).</summary>
public sealed class ModelServerWait
{
    public long JobId { get; set; }

    /// <summary>When the server was first considered away (UTC).</summary>
    public DateTime Since { get; set; }

    /// <summary>When the waiting run last probed the server (UTC); an old value means the run is gone.</summary>
    public DateTime CheckedAt { get; set; }

    public ModelServerFailure Cause { get; set; }
}
