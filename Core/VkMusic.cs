using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encore.Core;

public sealed class VkMusic(HttpClient http, IConfiguration config) {
    private static readonly Regex TrackId = new(@"^-?[1-9][0-9]{0,18}_[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);
    private sealed record Recording(Track Track, string Stream);
    private string? Token => config["ENCORE_VK_TOKEN"];
    public bool Configured => !string.IsNullOrWhiteSpace(Token);

    public async Task<Track[]> Search(string? query, CancellationToken cancellationToken) {
        query = query?.Trim();
        if (query == null || query.Length is < 2 or > 120)
            throw new RuleException("Введите от 2 до 120 символов для поиска в VK.");
        var result = await Read("audio.search", new() { ["q"] = query, ["count"] = "15" }, cancellationToken);
        return result.Select(item => item.Track).Take(15).ToArray();
    }

    public async Task<Track> Get(string? id, CancellationToken cancellationToken) {
        return (await GetRecording(id, cancellationToken)).Track;
    }

    public async Task<Uri> Playback(string id, CancellationToken cancellationToken) {
        var recording = await GetRecording(id, cancellationToken);
        if (!Uri.TryCreate(recording.Stream, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !IsVkMediaHost(uri.IdnHost) ||
            recording.Stream.Contains(Token!, StringComparison.Ordinal))
            throw new RuleException("VK не предоставил аудиопоток для этой композиции.", 422);
        return uri;
    }

    private static bool IsVkMediaHost(string name) =>
        new[] { "vk.com", "vk.ru", "vkuseraudio.net", "vkuser.net", "vk-cdn.net", "vkuseraudio.com" }
            .Any(domain => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal));

    private async Task<Recording> GetRecording(string? id, CancellationToken cancellationToken) {
        if (id == null || !TrackId.IsMatch(id))
            throw new RuleException("Неверный идентификатор трека VK.");
        var result = await Read("audio.getById", new() { ["audios"] = id }, cancellationToken);
        return result.FirstOrDefault(item => item.Track.VkId == id)
            ?? throw new RuleException("Трек больше недоступен в VK.", 404);
    }

    private async Task<Recording[]> Read(string method, Dictionary<string, string> parameters, CancellationToken cancellationToken) {
        if (!Configured) throw new RuleException("Поиск VK пока недоступен. Можно отправить свой аудиофайл.", 503);
        parameters["v"] = "5.199";
        parameters["access_token"] = Token!;
        try {
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await http.PostAsync(method, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var root = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!root.TryGetProperty("response", out var items))
                throw new RuleException("VK не предоставил доступ к музыке. Проверьте права токена VK Music API.", 502);
            if (items.ValueKind == JsonValueKind.Object) items = items.GetProperty("items");
            return items.EnumerateArray().Select(Parse).OfType<Recording>().ToArray();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) {
            throw new RuleException("VK Музыка временно недоступна. Попробуйте позже.", 502);
        }
    }

    private static Recording? Parse(JsonElement item) {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
            || !item.TryGetProperty("owner_id", out var owner) || owner.ValueKind != JsonValueKind.Number
            || !item.TryGetProperty("duration", out var length) || length.ValueKind != JsonValueKind.Number
            || !length.TryGetInt32(out var duration) || duration <= 0) return null;
        var key = $"{owner.GetRawText()}_{id.GetRawText()}";
        var title = Text(item, "title");
        var artist = Text(item, "artist");
        if (!TrackId.IsMatch(key) || title.Length == 0 || artist.Length == 0) return null;
        var explicitTrack = item.TryGetProperty("is_explicit", out var flag) &&
            (flag.ValueKind == JsonValueKind.True || (flag.ValueKind == JsonValueKind.Number && flag.TryGetInt32(out var value) && value == 1));
        var stream = item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString() ?? "" : "";
        return new Recording(new Track(0, title, artist, "VK Музыка", duration, "blue", "VK", explicitTrack, key), stream);
    }

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? string.Concat(value.GetString()!.Trim().Take(200)) : "";
}
