using System.Globalization;
using LoadTester.Web.Components;
using LoadTester.Web.Data;
using LoadTester.Web.Services;
using Microsoft.EntityFrameworkCore;

// The interface is Turkish; numbers and dates follow tr-TR whatever the machine's locale is.
var culture = CultureInfo.GetCultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 1024 * 1024); // request bodies typed into the editor can be large

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "loadtester.db")}"));

builder.Services.AddSingleton<ScenarioStore>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<RunManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<RunManager>());

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}
await app.Services.GetRequiredService<RunStore>().MarkInterruptedAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/", () => Results.Redirect("/scenarios"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
