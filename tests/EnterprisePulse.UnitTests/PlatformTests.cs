using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Modules.Identity;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Resources;
using OtpNet;

namespace EnterprisePulse.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_round_trips_and_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("Admin123!");
        Assert.StartsWith("$argon2id$", hash);
        Assert.True(PasswordHasher.Verify("Admin123!", hash));
        Assert.False(PasswordHasher.Verify("admin123!", hash));
    }

    [Theory]
    [InlineData("short1A")]
    [InlineData("alllowercase1")]
    [InlineData("ALLUPPERCASE1")]
    [InlineData("NoDigitsHere")]
    public void Weak_passwords_are_rejected(string password) =>
        Assert.Throws<BadRequestAppException>(() => PasswordHasher.AssertStrong(password));
}

public class NumberingTests
{
    [Fact]
    public void Formats_yearly_and_plain_series()
    {
        Assert.Equal("SAT-2026-000042", NumberingService.Format("SAT", 6, true, 2026, 42));
        Assert.Equal("DMB-000007", NumberingService.Format("DMB", 6, false, 2026, 7));
    }

    [Fact]
    public void Preview_shows_next_value() =>
        Assert.Equal("IZN-2026-000003", NumberingService.Preview(new NumberSequence { Prefix = "IZN", Padding = 6, IncludeYear = true, CurrentYear = 2026, NextValue = 3 }));
}

public class TotpTests
{
    [Fact]
    public void Accepts_current_code_and_rejects_garbage()
    {
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        Assert.True(AuthService.VerifyTotp(secret, code));
        Assert.False(AuthService.VerifyTotp(secret, "000000x"));
    }
}

public class MetadataTests
{
    private static readonly MetadataStore Meta = new();

    [Fact]
    public void Every_resource_maps_to_an_entity_type()
    {
        Assert.True(Meta.Resources.Count >= 140);
        Assert.All(Meta.Resources.Values, r => Assert.NotNull(r.ClrType));
        Assert.All(Meta.Resources.Values.SelectMany(r => r.Fields), f => Assert.NotNull(f.Property));
    }

    [Fact]
    public void Permissions_fall_back_to_module_update()
    {
        var leaveTypes = Meta.Get("leave-types");
        Assert.Equal("leave:read", leaveTypes.PermissionFor("read", Meta.Registry));
        Assert.Equal("leave:update", leaveTypes.PermissionFor("delete", Meta.Registry));
    }

    [Fact]
    public void Turkish_labels_exist_for_resources() =>
        Assert.DoesNotContain(Meta.Resources.Values, r => string.IsNullOrWhiteSpace(r.Label.Tr));

    [Fact]
    public void Converts_query_values()
    {
        Assert.Equal(new DateOnly(2026, 3, 1), ResourceService.Convert("2026-03-01", typeof(DateOnly)));
        Assert.Equal(12.5m, ResourceService.Convert("12.5", typeof(decimal)));
        Assert.Equal(WorkflowStatus.APPROVED, ResourceService.Convert("APPROVED", typeof(WorkflowStatus)));
        Assert.Null(ResourceService.Convert("null", typeof(int?)));
    }
}
