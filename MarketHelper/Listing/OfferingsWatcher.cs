using System;
using System.Collections.Generic;
using Dalamud.Game.Network.Structures;

namespace MarketHelper.Listing;

// Collects the market board listings the game receives after "Compare Prices".
public sealed class OfferingsWatcher : IDisposable
{
    private static readonly TimeSpan QuietTime = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan NoReplyTime = TimeSpan.FromSeconds(3);

    private readonly List<IMarketBoardItemListing> listings = new();
    private bool armed;
    private int requestId = -1;
    private DateTime armedAt;
    private DateTime lastPacket;

    public OfferingsWatcher()
    {
        Plugin.MarketBoard.OfferingsReceived += OnOfferings;
    }

    public void Dispose() => Plugin.MarketBoard.OfferingsReceived -= OnOfferings;

    public void Arm()
    {
        listings.Clear();
        requestId = -1;
        armed = true;
        armedAt = DateTime.UtcNow;
    }

    // Null while still waiting. An item nobody is selling sends no listings at all,
    // so silence after a few seconds means an empty market.
    public List<IMarketBoardItemListing>? TryGet()
    {
        var now = DateTime.UtcNow;
        if (requestId == -1)
            return now - armedAt > NoReplyTime ? new List<IMarketBoardItemListing>() : null;

        return now - lastPacket > QuietTime ? new List<IMarketBoardItemListing>(listings) : null;
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        if (!armed)
            return;

        if (requestId == -1)
            requestId = offerings.RequestId;
        else if (offerings.RequestId != requestId)
            return;

        listings.AddRange(offerings.ItemListings);
        lastPacket = DateTime.UtcNow;
    }
}
