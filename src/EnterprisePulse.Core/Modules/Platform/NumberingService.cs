using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Platform;

public sealed class NumberingService(AppDbContext db)
{
    private sealed record Row(string Prefix, int Padding, bool IncludeYear, int Value);

    /// <summary>formatNumber("ZMT", 6, true, 2026, 42) == "ZMT-2026-000042"</summary>
    public static string Format(string prefix, int padding, bool includeYear, int year, int value)
    {
        var sequence = value.ToString().PadLeft(padding, '0');
        return includeYear ? $"{prefix}-{year}-{sequence}" : $"{prefix}-{sequence}";
    }

    /// <summary>
    /// Reserves the next number with a single atomic UPDATE ... RETURNING, so concurrent requests never
    /// receive the same number. Runs inside the caller's transaction when there is one.
    /// </summary>
    public async Task<string> NextAsync(string code, Guid? companyId = null)
    {
        var company = companyId ?? db.User.RequireCompanyId();
        var year = DateTime.UtcNow.Year;
        var rows = await db.Database.SqlQuery<Row>($"""
            UPDATE number_sequences
               SET next_value = CASE WHEN reset_yearly AND current_year <> {year} THEN 2 ELSE next_value + 1 END,
                   current_year = {year},
                   updated_at = now()
             WHERE company_id = {company} AND code = {code}
            RETURNING prefix AS "Prefix", padding AS "Padding", include_year AS "IncludeYear", next_value - 1 AS "Value"
            """).ToListAsync();
        var row = rows.FirstOrDefault() ?? throw new NotFoundAppException($"Numara serisi tanımlı değil: {code}");
        return Format(row.Prefix, row.Padding, row.IncludeYear, year, row.Value);
    }

    public static string Preview(NumberSequence s)
    {
        var year = DateTime.UtcNow.Year;
        var next = s.ResetYearly && s.CurrentYear != year ? 1 : s.NextValue;
        return Format(s.Prefix, s.Padding, s.IncludeYear, year, next);
    }
}
