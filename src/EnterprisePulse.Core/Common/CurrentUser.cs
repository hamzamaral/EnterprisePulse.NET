namespace EnterprisePulse.Core.Common;

/// <summary>
/// Per-request identity and tenant. Filled by the API middleware; background jobs create their own
/// scope and either leave it empty (system) or call <see cref="ActAs"/>.
/// </summary>
public sealed class CurrentUser
{
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public bool IsSuperAdmin { get; set; }
    public HashSet<string> Permissions { get; set; } = new(StringComparer.Ordinal);
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Locale { get; set; } = "tr";

    /// <summary>When true the tenant query filter is off (login, cross-company jobs).</summary>
    public bool BypassTenant { get; set; }

    public Guid RequireUserId() => UserId ?? throw new UnauthorizedAppException();

    public Guid RequireCompanyId() => CompanyId ?? throw new BadRequestAppException("Aktif şirket seçilmedi");

    public bool Has(string code) => IsSuperAdmin || Permissions.Contains(code);

    public bool HasAny(params string[] codes) => IsSuperAdmin || codes.Any(Permissions.Contains);

    public void Require(string code)
    {
        if (!Has(code)) throw new ForbiddenAppException($"Missing permission: {code}");
    }

    /// <summary>Runs as a given company/user (jobs, seed); disposing restores the previous identity.</summary>
    public IDisposable ActAs(Guid companyId, Guid? userId = null, bool superAdmin = false)
    {
        var (company, user, admin, bypass) = (CompanyId, UserId, IsSuperAdmin, BypassTenant);
        CompanyId = companyId;
        UserId = userId;
        IsSuperAdmin = superAdmin;
        BypassTenant = false;
        return new Restore(() => (CompanyId, UserId, IsSuperAdmin, BypassTenant) = (company, user, admin, bypass));
    }

    public IDisposable WithoutTenant()
    {
        var previous = BypassTenant;
        BypassTenant = true;
        return new Restore(() => BypassTenant = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
