using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Fixed string codes of <see cref="DirectoryVerdictKind"/>, <see cref="RetentionValue"/> and
/// <see cref="VerdictSource"/>. The single source of the codes is the
/// <see cref="JsonStringEnumMemberNameAttribute"/> on each member, so database values and JSON
/// output cannot drift apart.
/// </summary>
public static class VerdictCodes
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<object, string> ToCode, Dictionary<string, object> FromCode)> Maps = new();

    /// <summary>The stored code of an enum value, for example <c>einheit</c>.</summary>
    public static string ToCode<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        GetMap(typeof(TEnum)).ToCode[value];

    public static string? ToCode<TEnum>(this TEnum? value) where TEnum : struct, Enum =>
        value is { } v ? v.ToCode() : null;

    /// <summary>Parses an exact code (case-insensitive). Returns false for anything else.</summary>
    public static bool TryParse<TEnum>(string? code, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (!GetMap(typeof(TEnum)).FromCode.TryGetValue(code.Trim(), out var boxed)) return false;
        value = (TEnum)boxed;
        return true;
    }

    /// <summary>Parses a stored code; null for null. Throws for an unknown code, which means the row is corrupt.</summary>
    public static TEnum? ParseNullable<TEnum>(string? code) where TEnum : struct, Enum
    {
        if (code is null) return null;
        if (TryParse<TEnum>(code, out var value)) return value;
        throw new FormatException($"Unknown {typeof(TEnum).Name} code '{code}'.");
    }

    /// <summary>All codes of an enum in declaration order.</summary>
    public static IReadOnlyList<string> All<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => v.ToCode()).ToList();

    private static (Dictionary<object, string> ToCode, Dictionary<string, object> FromCode) GetMap(Type type) =>
        Maps.GetOrAdd(type, t =>
        {
            var to = new Dictionary<object, string>();
            var from = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var code = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? throw new InvalidOperationException($"{t.Name}.{field.Name} has no code attribute.");
                var value = field.GetValue(null)!;
                to[value] = code;
                from[code] = value;
            }
            return (to, from);
        });
}
