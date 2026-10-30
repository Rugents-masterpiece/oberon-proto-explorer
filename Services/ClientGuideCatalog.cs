using System.Text.Json;

namespace OberonProtoExplorer.Services;

public sealed record GuideLanguage(string Slug, string Name);
public sealed record GuideStep(string Title, string Text, string Command);
public sealed record ClientGuide(string Slug, string Name, string Intro, string Prerequisites,
    List<GuideStep> Steps, string ExampleFile, List<string> Notes, string Example);

public sealed class ClientGuideCatalog(IWebHostEnvironment environment)
{
    public static IReadOnlyList<GuideLanguage> Languages { get; } = Array.AsReadOnly(new[]
    {
        new GuideLanguage("python", "Python"), new GuideLanguage("javascript", "JavaScript"),
        new GuideLanguage("csharp", "C#"), new GuideLanguage("java", "Java"), new GuideLanguage("go", "Go")
    });

    public async Task<ClientGuide?> GetAsync(string slug)
    {
        // Route parameters must never become arbitrary filesystem paths.
        if (!Languages.Any(language => language.Slug == slug)) return null;
        var path = Path.Combine(environment.ContentRootPath, "Guides", slug + ".json");
        using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ClientGuide>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
