using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Einvoice;
using EnterprisePulse.Core.Modules.Production;

namespace EnterprisePulse.UnitTests;

public class AccountingMathTests
{
    [Fact]
    public void Normalizes_negative_amounts_and_merges_equal_lines()
    {
        var lines = AccountingMath.NormalizeLines(
        [
            new DraftLine("600", 0, 100, null, null, null),
            new DraftLine("120", 118, 0, null, null, null),
            new DraftLine("391", 0, 18, null, null, null),
            new DraftLine("600", 0, 50, null, null, null),
            new DraftLine("153", 0, -10, null, null, null),
            new DraftLine("100", 0, 0, null, null, null),
        ]);
        Assert.Equal(["120", "153", "600", "391"], lines.Select(l => l.Code));
        Assert.Equal(150m, lines.First(l => l.Code == "600").Credit);
        Assert.Equal(10m, lines.First(l => l.Code == "153").Debit);
        Assert.False(AccountingMath.IsBalanced(lines.Select(l => (l.Debit, l.Credit))));
    }

    [Fact]
    public void Balanced_entries_have_equal_nonzero_totals()
    {
        Assert.True(AccountingMath.IsBalanced([(100m, 0m), (0m, 100m)]));
        Assert.False(AccountingMath.IsBalanced([(100m, 0m), (0m, 90m)]));
        Assert.False(AccountingMath.IsBalanced([]));
    }

    [Fact]
    public void Chart_classifies_codes_by_class()
    {
        Assert.Equal(LedgerAccountType.ASSET, AccountingChart.TypeOf("120"));
        Assert.Equal(LedgerAccountType.LIABILITY, AccountingChart.TypeOf("320"));
        Assert.Equal(LedgerAccountType.EQUITY, AccountingChart.TypeOf("500"));
        Assert.Equal(LedgerAccountType.INCOME, AccountingChart.TypeOf("600"));
        Assert.Equal(LedgerAccountType.EXPENSE, AccountingChart.TypeOf("770"));
        Assert.True(AccountingChart.IsValidCode("120.01"));
        Assert.False(AccountingChart.IsValidCode("12"));
        Assert.False(AccountingChart.IsValidCode("820"));
    }
}

public class EInvoiceRulesTests
{
    [Fact]
    public void Validates_tax_ids_with_their_check_digits()
    {
        var vkn = EInvoiceRules.WithVknCheckDigit("1234567890");
        Assert.True(EInvoiceRules.IsValidVkn(vkn));
        Assert.False(EInvoiceRules.IsValidVkn(vkn[..9] + ((vkn[9] - '0' + 1) % 10)));
        Assert.True(EInvoiceRules.IsValidTckn("10000000146"));
        Assert.False(EInvoiceRules.IsValidTckn("10000000147"));
        Assert.True(EInvoiceRules.IsValidTaxId("10000000146"));
    }

    [Fact]
    public void Gib_number_drops_the_dashes_and_matches_the_format()
    {
        var number = EInvoiceRules.ToGibNumber("EPF-2026-000000042");
        Assert.Equal("EPF2026000000042", number);
        Assert.Matches(EInvoiceRules.GibNumber, number);
    }

    [Fact]
    public void Receivers_with_an_alias_get_e_fatura_the_others_e_arsiv()
    {
        Assert.Equal(EInvoiceProfile.TICARIFATURA, EInvoiceRules.ResolveProfile("urn:mail:x@y.com", null, EInvoiceProfile.TICARIFATURA));
        Assert.Null(EInvoiceRules.ResolveProfile("urn:mail:x@y.com", EInvoiceProfile.EARSIVFATURA, EInvoiceProfile.TICARIFATURA));
        Assert.Equal(EInvoiceProfile.EARSIVFATURA, EInvoiceRules.ResolveProfile(null, null, EInvoiceProfile.TICARIFATURA));
        Assert.Null(EInvoiceRules.ResolveProfile(null, EInvoiceProfile.TICARIFATURA, EInvoiceProfile.TICARIFATURA));
    }

    [Fact]
    public void Delivered_ticari_faturas_wait_for_the_receiver_for_eight_days()
    {
        var delivered = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(EInvoiceRules.CanRespond(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.DELIVERED, delivered, delivered.AddDays(7)));
        Assert.False(EInvoiceRules.CanRespond(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.DELIVERED, delivered, delivered.AddDays(9)));
        Assert.False(EInvoiceRules.CanRespond(EInvoiceProfile.EARSIVFATURA, EInvoiceStatus.DELIVERED, delivered, delivered));
        var step = EInvoiceRules.NextOutgoingStep(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.DELIVERED, delivered, () => [], delivered.AddDays(9));
        Assert.Equal(EInvoiceStatus.ACCEPTED, step!.Status);
    }

    [Fact]
    public void Sales_cancellation_depends_on_the_scenario_and_status()
    {
        Assert.True(EInvoiceRules.SalesCancelCheck(null, null).Allowed);
        Assert.False(EInvoiceRules.SalesCancelCheck(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.SENT).Allowed);
        Assert.False(EInvoiceRules.SalesCancelCheck(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.DELIVERED).Allowed);
        var archive = EInvoiceRules.SalesCancelCheck(EInvoiceProfile.EARSIVFATURA, EInvoiceStatus.DELIVERED);
        Assert.True(archive.Allowed);
        Assert.True(archive.CancelEInvoice);
        Assert.True(EInvoiceRules.SalesCancelCheck(EInvoiceProfile.TICARIFATURA, EInvoiceStatus.REJECTED).Allowed);
    }
}

