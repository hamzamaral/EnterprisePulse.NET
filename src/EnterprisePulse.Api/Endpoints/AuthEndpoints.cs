using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Modules.Identity;

namespace EnterprisePulse.Api.Endpoints;

public sealed record RefreshRequest(string? RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string Password);
public sealed record ProfileRequest(string FirstName, string LastName, string? Phone, string Locale);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CodeRequest(string Code);
public sealed record PasswordRequest(string Password);

public static class AuthEndpoints
{
    private const string Cookie = "refresh_token";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest body, AuthService auth, HttpResponse res) =>
        {
            var result = await auth.LoginAsync(body);
            if (result.Tokens != null) SetCookie(res, result.Tokens);
            return Results.Ok(new { result.RequiresTwoFactor, result.Tokens?.AccessToken, result.Tokens?.RefreshToken, result.Tokens?.RefreshExpiresAt });
        }).RequireRateLimiting("auth");

        g.MapPost("/refresh", async (RefreshRequest? body, AuthService auth, HttpRequest req, HttpResponse res) =>
        {
            var token = body?.RefreshToken ?? req.Cookies[Cookie] ?? throw new UnauthorizedAppException("Oturum süresi doldu");
            var pair = await auth.RefreshAsync(token);
            SetCookie(res, pair);
            return Results.Ok(pair);
        });

        g.MapPost("/logout", async (RefreshRequest? body, AuthService auth, HttpRequest req, HttpResponse res) =>
        {
            await auth.LogoutAsync(body?.RefreshToken ?? req.Cookies[Cookie]);
            res.Cookies.Delete(Cookie, new CookieOptions { Path = "/api/auth" });
            return Results.NoContent();
        });

        g.MapPost("/forgot-password", async (ForgotPasswordRequest body, AuthService auth) =>
        {
            await auth.ForgotPasswordAsync(body.Email);
            return Results.Ok(new { message = "Hesap varsa sıfırlama bağlantısı e-posta ile gönderildi" });
        }).RequireRateLimiting("auth");

        g.MapPost("/reset-password", async (ResetPasswordRequest body, AuthService auth) =>
        {
            await auth.ResetPasswordAsync(body.Token, body.Password);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var me = g.MapGroup("").RequireAuthorization();
        me.MapGet("/me", (AuthService auth) => auth.MeAsync());
        me.MapPatch("/profile", async (ProfileRequest body, AuthService auth) =>
        {
            await auth.UpdateProfileAsync(body.FirstName, body.LastName, body.Phone, body.Locale);
            return Results.NoContent();
        });
        me.MapPost("/change-password", async (ChangePasswordRequest body, AuthService auth) =>
        {
            await auth.ChangePasswordAsync(body.CurrentPassword, body.NewPassword);
            return Results.NoContent();
        });
        me.MapPost("/2fa/setup", (AuthService auth) => auth.SetupTwoFactorAsync());
        me.MapPost("/2fa/enable", async (CodeRequest body, AuthService auth) =>
        {
            await auth.EnableTwoFactorAsync(body.Code);
            return Results.NoContent();
        });
        me.MapPost("/2fa/disable", async (PasswordRequest body, AuthService auth) =>
        {
            await auth.DisableTwoFactorAsync(body.Password);
            return Results.NoContent();
        });
    }

    private static void SetCookie(HttpResponse res, TokenPair pair) =>
        res.Cookies.Append(Cookie, pair.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/api/auth",
            Expires = pair.RefreshExpiresAt,
        });
}
