using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OtpNet;

namespace EnterprisePulse.Core.Modules.Identity;

public sealed record LoginRequest(string Email, string Password, string? Code);

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime RefreshExpiresAt);

public sealed record LoginResult(bool RequiresTwoFactor, TokenPair? Tokens);

public sealed record MeCompany(Guid Id, string Code, string Name, string BaseCurrency, bool IsDefault, List<MeBranch> Branches);

public sealed record MeBranch(Guid Id, string Code, string Name);

public sealed record MeUser(Guid Id, string Email, string FirstName, string LastName, string? Phone, string Locale, bool IsSuperAdmin, bool TwoFactorEnabled, Guid? EmployeeId);

public sealed record MeResponse(MeUser User, List<MeCompany> Companies, Guid ActiveCompanyId, Guid? ActiveBranchId, List<string> Permissions);

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

/// <summary>Resolves the company context and permission codes of a user (cached for a minute).</summary>
public sealed class PermissionResolver(AppDbContext db, IMemoryCache cache)
{
    public static string CacheKey(Guid userId, Guid companyId) => $"perm:{userId}:{companyId}";

    public async Task<HashSet<string>> ResolveAsync(Guid userId, Guid companyId) =>
        (await cache.GetOrCreateAsync(CacheKey(userId, companyId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var codes = await db.UserRoles.IgnoreQueryFilters()
                .Where(ur => ur.UserId == userId && ur.CompanyId == companyId && ur.Role.DeletedAt == null)
                .SelectMany(ur => ur.Role.Permissions.Select(rp => rp.Permission.Code))
                .Distinct()
                .ToListAsync();
            return codes.ToHashSet(StringComparer.Ordinal);
        }))!;

    public void Invalidate() => (cache as MemoryCache)?.Clear();
}

