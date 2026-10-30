using System.Text;
using OberonProtoExplorer.Components;
using OberonProtoExplorer.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = WebApplication.CreateBuilder(args);
var envPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".env"));
if (File.Exists(envPath))
{
    var values = new Dictionary<string, string?>();
    foreach (var line in File.ReadLines(envPath))
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) continue;
        var split = text.IndexOf('=');
        if (split <= 0) continue;
        var key = text[..split].Trim();
        var value = text[(split + 1)..].Trim().Trim('"', '\'');
        values[key switch
        {
            "OBERON_HOST" => "Oberon:Host",
            "OBERON_GRPC_PORT" => "Oberon:Port",
            "OBERON_REST_PORT" => "Oberon:RestPort",
            "OBERON_USERNAME" => "Oberon:Username",
            "OBERON_PASSWORD" => "Oberon:Password",
            _ => key
        }] = value;
    }
    builder.Configuration.AddInMemoryCollection(values);
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<GrpcExplorer>();
builder.Services.AddSingleton<RpcSchemaCatalog>();
builder.Services.AddSingleton<ClientGuideCatalog>();

builder.Services.AddScoped<ProcedureCatalog>();
var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
