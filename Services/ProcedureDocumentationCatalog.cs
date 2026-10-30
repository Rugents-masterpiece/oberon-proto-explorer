using System.Text.Json;

namespace OberonProtoExplorer.Services;

public sealed record SourceField(string Path, string Type, string Description, bool? Required);
public sealed record SourceProcedure(string Backend, string Name, string InvocationName, string Description,
    string Source, string HttpMethod, string Path, IReadOnlyList<SourceField> RequestFields,
    IReadOnlyList<SourceField> ResponseFields, IReadOnlyList<string> Notes)
{
    public string Key => Backend + ":" + InvocationName;
}

// Documentation only: never changes runtime catalogs, request validation or authorization.
public sealed class ProcedureDocumentationCatalog
{
    public IReadOnlyList<SourceProcedure> Entries { get; }
    public ProcedureDocumentationCatalog(IWebHostEnvironment environment)
        : this(File.ReadAllText(System.IO.Path.Combine(environment.ContentRootPath, "Documentation", "procedure-docs.json"))) { }
    public ProcedureDocumentationCatalog(string json)
    {
        Entries = JsonSerializer.Deserialize<List<SourceProcedure>>(json, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true })?.AsReadOnly() ?? throw new FormatException("Dokumentácia musí byť pole.");
        if (Entries.Any(e => e.Backend is not ("G1" or "G2" or "CRG2") || string.IsNullOrWhiteSpace(e.InvocationName)) ||
            Entries.GroupBy(e => e.Key, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new FormatException("Dokumentácia má neplatnú alebo duplicitnú identitu.");
    }
    public SourceProcedure? Find(string backend, string name) =>
        Entries.SingleOrDefault(e => e.Backend == backend && e.InvocationName.Equals(name, StringComparison.Ordinal));
    public static string BackendForRpc(string rpc) => rpc switch
    {
        "CallG1Procedure" or "ListG1Procedures" => "G1",
        "CallG2Procedure" => "G2",
        "CallCRG2Procedure" or "ListCRG2Procedures" => "CRG2",
        _ => ""
    };
}
