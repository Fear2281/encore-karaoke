using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encore.Core;

public sealed record ChartTrack(int Rank, string Title, string Artist);
public sealed record ChartSnapshot(DateOnly Date, ChartTrack[] Tracks);

public sealed class VkChart(IHttpClientFactory clients, IWebHostEnvironment environment, ILogger<VkChart> logger) : BackgroundService {
    public const string Source = "https://blog.upsound.ai/charts/vk/";
    private volatile ChartSnapshot snapshot = JsonSerializer.Deserialize<ChartSnapshot>(
        File.ReadAllText(Path.Combine(environment.ContentRootPath, "vk-chart.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public ChartSnapshot Snapshot => snapshot;

    public static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant().Replace('ё', 'е').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do {
            try {
                using var client = clients.CreateClient("vk-chart");
                var html = await client.GetStringAsync(Source, stoppingToken);
                var updated = Parse(html);
                if (updated.Date >= snapshot.Date) snapshot = updated;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) {
                logger.LogWarning(ex, "Unable to refresh the public VK chart; last snapshot retained");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static ChartSnapshot Parse(string html) {
        var stamp = Regex.Match(html, @"data-chart-date=""(\d{4}-\d{2}-\d{2})""").Groups[1].Value;
        if (!DateOnly.TryParseExact(stamp, "yyyy-MM-dd", out var date)) throw new FormatException("Missing chart date.");
        foreach (Match script in Regex.Matches(html, @"<script\s+type=""application/ld\+json""[^>]*>(.*?)</script>", RegexOptions.Singleline)) {
            using var document = JsonDocument.Parse(script.Groups[1].Value);
            if (!document.RootElement.TryGetProperty("itemListElement", out var elements)) continue;
            var rows = new List<ChartTrack>();
            foreach (var item in elements.EnumerateArray()) {
                var name = item.GetProperty("name").GetString() ?? "";
                var split = name.LastIndexOf(" — ", StringComparison.Ordinal);
                if (split < 1 || split + 3 >= name.Length) continue;
                var rank = item.GetProperty("position").GetInt32();
                if (rank is >= 1 and <= 100)
                    rows.Add(new ChartTrack(rank, name[..split].Trim(), name[(split + 3)..].Trim()));
            }
            if (rows.Select(row => row.Rank).Distinct().Count() >= 50)
                return new ChartSnapshot(date, rows.DistinctBy(row => row.Rank).OrderBy(row => row.Rank).ToArray());
        }
        throw new FormatException("Missing chart tracks.");
    }
}
