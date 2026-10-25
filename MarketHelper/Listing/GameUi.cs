using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketHelper.Listing;

// Small helpers for reading and pressing the game's own windows.
public static unsafe class GameUi
{
    public static AtkUnitBase* GetReady(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name, 1);
        if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
            return null;
        return (AtkUnitBase*)addon.Address;
    }

    public static bool IsOpen(string name) => GetReady(name) != null;

    // Sends the same event the window sends itself when you click something in it
    public static void Fire(AtkUnitBase* addon, bool updateState, params int[] args)
    {
        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            values[i].Type = AtkValueType.Int;
            values[i].Int = args[i];
        }

        addon->FireCallback((uint)args.Length, values, updateState);
    }

    public static bool ClickButton(AtkUnitBase* addon, uint nodeId)
    {
        var button = addon->GetComponentButtonById(nodeId);
        if (button == null || !button->IsEnabled)
            return false;

        var resNode = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = resNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        // Some windows (RetainerSell's Confirm) read the event data, so it can't be null
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
        return true;
    }

    // Context menu entries start at value 8; value 0 holds how many there are
    public static int FindContextEntry(AtkUnitBase* menu, string text)
    {
        var count = (int)menu->AtkValues[0].UInt;
        for (var i = 0; i < count; i++)
        {
            var value = menu->AtkValues[8 + i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString))
                continue;

            var entry = MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
            if (entry == text)
                return i;
        }

        return -1;
    }

    public static void SelectContextEntry(AtkUnitBase* menu, int index) => Fire(menu, true, 0, index, 0);

    public static List<string> SelectStringEntries(AtkUnitBase* addon)
    {
        var select = (AddonSelectString*)addon;
        var entries = new List<string>();
        for (var i = 0; i < select->PopupMenu.PopupMenu.EntryCount; i++)
            entries.Add(MemoryHelper.ReadSeStringNullTerminated((nint)select->PopupMenu.PopupMenu.EntryNames[i].Value).TextValue);
        return entries;
    }

    // SelectString lists choices like "Entrust or withdraw items."; matched on letters and digits only
    public static int FindSelectStringEntry(AtkUnitBase* addon, string text)
    {
        var wanted = Simplify(text);
        var entries = SelectStringEntries(addon);
        return wanted.Length == 0 ? -1 : entries.FindIndex(e => Simplify(e).StartsWith(wanted));
    }

    private static string Simplify(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Advances a speech bubble, like clicking on it
    public static void ClickTalk(AtkUnitBase* addon)
    {
        var evt = stackalloc AtkEvent[1];
        evt->Listener = (AtkEventListener*)addon;
        evt->Target = &AtkStage.Instance()->AtkEventTarget;
        evt->State.StateFlags = (AtkEventStateFlags)132;
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
    }

    // RetainerList: picking a row sends 2, the row, then two empty values
    public static void SelectRetainer(AtkUnitBase* list, int index)
    {
        var values = stackalloc AtkValue[4];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 2;
        values[1].Type = AtkValueType.UInt;
        values[1].UInt = (uint)index;
        values[2].Type = 0;
        values[3].Type = 0;
        list->FireCallback(4, values, true);
    }

    // RetainerList: each row is 10 values from value 3; value 8 of a row is false when it's greyed out.
    // Only trusted when the row is also past the number of retainers the account pays for.
    public static bool IsRetainerRowLocked(AtkUnitBase* list, int row, int entitlement)
    {
        var index = 3 + 10 * row + 8;
        if (index >= list->AtkValuesCount)
            return false;

        var value = list->AtkValues[index];
        if (value.Type != AtkValueType.Bool || value.Byte != 0)
            return false;

        return entitlement <= 0 || row >= entitlement;
    }
}
