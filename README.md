# Market Helper

A personal Dalamud plugin for FINAL FANTASY XIV that:

- values everything in your inventory and retainers using market board data, and sorts it into **Sell on MB**, **Vendor** and **Keep**
- organizes retainer inventories by rules you set, across all your characters
- lists and reprices retainer market items in batches, at the lowest price minus your undercut, with random delays between actions

Status: all features are in. Batch listing is new and needs in-game testing.

## Retainer organizing

On the Retainers tab, pick a retainer and tick any number of broad groups it should hold (Consumables, Crafting materials, Equipment, Tokens, Housing, Collectibles, Materia, Seasonal, Fishing tackle), add detailed categories for whatever is left over, and mark one retainer per character as the catch-all. Each character has its own rules: a group or category lives in one retainer per character. The most specific rule wins: a single item, then a category, then a group, then the catch-all. Groups follow the game's own item categories, so every item is in exactly one group (Miscellany items such as coffers stay in the Miscellany category).

The tab shows one character at a time (the one you're logged in as, unless you pick another). By default each character's items stay with that character's retainers. Tick **Combine characters** to let rules send items to another character's retainers (those moves need trades); a rule on the item's own character still wins when two are equally specific.

The Moves list shows what belongs elsewhere and can be searched and filtered. **Across characters** lists tradable items that several characters hold in small amounts, and which character has the most, so you can trade them together. While a retainer is open, the top of the tab lists what to take out of it and put into it. Right-click a move to pin that item to a specific retainer.

Retainers are listed in their in-game order (retainers that aren't paid for are marked locked and skipped), with how long ago each was last checked. Each group and category shows how many stacks the character has of it, and the biggest categories without a rule are suggested with an Add button. A retainer with more than fits gets a **Make room** list of its biggest categories and groups, marking the smallest one that frees enough, with a button to hand it to another retainer that has room. A retainer can be ignored (it's left alone and shown greyed out at the bottom) or given an overflow retainer that takes new stacks once it's full. **Copy to other characters** copies each retainer's rules to the retainer in the same list position on your other characters, and can do so automatically for characters with no rules yet.

With the retainer list open at a summoning bell:

- **Check all retainers** opens each retainer's inventory in turn and records it.
- **Sort this character's retainers** first shows what it will do, then on **Go** goes down the retainer list visiting each retainer once: it puts away what's in your bags for that retainer, then takes out what belongs further down the list until your bags reach the **Bag limit** (default 120 of 140 slots). If a retainer is full, it swaps out stacks that belong elsewhere to make room. Rounds alternate up and down the list until nothing is left to move, topping up partial stacks before using empty slots. Items a retainer currently has listed on the market board stay in that retainer (other copies of the item are sorted as usual), and gear you or your retainers are wearing is never touched. **Take out all equipment and coffers** empties every retainer's gear and coffers into your bags until they're full. A unique item (only one allowed per container) goes to the next retainer in the list that doesn't have one yet. With **Store crystals too**, your shards, crystals and clusters go into the first retainer in the list, then the next as each fills (a kind only goes in when all of it fits). Moves to another character still need a trade. **Top up crystals** takes crystals back from the retainers holding the fewest of each kind first until you have the amount you set (optionally after every Sort). **Copy last run's log** copies the last Sort's every visit and move with times, to paste back for tuning; **Sorting logs** lists the last 50 runs to read or copy. The game's own item sort runs on each retainer's inventory as it closes and on your bags once you leave the bell (turn it off in settings).

Both click through the retainer greetings and use the same random delays as listing; the status line shows the current step.

## Inventory

**Keep on me** lists the groups and single items (in collapsible sections) that should stay in your bags. Everything else in your bags shows up in Retainers > Moves going to the retainer its rules name (you can turn that off), and any kept item found in a retainer shows up there to be taken out. Items ticked **1 stack** keep one stack in your bags that Sort tops up from your retainers, with the extra stacks going to their retainer. Tick items from your bags (searchable, sorted by name, group, quantity or kept first), or right-click an item on the Valuation or Moves list.

**Saddlebag** (its own tab) lists the groups and single items that live in your saddlebag, with what's in the saddlebag and in your bags shown separately. Sort takes them out of retainers and puts them in the saddlebag at the end, and sends everything else in the saddlebag to its retainer. Buttons there fill or empty the saddlebag anywhere while its window is open. Items are only ever moved in or out of the saddlebag with its window open, since moving them with it closed disconnects you (error 90002); if it's closed during Sort, the saddlebag steps are skipped and noted in the log, and you can use the buttons afterwards.

**Cleanup ideas** lists kinds of items you have only a few of, and old items below an item level you pick, with their worth and where they are. **Armory cleanup** lists items in your armory chest that none of your gearsets use, leaving soul crystals and anything you've chosen to keep in the armory alone (right-click an item to keep it there), and moves them into free bag slots with one click, with the same random delays as listing.

## Valuation

Your bags and saddlebags are recorded whenever they change. Open each retainer's inventory once to record it; this works on every character you log into. Prices come from [Universalis](https://universalis.app) for each character's home world.

An item is **Sell on MB** when its stack is worth at least the minimum value in settings, it sells at least the minimum times per day, and the market pays more than a vendor. Otherwise it is **Vendor** if a vendor buys it, or **Keep**. Right-click an item to always keep it.

## Batch listing

Open a retainer at a summoning bell and choose "Sell items in your inventory on the market", then use the Listing tab:

- **Reprice all listings** opens each listing, compares prices, and sets it to the lowest price from someone else minus your undercut.
- **List selected items** puts bag items marked Sell on MB up for sale the same way, filling the retainer's free slots.

**Sell to the vendor** has the retainer sell every bag item marked Vendor (except items you keep on you) in one click.

HQ items are priced against HQ listings only. Your own retainers on any character are never undercut. An item is left alone if the market is at or below the vendor price, or if the lowest listing is below the "% of average" safety setting. Every action waits a random time between the min and max delay in settings.

## Install

1. In game, type `/xlsettings` and open **Experimental**.
2. Under **Custom Plugin Repositories**, add
   `https://github.com/DevilBike/market-helper/releases/latest/download/repo.json`,
   tick **Enabled**, and click **Save and Close**.
3. Type `/xlplugins`, search for **Market Helper**, and install it.
4. Type `/mhelper` to open it, or `/mhelper config` for settings. Settings > Backup copies your settings and rules to the clipboard and restores them.

Every push to `main` builds a new release, and Dalamud picks up the update automatically.

## Building locally

Requires the .NET 10 SDK and XIVLauncher with Dalamud run at least once. Run `dotnet build -c Release`; the plugin lands in `MarketHelper/bin/x64/Release/MarketHelper/`.
