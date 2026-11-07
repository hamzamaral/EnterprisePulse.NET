namespace EnterprisePulse.Api.Endpoints;

/// <summary>Business module endpoints (HR, assets, inventory...); each phase adds its own Map call here.</summary>
public static class ModuleEndpoints
{
    public static void MapModules(this IEndpointRouteBuilder app)
    {
        app.MapHr();
        app.MapAssets();
        app.MapInventory();
        app.MapPurchasing();
        app.MapSales();
    }
}
