namespace Encore.Core;

public sealed class AudioUploads(IWebHostEnvironment environment, ILogger<AudioUploads> log) {
    private readonly string directory = Directory.CreateDirectory(Path.Combine(DataDirectory(environment), "uploads")).FullName;

    public static string DataDirectory(IWebHostEnvironment env) =>
        Environment.GetEnvironmentVariable("ENCORE_DATA_DIR") ?? Path.Combine(env.ContentRootPath, "App_Data");

    public void Remove(string key) {
        try { File.Delete(Path.Combine(directory, key)); }
        catch (IOException ex) { log.LogWarning(ex, "Unable to remove expired audio file"); }
        catch (UnauthorizedAccessException ex) { log.LogWarning(ex, "Unable to remove expired audio file"); }
    }

    public void RemoveOrphans(HashSet<string> retained) {
        foreach (var name in Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()) {
            if (!retained.Contains(name) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _) &&
                Path.GetExtension(name) is ".mp3" or ".wav") Remove(name);
        }
    }
}
