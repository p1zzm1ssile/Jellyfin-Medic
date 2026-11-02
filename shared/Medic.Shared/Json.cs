using System;
using System.Globalization;
using System.Text.Json;

namespace Medic.Shared;

/// <summary>
/// Forgiving readers for the JSON other services send: their versions differ, so a field that's missing, of
/// another kind, or asked of a value that isn't an object reads as missing and never throws.
/// Medic Picks and Medic Profiles each compile this file in.
/// </summary>
internal static class Json
{
    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)d : null;

    public static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)d : 0;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTime? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
