using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pointer.Infrastructure.Mappings;

/// <summary>
/// jsonb ↔ <c>List&lt;string&gt;</c> for advisory flag/bullet lists. Reading is tolerant: anything
/// that is not a JSON array (the <c>'{}'</c> a migration default left in 104 production rows, a
/// bare <c>null</c>, garbage) becomes an empty list instead of throwing mid-query — a single bad
/// row used to turn the whole comments page into a 500. Writing always emits an array, and the
/// column default is <c>'[]'::jsonb</c> so future rows never hit the tolerant path.
/// </summary>
public static class JsonStringList
{
    public static string Serialize(List<string> value) =>
        JsonSerializer.Serialize(value ?? new List<string>(), (JsonSerializerOptions?)null);

    public static List<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                    list.Add(el.GetString()!);
            }
            return list;
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static PropertyBuilder<List<string>> ConfigureJsonStringList(
        this PropertyBuilder<List<string>> b,
        string columnName
    ) =>
        b.HasColumnName(columnName)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'")
            .HasConversion(
                v => Serialize(v),
                v => Parse(v),
                new ValueComparer<List<string>>(
                    (a, c) => (a ?? new()).SequenceEqual(c ?? new()),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToList()
                )
            );
}
