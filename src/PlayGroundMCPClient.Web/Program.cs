using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using PlayGroundMCPClient.Web.Components;
using PlayGroundMCPClient.Web.Data;
using PlayGroundMCPClient.Web.Models;
using PlayGroundMCPClient.Web.Services;
using PlayGroundMCPClient.Web.Services.OAuth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.Configure<LlmOptions>(
    builder.Configuration.GetSection(LlmOptions.SectionName));

var mcpFile = builder.Configuration["McpServersFile"] ?? "mcp-servers.json";
builder.Configuration.AddJsonFile(mcpFile, optional: true, reloadOnChange: true);
builder.Services.Configure<McpServersOptions>(builder.Configuration);

builder.Services.AddDbContextFactory<PlaygroundDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Playground")
        ?? "Data Source=playground.db"));

builder.Services.AddSingleton<ProtocolLogStore>();
builder.Services.AddSingleton<McpServerRegistry>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<OAuthState>();
builder.Services.AddHttpClient("oauth-discovery");
builder.Services.AddSingleton<OAuthMetadataDiscovery>();
builder.Services.AddSingleton<OAuthClient>();
builder.Services.AddSingleton<McpClientPool>();
builder.Services.AddSingleton<LlmStore>();
builder.Services.AddSingleton<PersonalityRegistry>();
builder.Services.AddScoped<ChatOrchestrator>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlaygroundDbContext>>();
    using var ctx = factory.CreateDbContext();
    ctx.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
