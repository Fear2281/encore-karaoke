using Encore.Core;
namespace Encore.Modules;
public static class GuestModule {
    private static void SetCookie(HttpContext context,GuestSession session) => context.Response.Cookies.Append("encore.guest",session.Id,new CookieOptions {HttpOnly=true,SameSite=SameSiteMode.Lax,Secure=context.Request.IsHttps,Expires=session.Expires,IsEssential=true});
    public static void MapGuestModule(this WebApplication app) {
        app.MapGet("/api/tables",(MusicStore store)=>Results.Ok(store.TableNumbers()));
        app.MapGet("/api/qr",(HttpContext context,IConfiguration config)=>Results.Text(QrLinks.Svg("/",context,config),"image/svg+xml"));
        app.MapGet("/table/{token}",(string token, MusicStore store, HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            try {
                SetCookie(context, store.CreateSession(token, context.Request.Cookies["encore.guest"]));
                return Results.Redirect("/?table-qr=joined");
            } catch (RuleException ex) when (ex.Status == 409) {
                return Results.Redirect("/?table-qr=locked");
            }
        }).RequireRateLimiting("visits");
        app.MapGet("/api/guest/session",(MusicStore store,HttpContext context,IWebHostEnvironment env)=> {
            try {var s=store.Guest(context.Request.Cookies["encore.guest"]);return Results.Ok(new {table=(int?)s.Table,s.Expires,demo=env.IsDevelopment()});}
            catch(RuleException) {return Results.Ok(new {table=(int?)null,expires=(DateTimeOffset?)null,demo=env.IsDevelopment()});}
        });
        app.MapPost("/api/guest/end", (MusicStore store, HttpContext context) => {
            store.EndVisit(context.Request.Cookies["encore.guest"]);
            context.Response.Cookies.Delete("encore.guest");
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/queue", (MusicStore store, HttpContext context) =>
            Results.Ok(store.Snapshot(context.Request.Cookies["encore.guest"])));
        app.MapPost("/api/orders",(OrderInput input,MusicStore store,HttpContext context)=>Results.Ok(store.Add(store.Guest(context.Request.Cookies["encore.guest"]),input.TrackId))).RequireRateLimiting("writes");
    }
    public record OrderInput(int TrackId);
}
