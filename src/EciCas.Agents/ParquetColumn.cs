using System.Globalization;

namespace EciCas.Agents;

/// <summary>
/// Read-side guards for parquet rows. Parquet.Net's POCO path needs a
/// parameterless row, so `required` is unavailable; required columns are
/// nullable with no default instead, and a missing one throws here rather
/// than reading back as a blank that looks like a real value.
/// </summary>
internal static class ParquetColumn
{
    public static string Required(string? value, string column) =>
        value ?? throw new InvalidDataException($"Parquet row is missing required column '{column}'.");

    public static DateTimeOffset RequiredTime(string? value, string column) =>
        DateTimeOffset.TryParse(Required(value, column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : throw new InvalidDataException($"Parquet column '{column}' is not a timestamp: '{value}'.");
}
