using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ReplayDrill.Geometry;
using ReplayDrill.Services;
using ReplayDrill.Windows;

namespace ReplayDrill;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/rdrill";

    /// <summary>ウィンドウのタイトルに出すバージョン（csproj の Version から取る）。</summary>
    public static string VersionText { get; } = $"v{typeof(Plugin).Assembly.GetName().Version?.ToString(3)}";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("ReplayDrill");
    private readonly GhostController ghost;
    private readonly HitJudge judge;
    private readonly GhostOverlay overlay;
    private readonly MainWindow mainWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        ICondition condition,
        IKeyState keyState,
        IGamepadState gamepad,
        IObjectTable objectTable,
        IGameGui gameGui,
        IDataManager dataManager,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;

        var configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(pluginInterface);

        this.ghost = new GhostController(configuration, framework, condition, keyState, gamepad, objectTable, interop, log);
        this.judge = new HitJudge(this.ghost, new AoeGeometryResolver(dataManager), objectTable, framework, interop, log);
        this.overlay = new GhostOverlay(this.ghost, this.judge, gameGui);
        this.mainWindow = new MainWindow(configuration, this.ghost, this.judge);
        this.windowSystem.AddWindow(this.mainWindow);

        pluginInterface.UiBuilder.Draw += this.Draw;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenMainUi;

        commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "ReplayDrill を開きます。",
        });
    }

    public void Dispose()
    {
        this.commandManager.RemoveHandler(CommandName);
        this.pluginInterface.UiBuilder.Draw -= this.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenMainUi;
        this.windowSystem.RemoveAllWindows();
        this.judge.Dispose();
        this.ghost.Dispose();
    }

    private void Draw()
    {
        this.overlay.Draw();
        this.windowSystem.Draw();
    }

    private void OpenMainUi() => this.mainWindow.IsOpen = true;

    private void OnCommand(string command, string args) => this.mainWindow.Toggle();
}
