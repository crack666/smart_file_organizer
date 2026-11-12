using System.Data;
using Dapper;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Reads <see cref="SensitiveMarker"/> from its stored token list (<c>zugangsdaten,schluessel</c>).
/// Registered once in <see cref="DatabaseContext"/>.
/// </summary>
/// <remarks>
/// Dapper converts enum <b>parameters</b> to their underlying integer and ignores type handlers
/// for them, so writes never pass the enum itself: repositories bind
/// <see cref="SensitiveMarkerNames.Format"/> explicitly.
/// </remarks>
internal sealed class SensitiveMarkerTypeHandler : SqlMapper.TypeHandler<SensitiveMarker>
{
    public override SensitiveMarker Parse(object value) => value switch
    {
        null or DBNull => SensitiveMarker.None,
        string s => SensitiveMarkerNames.Parse(s),
        _ => SensitiveMarkerNames.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
    };

    public override void SetValue(IDbDataParameter parameter, SensitiveMarker value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = (object?)SensitiveMarkerNames.Format(value) ?? DBNull.Value;
    }
}
