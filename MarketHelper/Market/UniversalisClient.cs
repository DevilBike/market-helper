using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MarketHelper.Market;

public class MarketData
{
    [JsonPropertyName("itemID")] public uint ItemId { get; set; }
    [JsonPropertyName("minPriceNQ")] public double MinPriceNq { get; set; }
    [JsonPropertyName("minPriceHQ")] public double MinPriceHq { get; set; }
    [JsonPropertyName("averagePriceNQ")] public double AveragePriceNq { get; set; }
    [JsonPropertyName("averagePriceHQ")] public double AveragePriceHq { get; set; }
    [JsonPropertyName("nqSaleVelocity")] public double NqSaleVelocity { get; set; }
    [JsonPropertyName("hqSaleVelocity")] public double HqSaleVelocity { get; set; }

    [JsonIgnore] public DateTime FetchedUtc { get; set; }

    // Lowest current listing, falling back to the recent average when nothing is listed
    public double UnitPrice(bool hq)
    {
        var min = hq ? MinPriceHq : MinPriceNq;
        if (min > 0)
            return min;
        return hq ? AveragePriceHq : AveragePriceNq;
    }

    public double SalesPerDay(bool hq) => hq ? HqSaleVelocity : NqSaleVelocity;

    public double AveragePrice(bool hq) => hq ? AveragePriceHq : AveragePriceNq;
}

// Prices from universalis.app, cached per world.
public sealed class UniversalisClient : IDisposable
{
    private const int BatchSize = 100;

    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri("https://universalis.app/api/v2/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly ConcurrentDictionary<(string World, uint ItemId), MarketData> cache = new();
    private readonly SemaphoreSlim fetchLock = new(1, 1);

    public bool IsFetching => fetchLock.CurrentCount == 0;
    public string? LastError { get; private set; }

    public UniversalisClient()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MarketHelper/0.1 (+https://github.com/DevilBike/market-helper)");
    }

    public void Dispose()
    {
        http.Dispose();
        fetchLock.Dispose();
    }

    public MarketData? Get(string world, uint itemId) =>
        cache.TryGetValue((world, itemId), out var data) ? data : null;

    // Fetches every item that has no price yet or one older than maxAge
    public async Task RefreshAsync(string world, IEnumerable<uint> itemIds, TimeSpan maxAge, bool force = false)
    {
        await fetchLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var stale = itemIds.Distinct()
                .Where(id => force || Get(world, id) is not { } d || now - d.FetchedUtc > maxAge)
                .ToList();

            LastError = null;
            foreach (var batch in stale.Chunk(BatchSize))
                await FetchBatch(world, batch);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning(ex, "Universalis request failed");
        }
        finally
        {
            fetchLock.Release();
        }
    }

    private async Task FetchBatch(string world, uint[] ids)
    {
        var url = $"{Uri.EscapeDataString(world)}/{string.Join(',', ids)}?listings=0&entries=0";
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var now = DateTime.UtcNow;

        // One id returns the item itself; several return them under "items"
        IEnumerable<JsonElement> elements = doc.RootElement.TryGetProperty("items", out var items)
            ? items.EnumerateObject().Select(p => p.Value)
            : [doc.RootElement];

        foreach (var element in elements)
        {
            var data = element.Deserialize<MarketData>();
            if (data == null || data.ItemId == 0)
                continue;
            data.FetchedUtc = now;
            cache[(world, data.ItemId)] = data;
        }

        // Remember items Universalis doesn't know about so we don't ask again every refresh
        foreach (var id in ids.Where(id => Get(world, id) is not { } d || d.FetchedUtc != now))
            cache[(world, id)] = new MarketData { ItemId = id, FetchedUtc = now };
    }
}