public class ProductionMathTests
{
    [Fact]
    public void Scrap_raises_the_planned_use_per_unit()
    {
        // 5 kg coffee at 4% scrap for a batch of 10 packages.
        Assert.Equal(0.52m, ProductionMath.PerUnit(5, 4, 10));
        Assert.Equal(2m, ProductionMath.PerUnit(20, 0, 10));
        Assert.Equal(0.333333m, ProductionMath.PerUnit(1, 0, 3));
    }

    [Fact]
    public void Requirements_round_up_to_three_decimals()
    {
        Assert.Equal(1m, ProductionMath.Required(0.333333m, 3));
        Assert.Equal(0.334m, ProductionMath.Required(0.333333m, 1));
        Assert.Equal(5.2m, ProductionMath.Required(0.52m, 10));
    }

    [Fact]
    public void Labour_and_overhead_are_spread_over_the_batch()
    {
        Assert.Equal(30m, ProductionMath.CostPerUnit(300, 10));
        Assert.Equal(33.3333m, ProductionMath.CostPerUnit(100, 3));
    }

    [Fact]
    public void The_finishing_record_uses_up_the_remaining_requirement()
    {
        Assert.Equal(2m, ProductionMath.StandardConsumption(1, 5, 3, 2, finishes: false));
        Assert.Equal(2m, ProductionMath.StandardConsumption(1, 5, 3, 1, finishes: true));
        Assert.Equal(1m, ProductionMath.StandardConsumption(1, 5, 4, 3, finishes: false)); // never more than what is left
    }

    [Fact]
    public void Costs_roll_up_through_made_components()
    {
        var sub = new Guid("00000000-0000-0000-0000-000000000001");
        var part = new Guid("00000000-0000-0000-0000-000000000002");
        var subBom = new BomShape(1, 10, 5, [new BomLineShape(part, 2, 0)]);
        var costs = new Dictionary<Guid, decimal> { [part] = 100m };
        var rolled = ProductionMath.RollUpCost(subBom, id => costs[id]);
        Assert.Equal(200m, rolled.Material);
        Assert.Equal(215m, rolled.UnitCost);
        costs[sub] = rolled.UnitCost;
        var top = new BomShape(2, 20, 0, [new BomLineShape(sub, 1, 10)]);
        Assert.Equal(128.25m, ProductionMath.RollUpCost(top, id => costs[id]).UnitCost);
    }

    [Fact]
    public void Explosion_only_goes_deeper_for_the_shortage_of_made_components()
    {
        var kit = Guid.NewGuid();
        var station = Guid.NewGuid();
        var monitor = Guid.NewGuid();
        var boms = new Dictionary<Guid, BomShape>
        {
            [kit] = new(1, 0, 0, [new BomLineShape(station, 1, 0)]),
            [station] = new(1, 0, 0, [new BomLineShape(monitor, 1, 0)]),
        };
        var lines = ProductionMath.Explode(kit, 12, v => boms.GetValueOrDefault(v), new() { [station] = 5, [monitor] = 100 });
        Assert.Equal(2, lines.Count);
        Assert.Equal(7m, lines[0].Shortage);
        Assert.Equal(7m, lines[1].Quantity);
        Assert.Equal(2, lines[1].Level);
        Assert.Single(ProductionMath.Explode(kit, 4, v => boms.GetValueOrDefault(v), new() { [station] = 5 }));
    }

    [Fact]
    public void Cycles_and_netting_and_status_rules()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var graph = new Dictionary<Guid, List<Guid>> { [b] = [a] };
        Assert.True(ProductionMath.CreatesCycle(a, [b], v => graph.GetValueOrDefault(v) ?? []));
        Assert.False(ProductionMath.CreatesCycle(a, [Guid.NewGuid()], v => graph.GetValueOrDefault(v) ?? []));

        Assert.Equal(3m, ProductionMath.NetRequirement(10, 5, 2));
        Assert.Equal(0m, ProductionMath.NetRequirement(10, 20, 0));
        Assert.Equal(10m, ProductionMath.NetRequirement(10, -3, 0));
        Assert.Equal(3m, ProductionMath.Buildable([new BuildLine(1, 10), new BuildLine(2, 7)]));

        Assert.True(ProductionMath.CanRecord(WorkOrderStatus.IN_PROGRESS));
        Assert.False(ProductionMath.CanRecord(WorkOrderStatus.DRAFT));
        Assert.True(ProductionMath.CanCancel(WorkOrderStatus.RELEASED, 0));
        Assert.False(ProductionMath.CanCancel(WorkOrderStatus.IN_PROGRESS, 2));
        Assert.True(ProductionMath.IsLate(WorkOrderStatus.RELEASED, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)));
        Assert.False(ProductionMath.IsLate(WorkOrderStatus.COMPLETED, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)));
        Assert.Equal(40, ProductionMath.Progress(4, 10));
        Assert.Equal("PARTIAL", ProductionMath.Availability(10, 4));
    }
}
