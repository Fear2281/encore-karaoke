using Encore.Core;

namespace Encore.Modules;

public static class FavoritesModule {
    public static void MapFavoritesModule(this WebApplication app) {
        var group = app.MapGroup("/api/favorites");
        group.MapGet("", (MusicStore store, HttpContext context) =>
            Results.Ok(store.Favorites(store.Guest(context.Request.Cookies["encore.guest"]))));
        group.MapPost("/vk", async (CatalogModule.VkOrder input, MusicStore store, VkMusic vk, HttpContext context) => {
            var guest = store.Guest(context.Request.Cookies["encore.guest"]);
            var track = await vk.Get(input.Id, context.RequestAborted);
            return Results.Ok(store.SaveVkFavorite(guest, track));
        }).RequireRateLimiting("writes");
        group.MapPut("/{id:int}", (int id, MusicStore store, HttpContext context) => {
            store.SetFavorite(store.Guest(context.Request.Cookies["encore.guest"]), id, true);
            return Results.Ok(new { saved = true });
        }).RequireRateLimiting("writes");
        group.MapDelete("/{id:int}", (int id, MusicStore store, HttpContext context) => {
            store.SetFavorite(store.Guest(context.Request.Cookies["encore.guest"]), id, false);
            return Results.Ok(new { saved = false });
        }).RequireRateLimiting("writes");
    }
}
