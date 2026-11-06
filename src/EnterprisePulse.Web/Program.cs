using EnterprisePulse.Web.Components;
using EnterprisePulse.Web.Services;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    o.SnackbarConfiguration.VisibleStateDuration = 4000;
});

var apiUrl = builder.Configuration["ApiUrl"] ?? "http://localhost:5100";
builder.Services.AddHttpClient("api", c =>
{
    c.BaseAddress = new Uri(apiUrl);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton(new ApiEndpoint(apiUrl));
builder.Services.AddSingleton<MetaCache>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<RealtimeClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
