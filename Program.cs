using Encore.Core;
using Encore.Modules;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory=builder.Configuration["ENCORE_DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath,"App_Data");
var keysDirectory=Directory.CreateDirectory(Path.Combine(dataDirectory,"keys"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(keysDirectory);
builder.Services.Configure<ForwardedHeadersOptions>(options=> {
    options.ForwardedHeaders=ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var trustedProxy=builder.Configuration["ENCORE_TRUSTED_PROXY"];
    if(!string.IsNullOrEmpty(trustedProxy)) {
        if(!IPAddress.TryParse(trustedProxy,out var address)) throw new InvalidOperationException("ENCORE_TRUSTED_PROXY должен быть IP-адресом вашего reverse proxy.");
        options.KnownProxies.Add(address);
    }
});
builder.Services.AddSingleton<MusicStore>();
builder.Services.AddSingleton<AudioUploads>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ItunesCatalog>();
builder.Services.AddHttpClient("itunes", client => {
    client.BaseAddress = new Uri("https://itunes.apple.com/");
    client.Timeout = TimeSpan.FromSeconds(12);
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
});
builder.Services.AddHttpClient("vk-chart", client => {
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
});
builder.Services.AddSingleton<VkChart>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VkChart>());
builder.Services.AddHttpClient<VkMusic>(client => {
    client.BaseAddress = new Uri("https://api.vk.com/method/");
    client.Timeout = TimeSpan.FromSeconds(8);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<QueueClock>();
builder.Services.AddAuthentication("dj").AddCookie("dj", options => {
    options.Cookie.Name = "encore.dj";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    foreach (var policy in new[]{"writes","login","vk-search","itunes","visits"}) options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
        policy + ":" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = policy switch { "writes" => 120, "visits" or "vk-search" => 60, _ => 10 }, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Method is "POST" or "PUT" or "DELETE") {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") {
            context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Запрос с другого сайта запрещён." }); return;
        }
    }
    try { await next(); }
    catch (RuleException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }

});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", application = "Encore", modules = 5 }));
app.MapGuestModule();
app.MapDjModule();
app.MapCatalogModule();
app.MapPopularModule();
app.MapFavoritesModule();
app.Run();
