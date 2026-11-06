namespace EnterprisePulse.Core.Common;

public sealed class AppOptions
{
    public string JwtSecret { get; set; } = "dev-secret-change-me-dev-secret-change-me-0123456789";
    public string JwtIssuer { get; set; } = "enterprise-pulse";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
    public string WebAppUrl { get; set; } = "http://localhost:5101";
    public int MaxFailedLogins { get; set; } = 5;
    public int LockMinutes { get; set; } = 15;

    public string S3Endpoint { get; set; } = "http://localhost:9010";
    public string S3AccessKey { get; set; } = "erpadmin";
    public string S3SecretKey { get; set; } = "erpadmin123";
    public string S3Bucket { get; set; } = "enterprise-pulse";

    public string MailHost { get; set; } = "localhost";
    public int MailPort { get; set; } = 1035;
    public string MailFrom { get; set; } = "EnterprisePulse <no-reply@enterprisepulse.dev>";

    public bool SeedOnStartup { get; set; } = true;
    public string[] CorsOrigins { get; set; } = ["http://localhost:5101"];
}
