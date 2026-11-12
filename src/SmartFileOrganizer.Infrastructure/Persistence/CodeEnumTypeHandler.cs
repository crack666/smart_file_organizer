using System.Data;
using Dapper;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Reads an enum stored as its fixed code (<see cref="VerdictCodes"/>), for example
/// <c>ai_results.origin = 'inherited'</c>. Registered once in <see cref="DatabaseContext"/>.
/// </summary>
/// <remarks>
/// As with <see cref="SensitiveMarkerTypeHandler"/>, Dapper binds enum <b>parameters</b> as integers and
/// ignores this handler for them, so repositories write <c>value.ToCode()</c> explicitly.
/// An unknown code throws: the row is corrupt, not merely unexpected.
/// </remarks>
internal sealed class CodeEnumTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum> where TEnum : struct, Enum
{
    public override TEnum Parse(object value) =>
        VerdictCodes.ParseNullable<TEnum>(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
        ?? throw new FormatException($"Empty {typeof(TEnum).Name} code.");

    public override void SetValue(IDbDataParameter parameter, TEnum value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToCode();
    }
}
