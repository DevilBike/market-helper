using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketHelper.Inventory;
using MarketHelper.Listing;
using AddonSheet = Lumina.Excel.Sheets.Addon;

namespace MarketHelper.Organizing;

// At a summoning bell, opens each retainer's inventory in turn so it gets recorded
public sealed unsafe class RetainerTour : IDisposable
{
    private const string ListAddon = "RetainerList";
    private const string MenuAddon = "SelectString";
    private const string TalkAddon = "Talk";
    private static readonly string[] InventoryAddons = ["InventoryRetainerLarge", "InventoryRetainer"];

    private readonly Plugin plugin;
    private readonly StepRunner runner;
    private readonly string entrustText;
    private readonly string quitText;

    private int total;
    private int done;

    public string Status { get; private set; } = string.Empty;
    public bool IsRunning => runner.IsRunning;

    public RetainerTour(Plugin plugin)
    {
        this.plugin = plugin;
        runner = new StepRunner(() => (plugin.Configuration.MinActionDelayMs, plugin.Configuration.MaxActionDelayMs));
        runner.Failed += step => Status = $"Stopped after {done} of {total}: {step} didn't work.";

        // Addon rows 2378 "Entrust or withdraw items." and 2383 "Quit.", in the client's language
        var sheet = Plugin.DataManager.GetExcelSheet<AddonSheet>();
        entrustText = sheet.TryGetRow(2378, out var entrust) ? entrust.Text.ToString().TrimEnd('.') : "Entrust or withdraw items";
        quitText = sheet.TryGetRow(2383, out var quit) ? quit.Text.ToString().TrimEnd('.') : "Quit";
    }

    public void Dispose() => runner.Dispose();

    public static bool CanStart => GameUi.IsOpen(ListAddon);

    public void Start()
    {
        if (IsRunning || !CanStart)
            return;

        var manager = RetainerManager.Instance();
        total = manager == null ? 0 : manager->GetRetainerCount();
        done = 0;
        Status = $"Checking {total} retainers...";

        for (var i = 0; i < total; i++)
        {
            var index = i;
            runner.Add("Pick retainer", () =>
            {
                var list = GameUi.GetReady(ListAddon);
                if (list == null)
                    return StepResult.Wait;
                GameUi.SelectRetainer(list, index);
                return StepResult.Done;
            });

            runner.Add("Open the retainer menu", () => PastTalk(MenuAddon), false);
            runner.Add("Choose Entrust or withdraw items", () => Choose(entrustText));

            runner.Add("Wait for the retainer inventory", () =>
            {
                if (OpenInventory() == null || !InventoryScanner.RetainerPagesLoaded())
                    return StepResult.Wait;
                plugin.Scanner.ScanNow();
                return StepResult.Done;
            }, false);

            runner.Add("Close the retainer inventory", () =>
            {
                var inventory = OpenInventory();
                if (inventory == null)
                    return StepResult.Wait;
                inventory->Close(true);
                return StepResult.Done;
            });

            runner.Add("Back to the retainer menu", () => PastTalk(MenuAddon), false);
            runner.Add("Choose Quit", () => Choose(quitText));

            runner.Add("Back to the retainer list", () =>
            {
                var result = PastTalk(ListAddon);
                if (result == StepResult.Done)
                {
                    done++;
                    Status = done == total ? $"Checked all {total} retainers." : $"Checked {done} of {total}.";
                }
                return result;
            }, false);
        }
    }

    public void Stop()
    {
        runner.Stop();
        Status = $"Stopped after {done} of {total}.";
    }

    private static AtkUnitBase* OpenInventory()
    {
        foreach (var name in InventoryAddons)
        {
            var addon = GameUi.GetReady(name);
            if (addon != null)
                return addon;
        }

        return null;
    }

    // Clicks through the retainer's greeting until the wanted window shows up
    private StepResult PastTalk(string target)
    {
        if (GameUi.IsOpen(target))
            return StepResult.Done;

        var talk = GameUi.GetReady(TalkAddon);
        if (talk != null)
        {
            GameUi.ClickTalk(talk);
            runner.Pause(250, 600);
        }

        return StepResult.Wait;
    }

    private static StepResult Choose(string text)
    {
        var menu = GameUi.GetReady(MenuAddon);
        if (menu == null)
            return StepResult.Wait;

        var index = GameUi.FindSelectStringEntry(menu, text);
        if (index < 0)
            return StepResult.Fail;

        GameUi.Fire(menu, true, index);
        return StepResult.Done;
    }
}
