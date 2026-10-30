using Exa.OBERON.ServiceGateway.Contracts;
using Google.Protobuf.Reflection;

namespace OberonProtoExplorer.Services;

public sealed record RpcFieldDocumentation(string Name, string JsonName, string Type, string Description,
    bool IsRepeated, bool IsMap, bool IsOptional, IReadOnlyList<string> EnumValues);
public sealed record RpcDocumentation(string Name, string RequestType, string ResponseType, string Description,
    string Category, string Risk, IReadOnlyList<RpcFieldDocumentation> Fields)
{
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> ImportantNotes { get; init; } = [];
}

public sealed class RpcSchemaCatalog
{
    private readonly Dictionary<string, MethodDescriptor> _methods;
    private readonly RpcInsightsCatalog _insights;
    public RpcSchemaCatalog()
    {
        _methods = Empty.Descriptor.File.Services.SelectMany(s => s.Methods).ToDictionary(m => m.Name, StringComparer.Ordinal);
        _insights = RpcInsightsCatalog.Load(Empty.Descriptor.File.Services.Single());
        var source = SourceComments.Load();
        Endpoints = Array.AsReadOnly(_methods.Values.Select(m =>
        {
            var category = source.Categories.GetValueOrDefault(m.Name, "Ostatné");
            var insight = _insights.Find(m.Name);
            var description = (insight is null ? RpcPurposes.Describe(m.Name) : "Účel podľa podkladov: " + insight.Description) + $"\nRPC metóda {m.Name}; oblasť: {category}. Požiadavka: {m.InputType.Name}; odpoveď: {m.OutputType.Name}.";
            if (m.Name.StartsWith("Call", StringComparison.Ordinal))
                description += " Všeobecné volanie procedúry podľa katalógu servera; konkrétne argumenty a účinky určuje zvolená procedúra. Schéma JSON payloadu nie je súčasťou protobuf zmluvy.";
            description = WithComment(description, source.Rpcs.GetValueOrDefault(m.Name));
            return new RpcDocumentation(m.Name, m.InputType.Name, m.OutputType.Name, description, category, ClassifyRisk(m.Name),
                Array.AsReadOnly(m.InputType.Fields.InDeclarationOrder().Select(f => new RpcFieldDocumentation(
                    f.Name, f.JsonName, FieldTypeName(f),
                        (insight?.Fields.TryGetValue(f.JsonName, out var note) == true ? note + "\n" : "") +
                        DescribeField(f, source), f.IsRepeated, f.IsMap, f.HasPresence,
                    f.FieldType == FieldType.Enum ? Array.AsReadOnly(f.EnumType.Values.Select(v => v.Name).ToArray())
                        : Array.Empty<string>())).ToArray()))
            { Sources = insight?.Sources ?? [], ImportantNotes = insight?.Notes ?? [] };
        }).ToArray());
    }
    private static string DescribeField(FieldDescriptor field, SourceComments source)
    {
        var shape = field.IsMap ? "mapa (JSON objekt)" : field.IsRepeated ? "zoznam (JSON pole)"
            : field.FieldType == FieldType.Message ? "vnorená správa (upraviteľný JSON objekt)"
            : field.FieldType == FieldType.Enum ? "výber hodnoty enum" : "skalárna hodnota";
        var description = $"Pole {field.Name}; typ {FieldTypeName(field)}; {shape}.";
        if (field.FieldType is FieldType.Int64 or FieldType.UInt64 or FieldType.SInt64 or FieldType.Fixed64 or FieldType.SFixed64)
            description += " Šablóna používa desiatkový reťazec pre 64-bitové číslo.";
        if (field.FieldType == FieldType.Bytes) description += " Bajty sa zapisujú ako Base64 reťazec.";
        // Presence is protobuf metadata, not a claim about business-required fields.
        description += field.HasPresence ? " Protobuf sleduje prítomnosť poľa; obchodná povinnosť nie je určená touto schémou."
            : " Obchodná povinnosť nie je určená protobuf schémou.";
        return WithComment(description, source.Fields.GetValueOrDefault(field.ContainingType.Name + "." + field.Name));
    }
    private static string WithComment(string description, string? comment) => string.IsNullOrWhiteSpace(comment)
        ? description : description + "\nPôvodný komentár proto (EN):\n" + comment;
    private static string ClassifyRisk(string name)
    {
        if (new[] { "Authenticate", "RefreshToken", "ValidateToken", "Logout", "GetLoginSalt", "GetLoginSalts", "LoginUser", "LoginUserEx", "IsLogin", "GetSessionInfo" }.Contains(name)) return "Session";
        if (name.StartsWith("Delete", StringComparison.Ordinal) || name.StartsWith("Remove", StringComparison.Ordinal)) return "Destructive";
        if (new[] { "Add", "Create", "Update", "Set" }.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return "Write";
        if (new[] { "Get", "List", "Search", "Find" }.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || name is "Ping" or "HealthCheck") return "Read";
        return "Unknown";
    }

    // Source text supplies annotations ONLY. Service coverage, field types and JSON parsing
    // always come from generated descriptors, never from these declaration recognizers.
    private sealed class SourceComments
    {
        public Dictionary<string, string> Rpcs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Categories { get; } = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> CategoryNames = new(StringComparer.Ordinal)
        {
            ["System"] = "Systém", ["Authentication"] = "Autentifikácia",
            ["Login pass-through (identical semantics to the original Gen1/Gen2 web service login surface)"] = "Prihlásenie a relácia",
            ["User Management"] = "Používatelia", ["Business Partner"] = "Obchodní partneri",
            ["Product"] = "Produkty", ["Stock"] = "Sklad", ["Stock Card Write Operations (Proxy-only)"] = "Sklad",
            ["Company/Common"] = "Firma", ["Cash Register"] = "Pokladnica", ["Loyalty System"] = "Vernostný systém",
            ["Hotel"] = "Hotel", ["Food Menu"] = "Jedálny lístok", ["Document"] = "Dokumenty", ["Invoice"] = "Faktúry",
            ["Reports"] = "Zostavy", ["Settings"] = "Nastavenia", ["Server Management"] = "Správa servera"
        };
        public static SourceComments Load()
        {
            var result = new SourceComments();
            var path = Path.Combine(AppContext.BaseDirectory, "Proto", "oberon_service_grpc.proto");
            if (!File.Exists(path))
                throw new InvalidOperationException("Chýba zdrojová protobuf zmluva pre dokumentáciu: " + path);
            string? message = null;
            var category = "Ostatné";
            var comments = new List<string>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    var text = line[2..].TrimStart();
                    comments.Add(text);
                    if (CategoryNames.TryGetValue(text, out var translated)) category = translated;
                    continue;
                }
                var declaration = System.Text.RegularExpressions.Regex.Match(line, @"^message\s+(\w+)\s*\{");
                if (declaration.Success) message = declaration.Groups[1].Value;
                var rpc = System.Text.RegularExpressions.Regex.Match(line, @"^rpc\s+(\w+)\s*\(");
                if (rpc.Success)
                {
                    var name = rpc.Groups[1].Value;
                    result.Rpcs[name] = string.Join("\n", comments);
                    result.Categories[name] = name is "CallG1Procedure" or "CallG2Procedure" or "CallCRG2Procedure" or "ListG1Procedures" or "ListCRG2Procedures"
                        ? "Katalógy a všeobecné procedúry" : category;
                }
                if (message != null)
                {
                    var field = System.Text.RegularExpressions.Regex.Match(line, @"\b(\w+)\s*=\s*\d+\s*;");
                    if (field.Success)
                    {
                        var inline = line.IndexOf("//", StringComparison.Ordinal);
                        if (inline >= 0) comments.Add(line[(inline + 2)..].TrimStart());
                        result.Fields[message + "." + field.Groups[1].Value] = string.Join("\n", comments);
                    }
                    if (line == "}") message = null;
                }
                comments.Clear();
            }
            return result;
        }
    }

    private static string FieldTypeName(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Message => field.MessageType.Name,
        FieldType.Enum => field.EnumType.Name,
        _ => field.FieldType.ToString().ToLowerInvariant()
    };
    public IReadOnlyList<RpcDocumentation> Endpoints { get; }
    public string FieldInsight(string rpc, string message, string field) => _insights.FieldNote(rpc, message, field);
    public RpcDocumentation Get(string name) => Endpoints.SingleOrDefault(e => e.Name == name)
        ?? throw new ArgumentException($"Neznáma RPC metóda: {name}", nameof(name));
    public string CreateTemplate(string name)
    {
        Get(name);
        return System.Text.Json.JsonSerializer.Serialize(Template(_methods[name].InputType, new HashSet<MessageDescriptor>()),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
    private static Dictionary<string, object?> Template(MessageDescriptor message, HashSet<MessageDescriptor> ancestry)
    {
        if (!ancestry.Add(message)) return new(); // Break recursive message cycles with a valid empty object.
        var result = new Dictionary<string, object?>();
        foreach (var f in message.Fields.InDeclarationOrder())
        {
            result[f.JsonName] = f.IsMap ? new Dictionary<string, object?>() : f.IsRepeated ? Array.Empty<object>() : f.FieldType switch
            {
                FieldType.Message => Template(f.MessageType, ancestry),
                FieldType.Enum => f.EnumType.Values[0].Name,
                FieldType.String or FieldType.Bytes => "",
                FieldType.Bool => false,
                FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 or FieldType.UInt64 or FieldType.Fixed64 => "0",
                _ => (object)0
            };
        }
        ancestry.Remove(message);
        return result;
    }
    public void Validate(string name, string json)
    {
        Get(name);
        if (json is null) throw new ArgumentException("JSON požiadavka nesmie byť null.", nameof(json));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { MaxDepth = 100 });
            CheckObject(document.RootElement, _methods[name].InputType, "$", 0);
            Google.Protobuf.JsonParser.Default.Parse(json, _methods[name].InputType);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Google.Protobuf.InvalidJsonException
            or Google.Protobuf.InvalidProtocolBufferException or FormatException or OverflowException)
        {
            throw new ArgumentException($"Neplatná protobuf JSON požiadavka pre {name}: {ex.Message}", nameof(json), ex);
        }
    }
    private static void CheckObject(System.Text.Json.JsonElement value, MessageDescriptor message, string path, int depth)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException($"{path}: očakáva sa JSON objekt typu {message.Name}.", "json");
        if (depth >= 100) throw new ArgumentException($"{path}: prekročená hĺbka JSON.", "json");
        var seen = new HashSet<FieldDescriptor>();
        foreach (var property in value.EnumerateObject())
        {
            var field = message.Fields.InDeclarationOrder().FirstOrDefault(f => f.Name == property.Name || f.JsonName == property.Name)
                ?? throw new ArgumentException($"{path}.{property.Name}: neznáme pole typu {message.Name}.", "json");
            if (!seen.Add(field)) throw new ArgumentException($"{path}.{property.Name}: duplicitné pole (vrátane protobuf/JSON aliasu).", "json");
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue; // Protobuf JSON null means unset.
            if (field.IsMap)
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new ArgumentException($"{path}.{property.Name}: očakáva sa JSON objekt mapy.", "json");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var mapValue = field.MessageType.FindFieldByNumber(2);
                foreach (var entry in property.Value.EnumerateObject())
                {
                    if (!keys.Add(entry.Name)) throw new ArgumentException($"{path}.{property.Name}: duplicitný kľúč mapy.", "json");
                    if (mapValue.FieldType == FieldType.Message) CheckObject(entry.Value, mapValue.MessageType, path + "." + property.Name + "." + entry.Name, depth + 1);
                }
            }
            else if (field.IsRepeated)
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new ArgumentException($"{path}.{property.Name}: očakáva sa JSON pole.", "json");
                if (field.FieldType == FieldType.Message)
                    foreach (var entry in property.Value.EnumerateArray()) CheckObject(entry, field.MessageType, path + "." + property.Name + "[]", depth + 1);
            }
            else if (field.FieldType == FieldType.Message)
                CheckObject(property.Value, field.MessageType, path + "." + property.Name, depth + 1);
        }
    }
}
