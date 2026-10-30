using System.Text.Json;
using Google.Protobuf.Reflection;
using ServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;

namespace OberonProtoExplorer.Services;

public sealed record RpcInsight(string Rpc, string Description, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> MessageFields, IReadOnlyList<string> Notes, IReadOnlyList<string> Sources);

// Annotations only: the descriptors remain authoritative for methods, fields and validation.
public sealed class RpcInsightsCatalog
{
    private readonly IReadOnlyDictionary<string, RpcInsight> _entries;
    public RpcInsightsCatalog(string json, ServiceDescriptor service)
    {
        var entries = JsonSerializer.Deserialize<List<RpcInsight>>(json, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true }) ?? throw new FormatException("Doplňujúce informácie musia byť pole.");
        if (entries.GroupBy(e => e.Rpc, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new FormatException("Duplicitná RPC anotácia.");
        foreach (var entry in entries)
        {
            var method = service.Methods.SingleOrDefault(m => m.Name == entry.Rpc)
                ?? throw new FormatException("Anotácia neznámej RPC: " + entry.Rpc);
            if (string.IsNullOrWhiteSpace(entry.Description) || entry.Fields is null || entry.MessageFields is null ||
                entry.Notes is null || entry.Sources is null || entry.Sources.Count == 0 || entry.Sources.Any(string.IsNullOrWhiteSpace))
                throw new FormatException("Neúplná RPC anotácia: " + entry.Rpc);
            if (entry.Fields.Any(f => string.IsNullOrWhiteSpace(f.Value) ||
                !method.InputType.Fields.InDeclarationOrder().Any(d => d.JsonName == f.Key)))
                throw new FormatException("Anotácia poľa mimo proto požiadavky: " + entry.Rpc);
            var messages = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
            void Visit(MessageDescriptor message)
            {
                if (!messages.TryAdd(message.Name, message)) return;
                foreach (var field in message.Fields.InDeclarationOrder().Where(f => f.FieldType == FieldType.Message)) Visit(field.MessageType);
            }
            Visit(method.InputType); Visit(method.OutputType);
            foreach (var field in entry.MessageFields)
            {
                var split = field.Key.LastIndexOf('.');
                if (split < 1 || string.IsNullOrWhiteSpace(field.Value) ||
                    !messages.TryGetValue(field.Key[..split], out var message) ||
                    !message.Fields.InDeclarationOrder().Any(f => f.JsonName == field.Key[(split + 1)..] || f.Name == field.Key[(split + 1)..]))
                    throw new FormatException("Anotácia poľa mimo RPC schémy: " + entry.Rpc + " / " + field.Key);
            }
        }
        _entries = entries.ToDictionary(e => e.Rpc, StringComparer.Ordinal);
    }
    public static RpcInsightsCatalog Load(ServiceDescriptor service) => new(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Documentation", "rpc-insights.json")), service);
    public RpcInsight? Find(string rpc) => _entries.GetValueOrDefault(rpc);
    public string FieldNote(string rpc, string message, string field) => Find(rpc)?.MessageFields.GetValueOrDefault(message + "." + field) ?? "";
}
