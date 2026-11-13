using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Why a call did not reach an answer of the model server. Only these count as the server being away; every
/// other failure (the server answered and refused, or the answer was unreadable) is an error of the item
/// (https://github.com/crack666/smart_file_organizer/issues/63).
/// </summary>
public enum ModelServerFailure
{
    /// <summary>No connection, or the connection broke before an answer: the server process is gone or restarting.</summary>
    [JsonStringEnumMemberName("connection_failed")]
    ConnectionFailed,

    /// <summary>No answer within the time limit, and the server did not answer a short probe either.</summary>
    [JsonStringEnumMemberName("timed_out")]
    TimedOut,

    /// <summary>HTTP 429 or 503: the server or a broker in front of it is busy and asks to come back later.</summary>
    [JsonStringEnumMemberName("busy")]
    Busy,

    /// <summary>HTTP 502 or 504: a proxy in front of the server got no answer from it.</summary>
    [JsonStringEnumMemberName("bad_gateway")]
    BadGateway
}

/// <summary>What a run reports about waiting for the model server (progress on stderr, GUI state).</summary>
public enum ModelServerWaitState
{
    /// <summary>The server is considered away; the run holds its model calls and probes the server.</summary>
    [JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>The server and the model answer again; the run sends its held calls.</summary>
    [JsonStringEnumMemberName("resumed")]
    Resumed,

    /// <summary>The server stayed away longer than the configured limit; the run ends and can be started again.</summary>
    [JsonStringEnumMemberName("given_up")]
    GivenUp
}
