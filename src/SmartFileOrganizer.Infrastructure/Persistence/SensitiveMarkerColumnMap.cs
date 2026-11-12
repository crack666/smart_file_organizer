using System.Reflection;
using Dapper;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Reads the column <c>sensitive_markers</c> into the private string property
/// <c>SensitiveMarkerTokens</c> of <see cref="Domain.Models.FileNode"/>, which parses the stored
/// token list. (<c>ai_results</c> is read through <c>ClassificationRepository.Row</c> instead.)
/// </summary>
/// <remarks>
/// Dapper never calls a type handler for an enum member: it parses a string column with
/// <c>Enum.Parse(ignoreCase)</c>. That accepted <c>zugangsdaten,schluessel</c> by accident (member
/// names) and threw for <c>private_nachricht</c>, so every read of a folder with a private message
/// failed (found by the evaluation corpus of
/// https://github.com/crack666/smart_file_organizer/issues/36). Mapping the column to a string
/// member keeps the token format the only one that matters, also when the codes change.
/// </remarks>
internal sealed class SensitiveMarkerColumnMap : SqlMapper.ITypeMap
{
    public const string Column = "sensitive_markers";
    public const string PropertyName = "SensitiveMarkerTokens";

    private readonly DefaultTypeMap _inner;
    private readonly PropertyInfo _tokens;

    public SensitiveMarkerColumnMap(Type type)
    {
        _inner = new DefaultTypeMap(type);
        _tokens = type.GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                  ?? throw new InvalidOperationException($"{type.Name} has no property {PropertyName}.");
    }

    public ConstructorInfo? FindConstructor(string[] names, Type[] types) => _inner.FindConstructor(names, types);

    public ConstructorInfo? FindExplicitConstructor() => _inner.FindExplicitConstructor();

    public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName) =>
        _inner.GetConstructorParameter(constructor, columnName);

    public SqlMapper.IMemberMap? GetMember(string columnName) =>
        string.Equals(columnName, Column, StringComparison.OrdinalIgnoreCase)
            ? new TokenMember(columnName, _tokens)
            : _inner.GetMember(columnName);

    private sealed class TokenMember(string columnName, PropertyInfo property) : SqlMapper.IMemberMap
    {
        public string ColumnName { get; } = columnName;
        public Type MemberType => Property.PropertyType;
        public PropertyInfo Property { get; } = property;
        public FieldInfo? Field => null;
        public ParameterInfo? Parameter => null;
    }
}
