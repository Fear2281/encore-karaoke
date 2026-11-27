using System.Security.Cryptography;
using System.Text.Json;

namespace Encore.Core;

public record Track(int Id, string Title, string Artist, string Genre, int Duration, string Color, string Initials, bool Explicit = false,
    string? VkId = null, string? UploadKey = null, bool ChartRequest = false, long? ItunesId = null);
public record RestaurantTable(int Number, string Token);
public record GuestSession(string Id, int Table, DateTimeOffset Expires) {
    public HashSet<int> Favorites { get; init; } = [];
}
public class RequestItem {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int TrackId { get; set; }
    public int Table { get; set; }
    public string? VisitId { get; set; }
    public string Status { get; set; } = "waiting";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Completed { get; set; }
    public string? Reason { get; set; }
    public HashSet<int> Votes { get; set; } = [];
}
public class Settings {
    public int TableLimit { get; set; } = 2;
    public bool AllowExplicit { get; set; }
    public int MaxDuration { get; set; } = 360;
    public string Mood { get; set; } = "Вечерний джаз";
    public bool Accepting { get; set; } = true;
}
public class SavedState {
    public List<RestaurantTable> Tables { get; set; } = [];
    public List<RequestItem> Orders { get; set; } = [];
    public List<Track> ImportedTracks { get; set; } = [];
    public Dictionary<string, GuestSession> Visits { get; set; } = [];
    public Settings Settings { get; set; } = new();
}
public class RuleException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

