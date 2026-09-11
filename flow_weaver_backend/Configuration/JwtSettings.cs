namespace flow_weaver_backend.Configuration;

public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpirationDays { get; set; } = 7;
}