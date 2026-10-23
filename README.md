# Market Helper

A personal Dalamud plugin for FINAL FANTASY XIV that:

- values everything in your inventory and retainers using market board data, and sorts it into **Sell on MB**, **Vendor** and **Keep**
- organizes retainer inventories by rules you set, across all your characters
- lists and reprices retainer market items in batches, at the lowest price minus your undercut, with random delays between actions

Status: skeleton only. Features arrive step by step.

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
