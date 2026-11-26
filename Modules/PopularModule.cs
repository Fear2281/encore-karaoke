using Encore.Core;

namespace Encore.Modules;

public static class PopularModule {
    public static void MapPopularModule(this WebApplication app) {
        app.MapGet("/api/popular", (MusicStore store) => Results.Ok(store.Popular()));
    }
}
