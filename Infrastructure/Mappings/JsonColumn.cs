using System.Collections;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pointer.Infrastructure.Mappings;

/// <summary>
/// Generic jsonb ↔ class converter (R4-01), modelled on <see cref="JsonStringList"/>: one opaque
/// jsonb value per column, tolerant parse, explicit default SQL. Used for
/// <c>comments.custom_fields</c> (<c>Dictionary&lt;string,string&gt;</c>, default <c>'{}'</c>) and
/// <c>workspace_settings.comment_field_definitions</c> (<c>List&lt;CommentFieldDefinition&gt;</c>,
/// default <c>'[]'</c>).
/// <para>
/// Reading is tolerant for the same reason as JsonStringList: a bad row must never 500 the
/// comments page — anything unparseable or of the wrong JSON kind (an object where a dictionary
/// belongs, an array where a list belongs) parses to an empty instance. Writing is canonical:
/// dictionaries are sorted by key before serialization, and the value comparer compares the
/// CANONICAL serialized strings — .NET dictionary enumeration order is not stable, so comparing
/// by enumeration would make EF issue phantom UPDATEs on every save.
/// </para>
/// </summary>
public static class JsonColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Canonical serialization: camelCase, dictionary keys sorted (ordinal).</summary>
    public static string Serialize<T>(T? value) where T : class, new()
    {
        if (value is Dictionary<string, string> dict)
        {
            var sorted = new SortedDictionary<string, string>(dict, StringComparer.Ordinal);
            return JsonSerializer.Serialize(sorted, Options);
        }
        return JsonSerializer.Serialize(value ?? new T(), Options);
    }

    /// <summary>Tolerant parse: null/blank/garbage/wrong JSON kind → a new empty instance, never a throw.</summary>
    public static T Parse<T>(string? json) where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return new T();
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Dictionaries expect an object; everything else (lists) expects an array.
            var expectsObject = typeof(IDictionary).IsAssignableFrom(typeof(T));
            if (expectsObject != (doc.RootElement.ValueKind == JsonValueKind.Object))
                return new T();
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    /// <summary>Compares/hashes/snapshots the canonical serialized string, so insertion order can never produce phantom UPDATEs.</summary>
    public static ValueComparer<T> BuildComparer<T>() where T : class, new() =>
        new(
            (a, b) => Serialize(a) == Serialize(b),
            v => Serialize(v).GetHashCode(),
            v => Parse<T>(Serialize(v))
        );

    public static PropertyBuilder<T> ConfigureJsonColumn<T>(
        this PropertyBuilder<T> b,
        string column,
        string defaultSql
    ) where T : class, new() =>
        b.HasColumnName(column)
            .HasColumnType("jsonb")
            .HasDefaultValueSql(defaultSql)
            .HasConversion(v => Serialize(v), v => Parse<T>(v), BuildComparer<T>());
}
