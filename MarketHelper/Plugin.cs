using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketHelper.Inventory;
using MarketHelper.Market;
using MarketHelper.Organizing;
using MarketHelper.Windows;

namespace MarketHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    private const string CommandName = "/mhelper";

    public Configuration Configuration { get; init; }
    public InventoryStore Store { get; init; }
    public InventoryScanner Scanner { get; init; }
    public UniversalisClient Universalis { get; init; }
    public Valuator Valuator { get; init; }
    public Organizer Organizer { get; init; }

    public readonly WindowSystem WindowSystem = new("MarketHelper");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Store = new InventoryStore(PluginInterface.GetPluginConfigDirectory());
        Scanner = new InventoryScanner(Store);
        Universalis = new UniversalisClient();
        Valuator = new Valuator(Universalis);
        Organizer = new Organizer(Valuator);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Market Helper. Use \"/mhelper config\" for settings."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"{PluginInterface.Manifest.Name} {PluginInterface.Manifest.AssemblyVersion} loaded");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Scanner.Dispose();
        Universalis.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", System.StringComparison.OrdinalIgnoreCase))
            ToggleConfigUi();
        else
            ToggleMainUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
