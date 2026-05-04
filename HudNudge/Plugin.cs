using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HudNudge.Windows;
using System;
using System.Runtime.InteropServices;

namespace HudNudge;

public unsafe sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/hudnudge";
    private const double FirstRepeatDelay = 0.50;
    private const double RepeatDelay = 0.10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyZ = 0x5A;
    private const int VirtualKeyY = 0x59;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IKeyState KeyState { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("HudNudge");

    private readonly HudDebugWindow hudDebugWindow;
    private readonly HudEditorWindow hudEditorWindow;
    private readonly HudLayoutController hudLayout;
    private readonly HudSnapController hudSnap;
    private readonly string[] hudLayoutRefreshAddons = ["HudLayout", "_HudLayoutWindow", "_HudLayoutScreen"];

    private bool movementKeysHeld;
    private bool undoKeysHeld;
    private bool redoKeysHeld;
    private double lastMovementTime;
    private bool logHudLayoutEvents;

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public Plugin()
    {
        hudLayout = new HudLayoutController();
        hudSnap = new HudSnapController(hudLayout);
        hudEditorWindow = new HudEditorWindow(hudLayout, hudSnap);
        hudDebugWindow = new HudDebugWindow(
            hudLayout,
            () => logHudLayoutEvents,
            value => logHudLayoutEvents = value);

        WindowSystem.AddWindow(hudEditorWindow);
        WindowSystem.AddWindow(hudDebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the HUD editor."
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenHudLayoutEditor;
        Framework.Update += OnFrameworkUpdate;
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, hudLayoutRefreshAddons, OnHudLayoutRefresh);
        AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "_HudLayoutScreen", OnHudLayoutReceiveEvent);
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "_HudLayoutScreen", OnHudLayoutReceiveEvent);
        AddonLifecycle.UnregisterListener(OnHudLayoutRefresh);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenMainUi -= OpenHudLayoutEditor;
        PluginInterface.UiBuilder.Draw -= Draw;
        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        hudDebugWindow.Dispose();
        hudEditorWindow.Dispose();
        hudLayout.Dispose();
    }

    private static bool IsPhysicalKeyDown(VirtualKey key)
        => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    internal static void ClickScreenPosition(int x, int y)
    {
        GetCursorPos(out var originalPosition);
        SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        SetCursorPos(originalPosition.X, originalPosition.Y);
    }

    private void Draw()
    {
        hudEditorWindow.IsOpen = hudLayout.IsHudLayoutOpen;
        hudEditorWindow.HideExtraTitleBarButtons();
        WindowSystem.Draw();
    }

    private void OpenHudLayoutEditor()
    {
        hudLayout.OpenHudLayoutEditor();
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (string.IsNullOrEmpty(args))
        {
            OpenHudLayoutEditor();
            return;
        }

        if (args.Equals("dev", StringComparison.OrdinalIgnoreCase)
            || args.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            hudDebugWindow.Toggle();
            return;
        }

        if (args.Equals("logevents", StringComparison.OrdinalIgnoreCase))
        {
            logHudLayoutEvents = !logHudLayoutEvents;
            Log.Information($"HUD layout event logging {(logHudLayoutEvents ? "enabled" : "disabled")}.");
            return;
        }

        hudLayout.HandleCommand(args);
    }

    private void OnHudLayoutReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!logHudLayoutEvents || args is not AddonReceiveEventArgs eventArgs)
            return;

        var selectedNodeId = hudLayout.TryGetSelectedNodeId(out var nodeId)
                                 ? nodeId.ToString()
                                 : "none";

        Log.Information(
            $"HUD layout event: Type={eventArgs.AtkEventType}, Param={eventArgs.EventParam}, SelectedNode={selectedNodeId}");
    }

    private void OnHudLayoutRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is AddonRefreshArgs refreshArgs)
        {
            hudLayout.HandleHudLayoutRefresh(refreshArgs.AtkValues, refreshArgs.AtkValueCount);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        hudLayout.UpdateHudEditorElementDiscovery();

        if (hudLayout.IsHudLayoutOpen)
        {
            hudLayout.ContinuePendingUndoRestore();
            hudLayout.UpdateExternalMovementTracking();
        }

        if (!ShouldHandleKeyboardMovement())
            return;

        if (HandleUndoHotkey())
            return;

        if (HandleRedoHotkey())
            return;

        var left = IsPhysicalKeyDown(VirtualKey.LEFT);
        var right = IsPhysicalKeyDown(VirtualKey.RIGHT);
        var up = IsPhysicalKeyDown(VirtualKey.UP);
        var down = IsPhysicalKeyDown(VirtualKey.DOWN);

        if (!left && !right && !up && !down)
        {
            movementKeysHeld = false;
            return;
        }

        BlockGameArrowKeyInput();

        if (!ShouldMoveNow())
            return;

        var step = hudEditorWindow.MoveStep;
        var dx = 0;
        var dy = 0;

        if (left) dx -= step;
        if (right) dx += step;
        if (up) dy -= step;
        if (down) dy += step;

        hudLayout.MoveSelected(dx, dy);
    }

    private bool HandleUndoHotkey()
    {
        var isUndoDown = (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0
                         && (GetAsyncKeyState(VirtualKeyZ) & 0x8000) != 0;

        if (!isUndoDown)
        {
            undoKeysHeld = false;
            return false;
        }

        BlockGameArrowKeyInput();
        if (undoKeysHeld)
            return true;

        undoKeysHeld = true;
        hudLayout.UndoLastMove();
        return true;
    }

    private bool HandleRedoHotkey()
    {
        var isRedoDown = (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0
                         && (GetAsyncKeyState(VirtualKeyY) & 0x8000) != 0;

        if (!isRedoDown)
        {
            redoKeysHeld = false;
            return false;
        }

        BlockGameArrowKeyInput();
        if (redoKeysHeld)
            return true;

        redoKeysHeld = true;
        hudLayout.RedoLastMove();
        return true;
    }

    private bool ShouldHandleKeyboardMovement()
    {
        return hudLayout.IsHudLayoutOpen
               && hudEditorWindow.IsOpen
               && !ImGui.GetIO().WantTextInput;
    }

    private bool ShouldMoveNow()
    {
        var now = ImGui.GetTime();

        if (!movementKeysHeld)
        {
            movementKeysHeld = true;
            lastMovementTime = now;
            return true;
        }

        var elapsed = now - lastMovementTime;

        if (elapsed < FirstRepeatDelay)
            return false;

        if (elapsed < FirstRepeatDelay + RepeatDelay)
            return false;

        lastMovementTime = now - FirstRepeatDelay;
        return true;
    }

    private static void BlockGameArrowKeyInput()
    {
        KeyState[VirtualKey.LEFT] = false;
        KeyState[VirtualKey.RIGHT] = false;
        KeyState[VirtualKey.UP] = false;
        KeyState[VirtualKey.DOWN] = false;
    }
}
