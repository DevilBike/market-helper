# Market Helper

A personal Dalamud plugin for FINAL FANTASY XIV that:

- values everything in your inventory and retainers using market board data, and sorts it into **Sell on MB**, **Vendor** and **Keep**
- organizes retainer inventories by rules you set, across all your characters
- lists and reprices retainer market items in batches, at the lowest price minus your undercut, with random delays between actions

Status: all features are in. Batch listing is new and needs in-game testing.

## Retainer organizing

On the Retainers tab, pick a retainer and tick any number of broad groups it should hold (Consumables, Crafting materials, Equipment, Tokens, Housing, Collectibles and unlocks, Materia, Seasonal, Fishing tackle), add detailed categories for whatever is left over, and mark one retainer as the catch-all. The most specific rule wins: a single item, then a category, then a group, then the catch-all. The Moves list shows what belongs elsewhere, including trades between characters. While a retainer is open, the top of the tab lists what to take out of it and put into it. Right-click a move to pin that item to a specific retainer.

## Valuation

Your bags and saddlebags are recorded whenever they change. Open each retainer's inventory once to record it; this works on every character you log into. Prices come from [Universalis](https://universalis.app) for each character's home world.

An item is **Sell on MB** when its stack is worth at least the minimum value in settings, it sells at least the minimum times per day, and the market pays more than a vendor. Otherwise it is **Vendor** if a vendor buys it, or **Keep**. Right-click an item to always keep it.

## Batch listing

Open a retainer at a summoning bell and choose "Sell items in your inventory on the market", then use the Listing tab:

- **Reprice all listings** opens each listing, compares prices, and sets it to the lowest price from someone else minus your undercut.
- **List selected items** puts bag items marked Sell on MB up for sale the same way, filling the retainer's free slots.

HQ items are priced against HQ listings only. Your own retainers on any character are never undercut. An item is left alone if the market is at or below the vendor price, or if the lowest listing is below the "% of average" safety setting. Every action waits a random time between the min and max delay in settings.

## Install

1. In game, type `/xlsettings` and open **Experimental**.
2. Under **Custom Plugin Repositories**, add
   `https://github.com/DevilBike/market-helper/releases/latest/download/repo.json`,
   tick **Enabled**, and click **Save and Close**.
3. Type `/xlplugins`, search for **Market Helper**, and install it.
4. Type `/mhelper` to open it, or `/mhelper config` for settings.

Every push to `main` builds a new release, and Dalamud picks up the update automatically.

## Building locally

Requires the .NET 10 SDK and XIVLauncher with Dalamud run at least once. Run `dotnet build -c Release`; the plugin lands in `MarketHelper/bin/x64/Release/MarketHelper/`.
