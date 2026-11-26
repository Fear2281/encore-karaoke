using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Encore.Core;

public sealed class ItunesCatalog(IHttpClientFactory clients, IMemoryCache cache) {
    public async Task<Track[]> Search(string? query, CancellationToken cancellation) {
        query = query?.Trim();
        if (query == null || query.Length is < 2 or > 120)
            throw new RuleException("Введите от 2 до 120 символов для поиска.");
        var key = "itunes-search:" + query.ToLowerInvariant();
        if (cache.TryGetValue<Track[]>(key, out var cached)) return cached!;
        var tracks = await Read("search?country=RU&media=music&entity=song&limit=20&term=" + Uri.EscapeDataString(query), cancellation);
        cache.Set(key, tracks, TimeSpan.FromMinutes(5));
        return tracks;
    }

    public async Task<Track> Get(long id, CancellationToken cancellation) {
        if (id <= 0) throw new RuleException("Неверный идентификатор iTunes.");
        if (cache.TryGetValue<Track>("itunes-track:" + id, out var track)) return track!;
        var tracks = await Read($"lookup?country=RU&id={id}", cancellation);
        return tracks.FirstOrDefault(item => item.ItunesId == id)
            ?? throw new RuleException("Песня iTunes больше недоступна.", 404);
    }

    private async Task<Track[]> Read(string path, CancellationToken cancellation) {
        using var client = clients.CreateClient("itunes");
        try {
            var payload = await client.GetFromJsonAsync<SearchResponse>(path, cancellation);
            var tracks = (payload?.Results ?? []).Where(item => item.Kind == "song" && item.TrackId > 0 &&
                !string.IsNullOrWhiteSpace(item.TrackName) && !string.IsNullOrWhiteSpace(item.ArtistName) && item.TrackTimeMillis > 0)
                .DistinctBy(item => item.TrackId).Select(item => new Track(0, item.TrackName!, item.ArtistName!, "iTunes",
                    (int)Math.Ceiling(item.TrackTimeMillis / 1000d), "purple", "♫",
                    item.TrackExplicitness == "explicit", ItunesId: item.TrackId)).ToArray();
            foreach (var track in tracks) cache.Set("itunes-track:" + track.ItunesId, track, TimeSpan.FromMinutes(10));
            return tracks;
        } catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) {
            throw new RuleException("Поиск iTunes временно недоступен. Попробуйте чуть позже.", 502);
        }
    }

    private sealed record SearchResponse(ItunesSong[]? Results);
    private sealed record ItunesSong(long TrackId, string? TrackName, string? ArtistName, string? Kind,
        int TrackTimeMillis, string? TrackExplicitness);
}