public sealed class AuthService(
    AppDbContext db,
    TokenService tokens,
    PermissionResolver permissions,
    MailQueue mail,
    IOptions<AppOptions> options)
{
    private readonly AppOptions _o = options.Value;
    private CurrentUser Ctx => db.User;

    public async Task<LoginResult> LoginAsync(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || !user.IsActive) throw new UnauthorizedAppException("E-posta veya şifre hatalı");
        if (user.LockedUntil > DateTime.UtcNow)
        {
            throw new UnauthorizedAppException($"Hesap kilitli. {user.LockedUntil.Value.ToLocalTime():HH:mm} sonrasında tekrar deneyin");
        }
        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _o.MaxFailedLogins)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(_o.LockMinutes);
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync();
            throw new UnauthorizedAppException("E-posta veya şifre hatalı");
        }
        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code)) return new LoginResult(true, null);
            if (!VerifyTotp(user.TwoFactorSecret!, request.Code)) throw new UnauthorizedAppException("Doğrulama kodu hatalı");
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        var pair = await IssueAsync(user, Guid.NewGuid());
        return new LoginResult(false, pair);
    }

    /// <summary>Rotates the refresh token. Reusing an already rotated token revokes the whole family.</summary>
    public async Task<TokenPair> RefreshAsync(string refreshToken)
    {
        var hash = TokenService.Sha256(refreshToken);
        var row = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash)
            ?? throw new UnauthorizedAppException("Oturum süresi doldu");
        if (row.RevokedAt != null)
        {
            await db.RefreshTokens.Where(t => t.FamilyId == row.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
            throw new UnauthorizedAppException("Oturum güvenlik nedeniyle sonlandırıldı");
        }
        if (row.ExpiresAt < DateTime.UtcNow || !row.User.IsActive || row.User.DeletedAt != null)
        {
            throw new UnauthorizedAppException("Oturum süresi doldu");
        }
        row.RevokedAt = DateTime.UtcNow;
        var pair = await IssueAsync(row.User, row.FamilyId);
        row.ReplacedById = (await db.RefreshTokens.FirstAsync(t => t.TokenHash == TokenService.Sha256(pair.RefreshToken))).Id;
        await db.SaveChangesAsync();
        return pair;
    }

    public async Task LogoutAsync(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        var hash = TokenService.Sha256(refreshToken);
        var row = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (row == null) return;
        await db.RefreshTokens.Where(t => t.FamilyId == row.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
    }

    private async Task<TokenPair> IssueAsync(User user, Guid familyId)
    {
        var refresh = TokenService.NewOpaqueToken();
        var expires = DateTime.UtcNow.AddDays(_o.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.Sha256(refresh),
            FamilyId = familyId,
            ExpiresAt = expires,
            Ip = Ctx.Ip,
            UserAgent = Ctx.UserAgent,
        });
        await db.SaveChangesAsync();
        return new TokenPair(tokens.CreateAccessToken(user), refresh, expires);
    }

    /// <summary>
    /// Picks the active company: the requested one if the user belongs to it (super admins may open any),
    /// otherwise the default membership.
    /// </summary>
    public async Task<(Guid CompanyId, Guid? BranchId)> ResolveCompanyAsync(Guid userId, bool isSuperAdmin, Guid? requested, Guid? requestedBranch)
    {
        var memberships = await db.UserCompanies.IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.Company.IsActive && m.Company.DeletedAt == null)
            .Select(m => new { m.CompanyId, m.IsDefault, m.DefaultBranchId })
            .ToListAsync();
        Guid companyId;
        Guid? branchId;
        if (requested != null && (memberships.Any(m => m.CompanyId == requested) ||
            (isSuperAdmin && await db.Companies.AnyAsync(c => c.Id == requested && c.IsActive))))
        {
            companyId = requested.Value;
            branchId = memberships.FirstOrDefault(m => m.CompanyId == requested)?.DefaultBranchId;
        }
        else
        {
            var chosen = memberships.OrderByDescending(m => m.IsDefault).FirstOrDefault();
            if (chosen == null && isSuperAdmin)
            {
                companyId = await db.Companies.Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => c.Id).FirstAsync();
                branchId = null;
            }
            else
            {
                companyId = chosen?.CompanyId ?? throw new ForbiddenAppException("Kullanıcı hiçbir şirkete bağlı değil");
                branchId = chosen.DefaultBranchId;
            }
        }
        if (requestedBranch != null && await db.Branches.IgnoreQueryFilters().AnyAsync(b => b.Id == requestedBranch && b.CompanyId == companyId && b.DeletedAt == null))
        {
            branchId = requestedBranch;
        }
        return (companyId, branchId);
    }

    public async Task<MeResponse> MeAsync()
    {
        var userId = Ctx.RequireUserId();
        var companyId = Ctx.RequireCompanyId();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        List<MeCompany> companies;
        using (Ctx.WithoutTenant())
        {
            var companyQuery = user.IsSuperAdmin
                ? db.Companies.Where(c => c.IsActive)
                : db.Companies.Where(c => c.IsActive && c.Memberships.Any(m => m.UserId == userId));
            var defaultId = await db.UserCompanies.Where(m => m.UserId == userId && m.IsDefault).Select(m => (Guid?)m.CompanyId).FirstOrDefaultAsync();
            companies = await companyQuery.OrderBy(c => c.Name)
                .Select(c => new MeCompany(c.Id, c.Code, c.Name, c.BaseCurrency, c.Id == defaultId,
                    c.Branches.Where(b => b.IsActive && b.DeletedAt == null).OrderBy(b => b.Name).Select(b => new MeBranch(b.Id, b.Code, b.Name)).ToList()))
                .ToListAsync();
        }
        var employeeId = await db.Employees.Where(e => e.UserId == userId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        var codes = user.IsSuperAdmin
            ? await db.Permissions.OrderBy(p => p.Code).Select(p => p.Code).ToListAsync()
            : (await permissions.ResolveAsync(userId, companyId)).OrderBy(c => c).ToList();
        return new MeResponse(
            new MeUser(user.Id, user.Email, user.FirstName, user.LastName, user.Phone, user.Locale, user.IsSuperAdmin, user.TwoFactorEnabled, employeeId),
            companies, companyId, Ctx.BranchId, codes);
    }

    public async Task UpdateProfileAsync(string firstName, string lastName, string? phone, string locale)
    {
        var user = await db.Users.FirstAsync(u => u.Id == Ctx.RequireUserId());
        Guard.That(!string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName), "Ad ve soyad zorunludur");
        user.FirstName = firstName.Trim();
        user.LastName = lastName.Trim();
        user.Phone = phone;
        user.Locale = locale is "en" ? "en" : "tr";
        await db.SaveChangesAsync();
    }

    public async Task ChangePasswordAsync(string current, string next)
    {
        var user = await db.Users.FirstAsync(u => u.Id == Ctx.RequireUserId());
        if (!PasswordHasher.Verify(current, user.PasswordHash)) throw new BadRequestAppException("Mevcut şifre hatalı");
        PasswordHasher.AssertStrong(next);
        user.PasswordHash = PasswordHasher.Hash(next);
        await db.SaveChangesAsync();
    }

    public async Task ForgotPasswordAsync(string email)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email.Trim().ToLower() && u.IsActive);
        if (user == null) return; // never reveal whether the address exists
        var token = TokenService.NewOpaqueToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = TokenService.Sha256(token),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        var link = $"{_o.WebAppUrl}/reset-password?token={Uri.EscapeDataString(token)}";
        mail.Send(user.Email, "Şifre sıfırlama", $"<p>Şifrenizi sıfırlamak için bağlantıyı açın (1 saat geçerli):</p><p><a href=\"{link}\">{link}</a></p>");
    }

    public async Task ResetPasswordAsync(string token, string password)
    {
        var hash = TokenService.Sha256(token);
        var row = await db.PasswordResetTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (row == null || row.UsedAt != null || row.ExpiresAt < DateTime.UtcNow) throw new BadRequestAppException("Bağlantı geçersiz veya süresi dolmuş");
        PasswordHasher.AssertStrong(password);
        row.UsedAt = DateTime.UtcNow;
        row.User.PasswordHash = PasswordHasher.Hash(password);
        row.User.FailedLoginCount = 0;
        row.User.LockedUntil = null;
        await db.RefreshTokens.Where(t => t.UserId == row.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    public async Task<TwoFactorSetup> SetupTwoFactorAsync()
    {
        var user = await db.Users.FirstAsync(u => u.Id == Ctx.RequireUserId());
        Guard.State(!user.TwoFactorEnabled, "İki adımlı doğrulama zaten açık");
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        user.TwoFactorSecret = secret;
        await db.SaveChangesAsync();
        var uri = $"otpauth://totp/EnterprisePulse:{Uri.EscapeDataString(user.Email)}?secret={secret}&issuer=EnterprisePulse";
        return new TwoFactorSetup(secret, uri);
    }

    public async Task EnableTwoFactorAsync(string code)
    {
        var user = await db.Users.FirstAsync(u => u.Id == Ctx.RequireUserId());
        Guard.State(user.TwoFactorSecret != null, "Önce kurulum başlatın");
        if (!VerifyTotp(user.TwoFactorSecret!, code)) throw new BadRequestAppException("Doğrulama kodu hatalı");
        user.TwoFactorEnabled = true;
        await db.SaveChangesAsync();
    }

    public async Task DisableTwoFactorAsync(string password)
    {
        var user = await db.Users.FirstAsync(u => u.Id == Ctx.RequireUserId());
        if (!PasswordHasher.Verify(password, user.PasswordHash)) throw new BadRequestAppException("Şifre hatalı");
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        await db.SaveChangesAsync();
    }

    public static bool VerifyTotp(string secret, string code) =>
        new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));
}
