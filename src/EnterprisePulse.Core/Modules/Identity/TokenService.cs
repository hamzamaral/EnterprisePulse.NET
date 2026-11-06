using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EnterprisePulse.Core.Modules.Identity;

public sealed class TokenService(IOptions<AppOptions> options)
{
    private readonly AppOptions _o = options.Value;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(_o.JwtSecret));

    public string CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", $"{user.FirstName} {user.LastName}"),
        };
        var token = new JwtSecurityToken(
            issuer: _o.JwtIssuer,
            audience: _o.JwtIssuer,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string NewOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = _o.JwtIssuer,
        ValidAudience = _o.JwtIssuer,
        IssuerSigningKey = SigningKey,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
}
