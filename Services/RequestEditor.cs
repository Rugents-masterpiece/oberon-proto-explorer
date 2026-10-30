using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OberonProtoExplorer.Services;

/// <summary>Lossless form editing of the same protobuf JSON sent by the RPC console.</summary>
public static class RequestEditor
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Update(string json, string name, string value, string kind, string? protoName = null)
    {
        try
        {
            var document = ReadObject(json);
            if (protoName is not null && protoName != name) document.Remove(protoName);
            document[name] = kind switch
            {
                "string" or "bytes" or "enum" => JsonValue.Create(value),
                "bool" => JsonValue.Create(bool.Parse(value)),
                "int64" or "sint64" or "sfixed64" => JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                "uint64" or "fixed64" => JsonValue.Create(ulong.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                "int32" or "sint32" or "sfixed32" => JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)),
                "uint32" or "fixed32" => JsonValue.Create(uint.Parse(value, CultureInfo.InvariantCulture)),
                "double" or "float" => value is "NaN" or "Infinity" or "-Infinity" ? JsonValue.Create(value) : JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)),
                "number" => JsonNode.Parse(value) is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number : throw new ArgumentException("Pole vyžaduje JSON číslo."),
                "array" => JsonNode.Parse(value) is JsonArray array ? array : throw new ArgumentException("Opakované pole vyžaduje JSON pole [...]."),
                "message" or "map" => ReadObject(value),
                _ => throw new ArgumentException("Neznámy typ poľa: " + kind)
            };
            return document.ToJsonString(Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new ArgumentException($"Neplatná hodnota poľa {name}: {ex.Message}", ex);
        }
    }

    public static string Remove(string json, string name, string? protoName = null)
    {
        var document = ReadObject(json);
        document.Remove(name);
        if (protoName is not null && protoName != name) document.Remove(protoName);
        return document.ToJsonString(Options);
    }

    public static string Value(string json, string name, string? protoName = null)
    {
        var document = ReadObject(json);
        var node = document[name] ?? (protoName is not null ? document[protoName] : null);
        return node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : node?.ToJsonString(Options) ?? "";
    }

    public static bool Contains(string json, string name, string? protoName = null)
    {
        var document = ReadObject(json);
        return document.ContainsKey(name) || (protoName is not null && document.ContainsKey(protoName));
    }

    private static JsonObject ReadObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Požiadavka musí byť JSON objekt {...}."); }
        catch (JsonException ex) { throw new ArgumentException("Neplatný JSON: " + ex.Message, ex); }
    }
}
