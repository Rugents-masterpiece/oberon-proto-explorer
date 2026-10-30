using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace OberonProtoExplorer.Services;

public record ProcedureItem(string Backend, string Name, string InvocationName, string RpcName,
    string Description, string ProcedureClass, string HttpMethod, string UriTemplate,
    string Arguments, string ReturnType, string ExampleJson, bool GenericCallable, bool HasPayloadSchema);

public record ProcedureCatalogResult(string Backend, int Total, IReadOnlyList<ProcedureItem> Items,
    string Source, string? Error);

public sealed class ProcedureCatalog(GrpcExplorer explorer, IConfiguration configuration)
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private const string UnknownArguments = "neznáme (katalóg ich neuvádza)";
    private const string UnknownReturn = "neznámy (katalóg ho neuvádza)";

    /// <summary>
    /// G1/CRG2 use authenticated list RPCs. G2 uses GET /api/g2/procedures
    /// with the gateway session obtained by gRPC login. port is the gRPC override; REST uses Oberon:RestPort,
    /// otherwise port+1 when supplied, otherwise 21009. Never follows redirects.
    /// </summary>
    public async Task<ProcedureCatalogResult> LoadAsync(string backend, string? host = null, int? port = null,
        CancellationToken cancellationToken = default, string? username = null, string? password = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        backend = (backend ?? "").ToUpperInvariant();
        var source = backend == "G2" ? "REST GET /api/g2/procedures" : "gRPC List" + backend + "Procedures";
        ProcedureCatalogResult Failure(string message) => new(backend, 0, Array.Empty<ProcedureItem>(), source,
            backend == "G2" ? message + " Proto neobsahuje ListG2Procedures; klient používa REST katalóg uvedený v README. Ak nie je dostupný, použite ručný presný názov alebo dodajte autoritatívny katalóg." : message);
        if (backend is not ("G1" or "G2" or "CRG2")) return Failure("Neznámy backend.");
        var targetHost = host ?? configuration["Oberon:Host"] ?? "192.168.16.169";
        if (targetHost.Length == 0 || targetHost != targetHost.Trim() ||
            Uri.CheckHostName(targetHost) == UriHostNameType.Unknown ||
            targetHost.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            return Failure("Host musí byť samostatné DNS meno alebo IP adresa, nie URL.");
        if (port is <= 0 or > 65535) return Failure("Port musí byť v rozsahu 1–65535.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = double.TryParse(configuration["Oberon:CatalogTimeoutSeconds"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configuredTimeout)
            && configuredTimeout is >= 0.01 and <= 300 ? configuredTimeout : 90;
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            if (backend != "G2")
            {
                var grpcPort = port ?? 21008;
                if (!port.HasValue && configuration["Oberon:Port"] is { } configuredGrpcPort &&
                    !int.TryParse(configuredGrpcPort, out grpcPort)) return Failure("Oberon:Port nie je platný port.");
                if (grpcPort is <= 0 or > 65535) return Failure("gRPC port musí byť v rozsahu 1–65535.");
                var json = await explorer.InvokeAsync("List" + backend + "Procedures", "{}",
                    username, password, hostOverride: targetHost, portOverride: grpcPort, cancellationToken: deadline.Token);
                return Parse(backend, json) with { Source = source };
            }
            var configuredRestPort = configuration["Oberon:RestPort"];
            var restPort = 21009;
            if (configuredRestPort is not null)
            {
                if (!int.TryParse(configuredRestPort, out restPort)) return Failure("Oberon:RestPort nie je platný port.");
            }
            else if (port.HasValue) restPort = port.Value + 1;
            if (restPort is <= 0 or > 65535) return Failure("REST port musí byť v rozsahu 1–65535.");
            var uri = new UriBuilder(Uri.UriSchemeHttp, targetHost, restPort, "/api/g2/procedures").Uri;
            var headers = await explorer.GetSessionHeadersAsync(username, password, targetHost, port, deadline.Token);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return Failure($"Objavenie katalógu G2 zablokované: HTTP {(int)response.StatusCode} po pripojení gateway relácie.");
            const int maxBytes = 4 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) return Failure("Katalóg presahuje limit 4 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (content.Length + count > maxBytes) return Failure("Katalóg presahuje limit 4 MiB.");
                content.Write(buffer, 0, count);
            }
            var parsed = Parse(backend, System.Text.Encoding.UTF8.GetString(content.ToArray()));
            return parsed.Error is null ? parsed with { Source = source } : Failure(parsed.Error);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested) { return Failure($"Načítanie katalógu prekročilo limit {timeoutSeconds} sekúnd."); }
        catch (Grpc.Core.RpcException ex) { return Failure("Načítanie katalógu zlyhalo: gRPC " + ex.StatusCode + "."); }
        catch (Exception ex)
        {
            // Do not expose auth details, session metadata, or arbitrary response bodies.
            return Failure("Načítanie katalógu zlyhalo (" + ex.GetType().Name + ").");
        }
    }

    /// <summary>Parses an unfiltered, authoritative catalog. Errors fail closed: no partial items.</summary>
    public static ProcedureCatalogResult Parse(string backend, string json)
    {
        backend = (backend ?? "").ToUpperInvariant();
        var total = 0;
        try
        {
            if (backend is not ("G1" or "G2" or "CRG2")) throw new FormatException("Neznámy backend.");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Katalóg musí byť JSON objekt.");
            if (Field(root, "result") is not { ValueKind: JsonValueKind.True })
                throw new FormatException("Katalóg nehlási result=true.");
            if (Field(root, "total") is not { } totalField || totalField.ValueKind != JsonValueKind.Number ||
                !totalField.TryGetInt32(out total) || total < 0) throw new FormatException("Chýba platný počet total.");
            if (Field(root, "procedures") is not { ValueKind: JsonValueKind.Array } entries)
                throw new FormatException("Chýba pole procedures.");
            var items = new List<ProcedureItem>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new FormatException("Procedúra musí byť objekt.");
                var name = Text(entry, "name");
                if (!Identifier.IsMatch(name)) throw new FormatException("Chýba platný presný názov procedúry.");
                var iface = backend == "G1" ? Text(entry, "interface_name") : "";
                if (backend == "G1" && iface is not ("IOBERONService" or "IOBERONCashRegisterService" or "IOBERONLoyaltySystemService"))
                    throw new FormatException("Chýba alebo je neznáme rozhranie G1.");
                var invocation = backend != "G1" || iface == "IOBERONService" ? name : iface + "." + name;
                if (!identities.Add(invocation)) throw new FormatException("Duplicitný názov procedúry: " + invocation);
                var args = backend == "G1" && Field(entry, "args") is not null ? Text(entry, "args") : UnknownArguments;
                var returns = backend == "G1" && Field(entry, "returns") is not null ? Text(entry, "returns") : UnknownReturn;
                if (string.IsNullOrEmpty(returns)) returns = UnknownReturn;
                var procedureClass = Text(entry, "procedure_class");
                var method = Text(entry, "http_method");
                var template = Text(entry, "uri_template");
                var generic = Field(entry, "generic_callable");
                if (generic is { } g && g.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("generic_callable musí byť boolean.");
                // Missing protobuf bool is false; never infer callability from the name/class.
                var callable = generic is { ValueKind: JsonValueKind.True };
                var example = backend == "G1" ? Text(entry, "example_json") : "";
                if (example.Length > 0)
                {
                    using var exampleDoc = JsonDocument.Parse(example);
                    if (exampleDoc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new FormatException("example_json musí obsahovať JSON objekt.");
                }
                var description = $"Backend: {backend}. Rozhranie: {(iface.Length > 0 ? iface : "katalóg ho neuvádza")}. " +
                    $"Trieda: {(procedureClass.Length > 0 ? procedureClass : "neznáma")}. " +
                    $"Argumenty: {(args.Length > 0 ? args : "bez argumentov")}. Návratový typ: {returns}. " +
                    (example.Length > 0 ? "Katalóg poskytuje vzor JSON, nie úplnú validačnú schému." : "Schéma/vzor JSON nie je v katalógu dostupný.");
                items.Add(new(backend, name, invocation, "Call" + backend + "Procedure", description,
                    procedureClass, method, template, args, returns, example, callable, example.Length > 0));
            }
            if (total != items.Count) throw new FormatException($"Neúplný katalóg: total={total}, procedures={items.Count}.");
            return new(backend, total, items.AsReadOnly(), "JSON catalog", null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new(backend, Math.Max(0, total), Array.Empty<ProcedureItem>(), "JSON catalog", "Neplatný katalóg: " + ex.Message);
        }
    }

    public static string CreateRequest(ProcedureItem item, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.GenericCallable) throw new InvalidOperationException("Procedúra nie je generic_callable; reláciu spravuje gateway.");
        if (item.Backend is not ("G1" or "G2" or "CRG2") || item.RpcName != "Call" + item.Backend + "Procedure" || !Identifier.IsMatch(item.Name))
            throw new ArgumentException("Neplatná identita procedúry/RPC.");
        var validInvocation = item.InvocationName == item.Name;
        if (item.Backend == "G1")
            validInvocation |= item.InvocationName == "IOBERONCashRegisterService." + item.Name ||
                item.InvocationName == "IOBERONLoyaltySystemService." + item.Name;
        if (!validInvocation) throw new ArgumentException("Neplatný kvalifikovaný názov procedúry.");
        using var payload = JsonDocument.Parse(payloadJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Payload musí byť JSON objekt.");
        return JsonSerializer.Serialize(new { procedure = item.InvocationName, jsonPayload = payload.RootElement.GetRawText() });
    }

    private static string Text(JsonElement element, string name)
    {
        if (Field(element, name) is not { } value) return "";
        if (value.ValueKind != JsonValueKind.String) throw new FormatException(name + " musí byť string.");
        return value.GetString()!;
    }

    private static JsonElement? Field(JsonElement element, string name)
    {
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Replace("_", "").Equals(name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null) throw new FormatException("Duplicitné pole: " + name);
                found = property.Value;
            }
        return found;
    }
}
