namespace Fixlosophy.Services;

// Public Supabase Storage URLs for the logo/favicon, served from the dedicated
// public Fixlosophy_Website_Images bucket (a separate bucket, not a folder inside
// the private Fixlosophy_N1_Bucket — Supabase's /object/public/ route is gated by
// the bucket's own public flag, which can't be set per-folder). Hardcoded rather
// than read from IConfiguration: this base URL isn't a secret (visible in any
// browser's network tab regardless), and these assets render on every single page
// load, so they must not depend on Supabase:Url being configured the way
// StorageService's other methods do — a missing/blank config value would
// otherwise break every page.
public static class SupabaseImageUrls
{
    private const string Base =
        "https://dkwyccnsjbwsejrijard.supabase.co/storage/v1/object/public/Fixlosophy_Website_Images";

    public const string LogoAvif   = $"{Base}/logo.avif";
    public const string LogoSvg    = $"{Base}/logo.svg";
    public const string FaviconSvg = $"{Base}/favicon.svg";
    public const string FaviconPng = $"{Base}/favicon.png";
}