public class MusicStore {
    private readonly object gate = new();
    private readonly string file;
    private SavedState state;
    private readonly Dictionary<int, Track> tracks;
    private readonly AudioUploads uploads;
    public MusicStore(IWebHostEnvironment env, AudioUploads uploads) {
        this.uploads = uploads;
        var directory = AudioUploads.DataDirectory(env);
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "state.json");
        state = File.Exists(file) ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(file)) ?? throw new InvalidDataException("Некорректный файл состояния") : new SavedState();
        tracks = TrackCatalog.All.Concat(state.ImportedTracks).ToDictionary(track => track.Id);
        var changed = false;
        for(var number = 1; number <= 12; number++) {
            if(state.Tables.Any(t => t.Number == number)) continue;
            state.Tables.Add(new RestaurantTable(number, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()));
            changed = true;
        }
        foreach (var order in state.Orders.Where(o => Active(o) && (o.VisitId == null || !state.Visits.ContainsKey(o.VisitId)))) {
            order.Status = "skipped";
            order.Reason = "Визит завершён";
            order.VisitId = null;
            changed = true;
        }
        if(changed) Save();
        uploads.RemoveOrphans(state.ImportedTracks.Where(t => t.UploadKey != null &&
            state.Orders.Any(o => o.TrackId == t.Id && o.VisitId != null && state.Visits.ContainsKey(o.VisitId)))
            .Select(t => t.UploadKey!).ToHashSet());
    }
    public object TableNumbers() { lock(gate) return state.Tables.OrderBy(t=>t.Number).Select(t=>new {number=t.Number}).ToArray(); }
    private void Save() {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, true);
    }
    private static bool Active(RequestItem o) => o.Status is "waiting" or "approved" or "playing";
    // Sort tables by their last turn, then give each one a turn before taking its next request.
    private List<RequestItem> Queue() {
        var lastTurns = state.Orders.Where(order => order.Started.HasValue)
            .GroupBy(order => order.Table).ToDictionary(table => table.Key, table => table.Max(order => order.Started));
        return state.Orders.Where(order => order.Status is "approved" or "waiting")
            .GroupBy(order => order.Table)
            .Select(table => table.OrderBy(order => order.Created).Select((order, round) => new { order, round }))
            .SelectMany(table => table)
            .OrderBy(entry => entry.round).ThenBy(entry => lastTurns.GetValueOrDefault(entry.order.Table))
            .ThenBy(entry => entry.order.Created).Select(entry => entry.order).ToList();
    }
    private bool Available(Track t) => (!t.Explicit || state.Settings.AllowExplicit) && t.Duration <= state.Settings.MaxDuration;
    public GuestSession CreateSession(string token, string? currentId = null) { lock(gate) {
        var table = state.Tables.FirstOrDefault(t => t.Token == token) ?? throw new RuleException("Неизвестный код столика.",404);
        var current = FindGuest(currentId);
        if (current?.Table == table.Number) return current;
        if (current != null) throw new RuleException("Сначала завершите текущий визит, чтобы подключить другой столик.", 409);
        if (state.Visits.Count >= 500) throw new RuleException("Слишком много открытых визитов. Обратитесь к сотруднику.", 503);
        var session = new GuestSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), table.Number, DateTimeOffset.UtcNow.AddHours(4));
        state.Visits.Add(session.Id, session);
        try { Save(); } catch { state.Visits.Remove(session.Id); throw; }
        return session;
    }}
    private GuestSession? FindGuest(string? id) =>
        id != null && state.Visits.TryGetValue(id, out var guest) && guest.Expires > DateTimeOffset.UtcNow ? guest : null;
    public GuestSession Guest(string? id) { lock(gate) {
        return FindGuest(id) ?? throw new RuleException("Визит завершён. Отсканируйте QR столика.", 401);
    }}
    public void EndVisit(string? id) { lock(gate) {
        if (id != null && state.Visits.ContainsKey(id)) CloseVisits([id]);
    }}
    public int CloseTable(int number) { lock(gate) {
        TableToken(number);
        var ids = state.Visits.Values.Where(v => v.Table == number).Select(v => v.Id).ToArray();
        CloseVisits(ids);
        return ids.Length;
    }}
    private void CloseVisits(string[] ids) {
        if (ids.Length == 0) return;
        var closed = ids.ToHashSet();
        var orders = state.Orders.Where(o => o.VisitId != null && closed.Contains(o.VisitId)).ToArray();
        var previousOrders = orders.Select(o => (Order: o, o.VisitId, o.Status, o.Reason)).ToArray();
        var previousVisits = ids.Select(id => state.Visits[id]).ToArray();
        foreach (var id in ids) state.Visits.Remove(id);
        foreach (var order in orders) {
            order.VisitId = null;
            if (!Active(order)) continue;
            order.Status = "skipped";
            order.Reason = "Визит завершён";
        }
        try { Save(); }
        catch {
            foreach (var visit in previousVisits) state.Visits.Add(visit.Id, visit);
            foreach (var entry in previousOrders) {
                entry.Order.VisitId = entry.VisitId;
                entry.Order.Status = entry.Status;
                entry.Order.Reason = entry.Reason;
            }
            throw;
        }
        foreach (var key in orders.Select(o => tracks[o.TrackId].UploadKey).OfType<string>().Distinct())
            uploads.Remove(key);
    }
    public object Snapshot(string? visitId = null, bool dj = false) { lock(gate) {
        var guest = FindGuest(visitId);
        var queue = Queue();
        var playing = state.Orders.FirstOrDefault(o=>o.Status == "playing");
        var remaining = playing == null ? 0 : Math.Max(0, (int)Math.Ceiling(
            (playing.Started!.Value.AddSeconds(tracks[playing.TrackId].Duration) - DateTimeOffset.UtcNow).TotalSeconds));
        var eta = remaining;
        var items = queue.Select((order, index) => {
            var song = tracks[order.TrackId];
            var wait = eta;
            eta += song.Duration;
            return new { order.Id, track = QueueTrack(song), order.Table, order.Status, position = index + 1,
                waitSeconds = wait, tickingSeconds = remaining, ownTable = guest?.Table == order.Table };
        }).ToArray();
        var current = playing != null
            ? new { playing.Id, track = QueueTrack(tracks[playing.TrackId]), playing.Table, remaining } : null;
        var history = state.Orders.Where(o => !Active(o) && (dj || (guest != null && o.VisitId == guest.Id)))
            .OrderByDescending(o => o.Created).Take(20)
            .Select(o => new { o.Id, track = QueueTrack(tracks[o.TrackId]), o.Table, o.Status, o.Reason }).ToArray();
        if (!dj) return new { table = guest?.Table, expires = guest?.Expires, queue = items, playing = current, settings = state.Settings, history };
        var stats = new { total = state.Orders.Count, completed = state.Orders.Count(o => o.Status == "completed"), waiting = queue.Count, votes = state.Orders.Sum(o => o.Votes.Count) };
        return new { queue = items, playing = current, settings = state.Settings, stats, history };
    }}
    private static object QueueTrack(Track track) =>
        new { track.Title, track.Artist, track.Duration, track.Color, track.Initials };
    public object Add(GuestSession guest, int trackId) { lock(gate) {
        var track = FindTrack(trackId);
        if (track.VkId == null) throw new RuleException("Выберите композицию из поиска или чарта.", 404);
        return AddOrder(guest, track);
    }}
    public object AddVk(GuestSession guest, Track source) { lock(gate) {
        var existing = state.ImportedTracks.FirstOrDefault(t => t.VkId == source.VkId);
        if (existing != null) return AddOrder(guest, existing);
        var track = source with { Id = Math.Max(999, tracks.Keys.Max()) + 1 };
        return AddOrder(guest, track, import: true);
    }}
    public Track SaveVkFavorite(GuestSession visit, Track source) { lock(gate) {
        var guest = Guest(visit.Id);
        var existing = state.ImportedTracks.FirstOrDefault(t => t.VkId == source.VkId);
        if (existing != null) {
            SetFavorite(guest, existing.Id, true);
            return existing;
        }
        var track = source with { Id = Math.Max(999, tracks.Keys.Max()) + 1 };
        if (guest.Favorites.Count >= 100) throw new RuleException("В избранном можно сохранить до 100 треков.", 409);
        state.ImportedTracks.Add(track);
        guest.Favorites.Add(track.Id);
        try { Save(); }
        catch {
            guest.Favorites.Remove(track.Id);
            state.ImportedTracks.Remove(track);
            throw;
        }
        tracks.Add(track.Id, track);
        return track;
    }}
    public object AddChart(GuestSession guest, ChartTrack source) { lock(gate) {
        var track = new Track(Math.Max(999, tracks.Keys.Max()) + 1, source.Title, source.Artist, "VK Музыка", Math.Min(180, state.Settings.MaxDuration),
            "blue", "VK", ChartRequest: true);
        if (state.Orders.Any(o => o.Table == guest.Table && Active(o) && tracks[o.TrackId].ChartRequest &&
            tracks[o.TrackId].Title == track.Title && tracks[o.TrackId].Artist == track.Artist))
            throw new RuleException("Эта композиция уже в очереди вашего столика.", 409);
        return AddOrder(guest, track, import: true);
    }}
    public object AddCustom(GuestSession guest, string? title, string? artist) { lock(gate) {
        title = title?.Trim();
        artist = artist?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl))
            throw new RuleException("Введите название песни длиной до 120 символов.");
        if (artist.Length > 120 || artist.Any(char.IsControl))
            throw new RuleException("Укажите исполнителя длиной до 120 символов.");
        var name = VkChart.Normalize(title);
        var performer = VkChart.Normalize(artist);
        if (state.Orders.Any(order => order.Table == guest.Table && Active(order) &&
            VkChart.Normalize(tracks[order.TrackId].Title) == name &&
            (performer.Length == 0 || VkChart.Normalize(tracks[order.TrackId].Artist) == performer)))
            throw new RuleException("Эта композиция уже в очереди вашего столика.", 409);
        var track = new Track(Math.Max(999, tracks.Keys.Max()) + 1, title, artist, "Своя музыка",
            Math.Min(180, state.Settings.MaxDuration), "purple", "♫");
        return AddOrder(guest, track, import: true);
    }}
    public object AddItunes(GuestSession guest, Track source) { lock(gate) {
        if (state.Orders.Any(order => order.Table == guest.Table && Active(order) &&
            tracks[order.TrackId].ItunesId == source.ItunesId))
            throw new RuleException("Эта композиция уже в очереди вашего столика.", 409);
        return AddOrder(guest, source with { Id = Math.Max(999, tracks.Keys.Max()) + 1 }, import: true);
    }}
    private Track FindTrack(int id) => tracks.TryGetValue(id, out var track)
        ? track : throw new RuleException("Композиция не найдена.", 404);
    private object AddOrder(GuestSession guest, Track track, bool import = false) {
        Guest(guest.Id);
        if(!state.Settings.Accepting) throw new RuleException("Приём заявок на паузе. Дождитесь следующего музыкального слота.",409);
        if(!Available(track)) throw new RuleException("Композиция не подходит под текущие настройки вечера.",409);
        if(state.Orders.Any(o=>o.Table==guest.Table && o.TrackId==track.Id && Active(o))) throw new RuleException("Этот трек уже заказан вашим столиком.",409);
        if(state.Orders.Count(o=>o.Table==guest.Table && Active(o)) >= state.Settings.TableLimit) throw new RuleException($"Лимит: {state.Settings.TableLimit} активные заявки на столик.",409);
        var order = new RequestItem { TrackId = track.Id, Table = guest.Table, VisitId = guest.Id };
        if (import) state.ImportedTracks.Add(track);
        state.Orders.Add(order);
        try { Save(); }
        catch {
            state.Orders.Remove(order);
            if (import) state.ImportedTracks.Remove(track);
            throw;
        }
        if (import) tracks.Add(track.Id, track);
        return new { order.Id, order.Status, trackId = track.Id };
    }
    public object Popular() { lock(gate) {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        return state.Orders.Where(o => o.Status == "completed" && (o.Completed ?? o.Started ?? o.Created) >= cutoff)
            .GroupBy(o => o.TrackId)
            .Where(group => tracks[group.Key].VkId != null && Available(tracks[group.Key]))
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Max(o => o.Completed ?? o.Started ?? o.Created))
            .Take(10).Select(group => new { track = tracks[group.Key], count = group.Count() }).ToArray();
    }}
    public Track[] Favorites(GuestSession guest) { lock(gate) {
        return Guest(guest.Id).Favorites.Select(FindTrack).OrderBy(t => t.Title).ToArray();
    }}
    public void SetFavorite(GuestSession guest, int id, bool saved) { lock(gate) {
        if (FindTrack(id).VkId == null) throw new RuleException("В избранное можно добавить треки VK.", 404);
        var favorites = Guest(guest.Id).Favorites;
        if (saved && favorites.Count >= 100 && !favorites.Contains(id))
            throw new RuleException("В избранном можно сохранить до 100 треков.", 409);
        var changed = saved ? favorites.Add(id) : favorites.Remove(id);
        if (!changed) return;
        try { Save(); }
        catch {
            if (saved) favorites.Remove(id);
            else favorites.Add(id);
            throw;
        }
    }}
    public void Action(string id,string action) { lock(gate) {
        var o=state.Orders.FirstOrDefault(o=>o.Id==id) ?? throw new RuleException("Заявка не найдена.",404);
        if(!Active(o)) throw new RuleException("Заявка уже завершена.",409);
        switch(action) {
            case "approve": if(o.Status!="waiting") throw new RuleException("Заявка уже одобрена.",409); o.Status="approved"; break;
            case "play":
                if(o.Status!="approved") throw new RuleException("Сначала одобрите заявку.",409);
                if(state.Orders.Any(x=>x.Status=="playing")) throw new RuleException("Сначала завершите текущее выступление.",409);
                if (Queue().FirstOrDefault()?.Id != id) throw new RuleException("Сначала обработайте первую заявку общей очереди.", 409);
                o.Status="playing"; o.Started=DateTimeOffset.UtcNow; break;
            case "complete": if(o.Status!="playing") throw new RuleException("Трек ещё не запущен.",409); Complete(o); break;
            case "reject": if(o.Status=="playing") throw new RuleException("Используйте завершение или пропуск.",409); o.Status="rejected"; o.Reason="Не подходит к программе вечера"; break;
            case "skip": o.Status="skipped"; break;
            default: throw new RuleException("Неизвестное действие.");
        }
        Save();
    }}
    public void Update(Settings value) { lock(gate) {
        if(value.TableLimit is <1 or >5 || value.MaxDuration is <60 or >600 || !new[]{"Вечерний джаз","Караоке-вечеринка","Тихий ужин"}.Contains(value.Mood)) throw new RuleException("Проверьте настройки: лимит 1–5, длительность 60–600 секунд.");
        state.Settings=value; Save();
    }}
    public void Tick() { lock(gate) {
        CloseVisits(state.Visits.Values.Where(s => s.Expires <= DateTimeOffset.UtcNow).Select(s => s.Id).ToArray());
        var playing=state.Orders.FirstOrDefault(o=>o.Status=="playing");
        if(playing!=null && playing.Started!.Value.AddSeconds(tracks[playing.TrackId].Duration)<=DateTimeOffset.UtcNow) {Complete(playing);Save();}
    }}
    private static void Complete(RequestItem order) {
        order.Status = "completed";
        order.Completed = DateTimeOffset.UtcNow;
    }
    public RestaurantTable[] Tables() { lock(gate) return state.Tables.ToArray(); }
    public string TableToken(int number) { lock(gate) return state.Tables.FirstOrDefault(t=>t.Number==number)?.Token ?? throw new RuleException("Столик не найден.",404); }
    public string Csv() { lock(gate) {
        static string Cell(string s) {
            if (s.TrimStart() is ['=' or '+' or '-' or '@', ..]) s = "'" + s;
            return "\"" + s.Replace("\"","\"\"") + "\"";
        }
        return "Дата;Столик;Композиция;Исполнитель;Статус;Голоса\r\n"+string.Join("\r\n",state.Orders.Select(o=> {var t=tracks[o.TrackId];return $"{o.Created:O};{o.Table};{Cell(t.Title)};{Cell(t.Artist)};{o.Status};{o.Votes.Count}";}));
    }}
}
public class QueueClock(MusicStore store) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        while(await timer.WaitForNextTickAsync(stoppingToken)) store.Tick();
    }
}
