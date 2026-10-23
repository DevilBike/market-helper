using Dalamud.Memory;
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
}
