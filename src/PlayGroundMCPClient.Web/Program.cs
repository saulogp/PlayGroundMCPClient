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
builder.Services.AddSingleton<PendingAuthorizationStore>();
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

// OAuth Authorization Code redirect target. The provider sends the user's
// browser here; we finish the PKCE exchange and persist the token. This is the
// web-native replacement for the old loopback HttpListener, so the flow works
// inside a container where the app and browser are on different machines.
app.MapGet("/oauth/callback", async (
    HttpContext http,
    OAuthClient oauthClient,
    TokenStore tokenStore) =>
{
    var q = http.Request.Query;
    var state = q["state"].ToString();
    if (string.IsNullOrWhiteSpace(state))
    {
        return Results.Content(CallbackHtml("Falha", "Callback sem 'state'."), "text/html");
    }
    try
    {
        var (serverName, token) = await oauthClient.CompleteAuthorizationAsync(
            state,
            q["code"].ToString() is { Length: > 0 } code ? code : null,
            q["error"].ToString() is { Length: > 0 } error ? error : null,
            q["error_description"].ToString() is { Length: > 0 } desc ? desc : null,
            http.RequestAborted);
        tokenStore.Save(serverName, token);
        return Results.Content(
            CallbackHtml("Autenticado", $"Servidor \"{serverName}\" autenticado. Pode fechar esta aba."),
            "text/html");
    }
    catch (Exception ex)
    {
        return Results.Content(CallbackHtml("Falha", ex.Message), "text/html");
    }
});

app.Run();

static string CallbackHtml(string title, string message)
{
    var t = System.Net.WebUtility.HtmlEncode(title);
    var m = System.Net.WebUtility.HtmlEncode(message);
    return "<!doctype html><html lang=\"pt-br\"><head><meta charset=\"utf-8\"><title>" + t + "</title>"
        + "<style>body{font-family:system-ui,sans-serif;margin:3rem;color:#222}</style></head>"
        + "<body><h2>" + t + "</h2><p>" + m + "</p>"
        + "<script>setTimeout(function(){try{window.close();}catch(e){}},1500);</script>"
        + "</body></html>";
}
