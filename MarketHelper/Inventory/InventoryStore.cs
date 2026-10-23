using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MarketHelper.Inventory;

// Remembers every character's and retainer's items between sessions, so items
// on characters you aren't logged into still show up.
public class InventoryStore
{
    private readonly string path;

    public Dictionary<ulong, CharacterRecord> Characters { get; private set; } = new();

    public event Action? Changed;

    public InventoryStore(string configDirectory)
    {
        path = Path.Combine(configDirectory, "inventories.json");
        Load();
    }

    public CharacterRecord GetOrAdd(ulong contentId)
    {
        if (!Characters.TryGetValue(contentId, out var record))
        {
            record = new CharacterRecord { ContentId = contentId };
            Characters[contentId] = record;
        }

        return record;
    }

    public void Remove(ulong contentId)
    {
        if (Characters.Remove(contentId))
            Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Characters, Formatting.Indented));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not save inventories");
        }

        Changed?.Invoke();
    }

    private void Load()
    {
        if (!File.Exists(path))
            return;

        try
        {
            Characters = JsonConvert.DeserializeObject<Dictionary<ulong, CharacterRecord>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not read inventories, starting fresh");
        }
    }
}
