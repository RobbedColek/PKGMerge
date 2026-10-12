using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKGMerge.Repositories.Settings;

public interface ISettingsRepository
{
    T? Get<T>(string key);

    void Set<T>(string key, T value);
}

public sealed class SettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, JsonNode?> _values;

    public SettingsRepository()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PKGMerge", "settings.json"))
    {
        //
    }

    public SettingsRepository(string path)
    {
        _path = path;
        _values = Load(path);
    }

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            try
            {
                return _values.TryGetValue(key, out JsonNode? node) && node is not null
                    ? node.Deserialize<T>(JsonOptions)
                    : default;
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _values[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
            Save();
        }
    }

    private static Dictionary<string, JsonNode?> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings fall back to defaults.
        }

        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_values, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are for convenience, never fail the app because of some issue with saving.
        }
    }
}
