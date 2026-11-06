using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EnterprisePulse.Core.Modules.Identity;

public sealed record CreateUserRequest(
    string Email,
    string FirstName,
    string LastName,
    string? Phone,
    string? Password,
    List<Guid> RoleIds,
    Guid? BranchId,
    bool IsSuperAdmin = false);

public sealed record UserRolesRequest(List<Guid> RoleIds, Guid? BranchId);

public sealed record RoleRequest(string Code, string Name, string? Description, List<string> Permissions);

public sealed record RoleDetail(Guid Id, Guid? CompanyId, string Code, string Name, string? Description, bool IsSystem, List<string> Permissions, int UserCount);

public sealed class UsersService(AppDbContext db, PermissionResolver permissions, MailQueue mail, IOptions<AppOptions> options)
{
    private CurrentUser Ctx => db.User;

    /// <summary>Creates the user or, if the e-mail already exists, adds the existing user to this company.</summary>
    public async Task<User> CreateAsync(CreateUserRequest request)
    {
        Ctx.Require("users:create");
        var companyId = Ctx.RequireCompanyId();
        var email = request.Email.Trim().ToLowerInvariant();
        Guard.That(email.Contains('@'), "Geçerli bir e-posta girin");
        Guard.That(!request.IsSuperAdmin || Ctx.IsSuperAdmin, "Süper yönetici yalnızca süper yönetici tarafından atanabilir");

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email);
        var invite = string.IsNullOrEmpty(request.Password);
        if (user == null)
        {
            if (!invite) PasswordHasher.AssertStrong(request.Password!);
            user = new User
            {
                Email = email,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Phone = request.Phone,
                IsSuperAdmin = request.IsSuperAdmin,
                PasswordHash = PasswordHasher.Hash(invite ? TokenService.NewOpaqueToken() : request.Password!),
            };
            db.Users.Add(user);
        }
        else
        {
            Guard.State(user.DeletedAt == null, "Bu e-posta silinmiş bir kullanıcıya ait");
            var member = await db.UserCompanies.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id && m.CompanyId == companyId);
            Guard.State(!member, "Kullanıcı bu şirkette zaten tanımlı");
            invite = false;
        }

        var hasDefault = await db.UserCompanies.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id && m.IsDefault);
        db.UserCompanies.Add(new UserCompany { UserId = user.Id, CompanyId = companyId, IsDefault = !hasDefault, DefaultBranchId = request.BranchId });
        await SetRolesAsync(user.Id, companyId, request.RoleIds);
        await db.SaveChangesAsync();

        if (invite)
        {
            var token = TokenService.NewOpaqueToken();
            db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = TokenService.Sha256(token), ExpiresAt = DateTime.UtcNow.AddDays(3) });
            await db.SaveChangesAsync();
            var link = $"{options.Value.WebAppUrl}/reset-password?token={Uri.EscapeDataString(token)}";
            mail.Send(user.Email, "EnterprisePulse davetiyesi",
                $"<p>Merhaba {System.Net.WebUtility.HtmlEncode(user.FirstName)},</p><p>Hesabınız oluşturuldu. Şifrenizi belirlemek için bağlantıyı açın (3 gün geçerli):</p><p><a href=\"{link}\">{link}</a></p>");
        }
        return user;
    }

    public async Task<List<Guid>> RoleIdsAsync(Guid userId)
    {
        var companyId = Ctx.RequireCompanyId();
        return await db.UserRoles.Where(ur => ur.UserId == userId && ur.CompanyId == companyId).Select(ur => ur.RoleId).ToListAsync();
    }

    public async Task UpdateRolesAsync(Guid userId, UserRolesRequest request)
    {
        Ctx.Require("users:update");
        var companyId = Ctx.RequireCompanyId();
        var membership = await db.UserCompanies.FirstOrDefaultAsync(m => m.UserId == userId && m.CompanyId == companyId)
            ?? throw new NotFoundAppException("Kullanıcı bu şirkette değil");
        membership.DefaultBranchId = request.BranchId;
        await SetRolesAsync(userId, companyId, request.RoleIds);
        await db.SaveChangesAsync();
        permissions.Invalidate();
    }

    /// <summary>Removes the user from the active company; the account itself stays for other companies.</summary>
    public async Task RemoveFromCompanyAsync(Guid userId)
    {
        Ctx.Require("users:delete");
        Guard.State(userId != Ctx.UserId, "Kendinizi şirketten çıkaramazsınız");
        var companyId = Ctx.RequireCompanyId();
        await db.UserRoles.Where(ur => ur.UserId == userId && ur.CompanyId == companyId).ExecuteDeleteAsync();
        await db.UserCompanies.Where(m => m.UserId == userId && m.CompanyId == companyId).ExecuteDeleteAsync();
        permissions.Invalidate();
    }

    private async Task SetRolesAsync(Guid userId, Guid companyId, List<Guid> roleIds)
    {
        var valid = await db.Roles.Where(r => roleIds.Contains(r.Id) && (r.CompanyId == companyId || r.CompanyId == null)).Select(r => r.Id).ToListAsync();
        Guard.That(valid.Count == roleIds.Distinct().Count(), "Geçersiz rol seçimi");
        var existing = await db.UserRoles.Where(ur => ur.UserId == userId && ur.CompanyId == companyId).ToListAsync();
        db.UserRoles.RemoveRange(existing.Where(ur => !valid.Contains(ur.RoleId)));
        foreach (var roleId in valid.Where(id => existing.All(ur => ur.RoleId != id)))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, CompanyId = companyId });
        }
    }

    // ---------- roles ----------

    public async Task<List<RoleDetail>> RolesAsync()
    {
        var companyId = Ctx.RequireCompanyId();
        return await db.Roles.Where(r => r.CompanyId == companyId || r.CompanyId == null).OrderBy(r => r.Name)
            .Select(r => new RoleDetail(r.Id, r.CompanyId, r.Code, r.Name, r.Description, r.IsSystem,
                r.Permissions.Select(p => p.Permission.Code).OrderBy(c => c).ToList(),
                r.UserRoles.Count(ur => ur.CompanyId == companyId)))
            .ToListAsync();
    }

    public async Task<RoleDetail> SaveRoleAsync(Guid? id, RoleRequest request)
    {
        Ctx.Require(id == null ? "roles:create" : "roles:update");
        var companyId = Ctx.RequireCompanyId();
        Role role;
        if (id == null)
        {
            Guard.That(!string.IsNullOrWhiteSpace(request.Code) && !string.IsNullOrWhiteSpace(request.Name), "Kod ve ad zorunludur");
            Guard.State(!await db.Roles.AnyAsync(r => r.CompanyId == companyId && r.Code == request.Code), "Bu kodda bir rol var");
            role = new Role { CompanyId = companyId, Code = request.Code.Trim(), Name = request.Name.Trim() };
            db.Roles.Add(role);
        }
        else
        {
            role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
                ?? throw new NotFoundAppException("Rol bulunamadı");
            Guard.State(!role.IsSystem, "Sistem rolü değiştirilemez");
            role.Name = request.Name.Trim();
        }
        role.Description = request.Description;
        var wanted = await db.Permissions.Where(p => request.Permissions.Contains(p.Code)).ToListAsync();
        db.RolePermissions.RemoveRange(role.Permissions.Where(rp => wanted.All(p => p.Id != rp.PermissionId)));
        foreach (var p in wanted.Where(p => role.Permissions.All(rp => rp.PermissionId != p.Id)))
        {
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = p.Id });
        }
        await db.SaveChangesAsync();
        permissions.Invalidate();
        return (await RolesAsync()).First(r => r.Id == role.Id);
    }

    public async Task DeleteRoleAsync(Guid id)
    {
        Ctx.Require("roles:delete");
        var companyId = Ctx.RequireCompanyId();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId) ?? throw new NotFoundAppException("Rol bulunamadı");
        Guard.State(!role.IsSystem, "Sistem rolü silinemez");
        Guard.State(!await db.UserRoles.AnyAsync(ur => ur.RoleId == id), "Kullanıcıya atanmış rol silinemez");
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
    }
}

