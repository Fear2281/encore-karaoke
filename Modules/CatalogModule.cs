using Encore.Core;

namespace Encore.Modules;

public static class CatalogModule {
    public static void MapCatalogModule(this WebApplication app) {
        app.MapPost("/api/orders/custom", (CustomOrder input, HttpContext context, MusicStore store) =>
            Results.Ok(store.AddCustom(store.Guest(context.Request.Cookies["encore.guest"]), input.Title, input.Artist)))
            .RequireRateLimiting("writes");
        app.MapGet("/api/catalog/itunes", async (string? query, ItunesCatalog itunes, CancellationToken cancellation) =>
            Results.Ok(await itunes.Search(query, cancellation))).RequireRateLimiting("itunes");
        app.MapPost("/api/catalog/itunes/order", async (ItunesOrder input, ItunesCatalog itunes, MusicStore store, HttpContext context) => {
            var guest = store.Guest(context.Request.Cookies["encore.guest"]);
            var track = await itunes.Get(input.Id, context.RequestAborted);
            return Results.Ok(store.AddItunes(guest, track));
        }).RequireRateLimiting("itunes");
        app.MapGet("/api/catalog/vk/status", (VkMusic vk) => Results.Ok(new { vk.Configured }));
        app.MapGet("/api/catalog/vk/discover", async (string? query, VkChart chart, VkMusic vk, ILogger<VkChart> log, CancellationToken cancellation) => {
            var term = VkChart.Normalize(query ?? "");
            if (term.Length > 120) throw new RuleException("Запрос должен быть не длиннее 120 символов.");
            var snapshot = chart.Snapshot;
            var matches = snapshot.Tracks.Where(track => term.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .All(word => VkChart.Normalize(track.Title + " " + track.Artist).Contains(word, StringComparison.Ordinal))).ToArray();
            Track[] live = [];
            var fullSearch = false;
            if (term.Length >= 2 && vk.Configured) {
                try {
                    live = await vk.Search(query, cancellation);
                    fullSearch = true;
                } catch (RuleException error) { log.LogInformation("VK full search unavailable ({Status})", error.Status); }
            }
            return Results.Ok(new { snapshot.Date, chart = matches.Take(term.Length == 0 ? 20 : 100), live, fullSearch });
        }).RequireRateLimiting("vk-search");
        app.MapPost("/api/orders/chart", (ChartOrder input, HttpContext context, MusicStore store, VkChart chart) => {
            var guest = store.Guest(context.Request.Cookies["encore.guest"]);
            var snapshot = chart.Snapshot;
            var track = input.Date == snapshot.Date ? snapshot.Tracks.FirstOrDefault(t => t.Rank == input.Rank) : null;
            if (track == null) throw new RuleException("Чарт обновился. Выберите песню ещё раз.", 409);
            return Results.Ok(store.AddChart(guest, track));
        }).RequireRateLimiting("writes");
        app.MapGet("/api/catalog/vk", async (string? query, VkMusic vk, CancellationToken cancellationToken) =>
            Results.Ok(await vk.Search(query, cancellationToken))).RequireRateLimiting("vk-search");
        app.MapPost("/api/catalog/vk/order", async (VkOrder input, MusicStore store, VkMusic vk, HttpContext context) => {
            var guest = store.Guest(context.Request.Cookies["encore.guest"]);
            var track = await vk.Get(input.Id, context.RequestAborted);
            return Results.Ok(store.AddVk(guest, track));
        }).RequireRateLimiting("writes");

    }

    public record VkOrder(string? Id);
    public record ItunesOrder(long Id);
    public record ChartOrder(DateOnly Date, int Rank);
    public record CustomOrder(string? Title, string? Artist);
}
