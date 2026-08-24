namespace Fixlosophy.Services;

/// <summary>
/// Resolves site photography to its public Supabase storage URL. The photos live in
/// the public "Fixlosophy_Website_Images" bucket rather than wwwroot, so they can be
/// swapped without a redeploy. Keeping the base URL in configuration means the
/// project ref appears in exactly one place and can be pointed elsewhere per
/// environment.
/// </summary>
public sealed class SiteImages(IConfiguration config)
{
    private readonly string _base = (config["SiteImages:BaseUrl"] ?? "").TrimEnd('/');

    public string Url(string path) => $"{_base}/{path.TrimStart('/')}";
}