/// <summary>Users are global accounts; lists show only the members of the active company.</summary>
public sealed class UsersHook(CurrentUser ctx) : IResourceHook
{
    public string Resource => "users";

    public IQueryable<T> Scope<T>(IQueryable<T> query) where T : class
    {
        var companyId = ctx.CompanyId;
        return ((IQueryable<User>)query).Where(u => u.Memberships.Any(m => m.CompanyId == companyId)).Cast<T>();
    }

    public Task BeforeDeleteAsync(object entity)
    {
        Guard.State(((User)entity).Id != ctx.UserId, "Kendi hesabınızı silemezsiniz");
        Guard.State(ctx.IsSuperAdmin, "Hesabı tamamen silmek yalnızca süper yöneticiye açıktır; kullanıcıyı şirketten çıkarın");
        return Task.CompletedTask;
    }
}

public sealed class RolesHook(CurrentUser ctx) : IResourceHook
{
    public string Resource => "roles";

    public IQueryable<T> Scope<T>(IQueryable<T> query) where T : class
    {
        var companyId = ctx.CompanyId;
        return ((IQueryable<Role>)query).Where(r => r.CompanyId == companyId || r.CompanyId == null).Cast<T>();
    }
}

/// <summary>Non super admins only see the companies they belong to.</summary>
public sealed class CompaniesHook(CurrentUser ctx) : IResourceHook
{
    public string Resource => "companies";

    public IQueryable<T> Scope<T>(IQueryable<T> query) where T : class
    {
        if (ctx.IsSuperAdmin) return query;
        var userId = ctx.UserId;
        return ((IQueryable<Company>)query).Where(c => c.Memberships.Any(m => m.UserId == userId)).Cast<T>();
    }
}
