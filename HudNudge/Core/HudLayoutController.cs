using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HudNudge
{
    public readonly record struct HudEditorElement(ushort Id, string AddonName);

    public readonly record struct HudUndoEntry(
        Vector2 Position,
        uint NodeId,
        ushort HudId,
        string Name);

    public unsafe sealed class HudLayoutController : IDisposable
    {
        private const uint SaveButtonNodeId = 16;
        public const int HudNodeIdMin = 20000;
        public const int HudNodeIdMax = 20100;
        public const int MinSnapNodeSize = 20;
        private const int MaxUndoHistory = 100;
        private const ushort HudEditorScanMinId = 0;
        private const ushort HudEditorScanMaxId = 100;
        private const int HudEditorScanWarmupFrames = 20;
        private const int HudEditorScanWaitFrames = 4;
        private const string HudEditorScanResetName = "_ActionBar";
        private const int UndoRestoreMaxWaitFrames = 30;
        private const double VanillaMoveSettleDelay = 0.35;

        public bool IsHudLayoutOpen => Plugin.GameGui.GetAddonByName("_HudLayoutScreen") != nint.Zero;

        private readonly List<HudUndoEntry> undoHistory = new();
        private readonly List<HudUndoEntry> redoHistory = new();
        private string lastUndoReason = string.Empty;
        private readonly List<HudEditorElement> hudEditorElements = new();
        private readonly Dictionary<string, ushort> hudEditorIdsByAddonName = new(StringComparer.Ordinal);
        private HudUndoEntry? pendingUndoRestore;
        private bool pendingUndoRestoreIsRedo;
        private HudUndoEntry? lastObservedEntry;
        private HudUndoEntry? pendingVanillaMoveStart;
        private HudUndoEntry? pendingVanillaMoveLatest;
        private int pendingUndoRestoreWaitFrames;
        private double lastVanillaMoveTime;
        private bool wasLeftMouseDown;
        private bool isRestoringUndo;
        private bool suppressNextObservedMovement;
        private bool wasHudLayoutOpen;
        private int lastHudEditorElementId = -1;
        private bool hudEditorScanActive;
        private int hudEditorScanWarmupFrames;
        private ushort hudEditorScanNextId = HudEditorScanMinId;
        private int hudEditorScanPendingId = -1;
        private int hudEditorScanWaitFrames;
        private bool hudEditorScanWaitingForReset;
        private bool hudEditorScanHidScreen;
        private short hudEditorScanScreenX;
        private short hudEditorScanScreenY;
        private bool hudEditorScanHidWindow;
        private short hudEditorScanWindowX;
        private short hudEditorScanWindowY;

        public int UndoCount => undoHistory.Count + (pendingUndoRestore.HasValue ? 1 : 0);
        public int RedoCount => redoHistory.Count;
        public IReadOnlyList<HudEditorElement> HudEditorElements => hudEditorElements;
        public bool LogHudNudgeDebugActions { get; set; }
        public bool IsHudEditorScanActive => hudEditorScanActive;
        public int HudEditorScanCurrentId => hudEditorScanPendingId >= 0 ? hudEditorScanPendingId : hudEditorScanNextId;
        public int MaxHudEditorScanId => HudEditorScanMaxId;
        public int HistoryRestoreVersion { get; private set; }

        private void LogDebug(string message)
        {
            if (LogHudNudgeDebugActions)
            {
                Plugin.Log.Information(message);
            }
        }

        public void HandleHudLayoutRefresh(nint valuesPtr, uint valueCount)
        {
            if (valueCount < 2
                || !TryGetAtkInt(valuesPtr, 0, out var action)
                || action != 3
                || !TryGetAtkInt(valuesPtr, 1, out var id))
            {
                return;
            }

            lastHudEditorElementId = id;
        }

        public void OpenHudLayoutEditor()
        {
            var agent = AgentHUDLayout.Instance();

            if (agent == null)
            {
                LogDebug("HUD layout agent is not available.");
                return;
            }

            agent->Show();
        }

        public void Dispose()
        {
            RestoreHudLayoutAddons();
        }

        public void UpdateHudEditorElementDiscovery()
        {
            var isOpen = IsHudLayoutOpen && GetHudLayoutCallbackWindow() != null;
            if (isOpen && !wasHudLayoutOpen)
            {
                StartHudEditorElementScan();
            }
            else if (!isOpen && wasHudLayoutOpen)
            {
                hudEditorScanActive = false;
                RestoreHudLayoutAddons();
            }

            wasHudLayoutOpen = isOpen;

            if (!hudEditorScanActive)
                return;

            if (hudEditorScanWarmupFrames > 0)
            {
                hudEditorScanWarmupFrames--;
                return;
            }

            HideHudLayoutAddons();
            StepHudEditorElementScan();
        }

        public void HandleCommand(string args)
        {
            args = args.Trim();

            if (args.Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                ClickHudSave();
                return;
            }

            if (args.Equals("undo", StringComparison.OrdinalIgnoreCase))
            {
                UndoLastMove();
                return;
            }

            if (args.Equals("redo", StringComparison.OrdinalIgnoreCase))
            {
                RedoLastMove();
                return;
            }

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 3
                && (parts[0].Equals("move", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("delta", StringComparison.OrdinalIgnoreCase))
                && int.TryParse(parts[1], out var dx)
                && int.TryParse(parts[2], out var dy))
            {
                MoveSelected(dx, dy);
                return;
            }

            if (parts.Length == 3
                && (parts[0].Equals("position", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("pos", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("set", StringComparison.OrdinalIgnoreCase))
                && int.TryParse(parts[1], out var x)
                && int.TryParse(parts[2], out var y))
            {
                SetSelectedScreenPosition(x, y);
            }
        }

        public void MoveSelected(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return;

            var screen = GetHudLayoutScreen();

            if (screen == null)
                return;

            if (!TryGetSelected(screen, out var info, out var unit))
                return;

            RecordUndoSnapshot();
            MoveOverlayByDelta(screen, dx, dy);
            unit->SetPosition((short)(unit->X + dx), (short)(unit->Y + dy));
            suppressNextObservedMovement = true;
            MarkChanged(info);
            ActivateSaveButton();
        }

        public void ClickHudSave()
        {
            var window = GetHudLayoutWindow();

            if (window == null)
                return;

            window->FireCallbackInt(8);
        }

        public bool SelectHudEditorElementById(ushort hudId)
        {
            var window = GetHudLayoutCallbackWindow();

            if (window == null)
                return false;

            var values = stackalloc AtkValue[2];
            values[0].SetInt(3);
            values[1].SetInt(hudId);
            window->FireCallback(2, values, true);
            return true;
        }

        public bool SelectHudEditorElementByName(string addonName)
        {
            if (!hudEditorIdsByAddonName.TryGetValue(addonName, out var hudId))
            {
                LogDebug($"HUD select by name failed: {addonName} not learned. Learned count: {hudEditorIdsByAddonName.Count}.");
                return false;
            }

            LogDebug($"HUD select by name: {addonName} -> id {hudId}.");
            return SelectHudEditorElementById(hudId);
        }

        private void StartHudEditorElementScan()
        {
            hudEditorElements.Clear();
            hudEditorIdsByAddonName.Clear();
            hudEditorScanActive = true;
            hudEditorScanWarmupFrames = HudEditorScanWarmupFrames;
            hudEditorScanNextId = HudEditorScanMinId;
            hudEditorScanPendingId = -1;
            hudEditorScanWaitFrames = 0;
            hudEditorScanWaitingForReset = false;
            LogDebug($"HUD editor element scan started ({HudEditorScanMinId}-{HudEditorScanMaxId}).");
        }

        private void StepHudEditorElementScan()
        {
            if (hudEditorScanPendingId >= 0)
            {
                if (hudEditorScanWaitingForReset)
                {
                    if (TryGetSelectedAddonName(out var resetName)
                        && string.Equals(resetName, HudEditorScanResetName, StringComparison.Ordinal))
                    {
                        hudEditorScanWaitingForReset = false;
                        hudEditorScanWaitFrames = 0;
                        SelectHudEditorElementById((ushort)hudEditorScanPendingId);
                        return;
                    }

                    hudEditorScanWaitFrames++;
                    if (hudEditorScanWaitFrames < HudEditorScanWaitFrames)
                        return;

                    hudEditorScanWaitingForReset = false;
                    hudEditorScanWaitFrames = 0;
                    SelectHudEditorElementById((ushort)hudEditorScanPendingId);
                    return;
                }

                if (lastHudEditorElementId == hudEditorScanPendingId && TryGetSelectedAddonName(out var addonName))
                {
                    if (hudEditorScanPendingId != HudEditorScanMinId
                        && string.Equals(addonName, HudEditorScanResetName, StringComparison.Ordinal))
                    {
                        hudEditorScanWaitFrames++;
                        if (hudEditorScanWaitFrames < HudEditorScanWaitFrames)
                            return;

                        hudEditorScanPendingId = -1;
                        hudEditorScanWaitFrames = 0;
                        return;
                    }

                    AddOrUpdateHudEditorElement((ushort)hudEditorScanPendingId, addonName);
                    hudEditorScanPendingId = -1;
                    hudEditorScanWaitFrames = 0;
                    return;
                }

                hudEditorScanWaitFrames++;
                if (hudEditorScanWaitFrames < HudEditorScanWaitFrames)
                    return;

                hudEditorScanPendingId = -1;
                hudEditorScanWaitFrames = 0;
            }

            if (hudEditorScanNextId > HudEditorScanMaxId)
            {
                hudEditorScanActive = false;
                RestoreHudLayoutAddons();
                LogDebug($"HUD editor element scan done. Learned {hudEditorElements.Count} elements.");
                return;
            }

            hudEditorScanPendingId = hudEditorScanNextId++;
            hudEditorScanWaitFrames = 0;
            hudEditorScanWaitingForReset = hudEditorScanPendingId != HudEditorScanMinId;
            SelectHudEditorElementById(hudEditorScanWaitingForReset ? HudEditorScanMinId : (ushort)hudEditorScanPendingId);
        }

        private void AddOrUpdateHudEditorElement(ushort hudId, string addonName)
        {
            hudEditorIdsByAddonName[addonName] = hudId;

            for (var i = 0; i < hudEditorElements.Count; i++)
            {
                if (hudEditorElements[i].Id != hudId)
                    continue;

                hudEditorElements[i] = new HudEditorElement(hudId, addonName);
                LogDebug($"HUD learned update: {addonName} -> id {hudId}.");
                return;
            }

            hudEditorElements.Add(new HudEditorElement(hudId, addonName));
            LogDebug($"HUD learned add: {addonName} -> id {hudId}.");
        }

        private void HideHudLayoutAddons()
        {
            HideHudLayoutScreen();
            HideHudLayoutWindow();
        }

        private void RestoreHudLayoutAddons()
        {
            RestoreHudLayoutScreen();
            RestoreHudLayoutWindow();
        }

        private void HideHudLayoutScreen()
        {
            var screen = GetHudLayoutScreen();

            if (screen == null)
                return;

            var unit = (AtkUnitBase*)screen;

            if (!hudEditorScanHidScreen)
            {
                hudEditorScanScreenX = unit->X;
                hudEditorScanScreenY = unit->Y;
                hudEditorScanHidScreen = true;
            }

            unit->SetPosition(-32000, -32000);
        }

        private void RestoreHudLayoutScreen()
        {
            if (!hudEditorScanHidScreen)
                return;

            var screen = GetHudLayoutScreen();

            if (screen != null)
            {
                ((AtkUnitBase*)screen)->SetPosition(hudEditorScanScreenX, hudEditorScanScreenY);
            }

            hudEditorScanHidScreen = false;
        }

        private void HideHudLayoutWindow()
        {
            var window = GetHudLayoutWindow();

            if (window == null)
                return;

            if (!hudEditorScanHidWindow)
            {
                hudEditorScanWindowX = window->X;
                hudEditorScanWindowY = window->Y;
                hudEditorScanHidWindow = true;
            }

            window->SetPosition(-32000, -32000);
        }

        private void RestoreHudLayoutWindow()
        {
            if (!hudEditorScanHidWindow)
                return;

            var window = GetHudLayoutWindow();

            if (window != null)
            {
                window->SetPosition(hudEditorScanWindowX, hudEditorScanWindowY);
            }

            hudEditorScanHidWindow = false;
        }

        public bool TryGetHudLayoutWindowBounds(out Vector2 pos, out Vector2 size)
        {
            pos = default;
            size = default;

            var window = GetHudLayoutWindow();

            if (window == null || window->RootNode == null)
                return false;

            var node = window->RootNode;
            var scale = node->ScaleX;

            pos = hudEditorScanHidWindow
                      ? new Vector2(hudEditorScanWindowX, hudEditorScanWindowY)
                      : new Vector2(window->X, window->Y);
            size = new Vector2(node->Width * scale, node->Height * scale);

            return true;
        }

        public bool SetHudLayoutWindowPosition(Vector2 pos)
        {
            var x = (short)Math.Clamp((int)MathF.Round(pos.X), short.MinValue, short.MaxValue);
            var y = (short)Math.Clamp((int)MathF.Round(pos.Y), short.MinValue, short.MaxValue);

            if (hudEditorScanHidWindow)
            {
                hudEditorScanWindowX = x;
                hudEditorScanWindowY = y;
            }

            var window = GetHudLayoutWindow();

            if (window == null)
                return hudEditorScanHidWindow;

            window->SetPosition(x, y);
            return true;
        }

        public (int width, int height) GetScreenDimensions()
        {
            var screen = GetHudLayoutScreen();
            if (screen == null || screen->RootNode == null)
                return (0, 0);

            var scale = screen->RootNode->ScaleX;
            return (
                       (int)(screen->RootNode->Width * scale),
                       (int)(screen->RootNode->Height * scale)
                   );
        }

        public bool TryGetSelectedScreenPosition(out string name, out float screenX, out float screenY)
            => TryGetSelectedScreenPosition("TopLeft", out name, out screenX, out screenY);

        public bool TryGetSelectedScreenPosition(string anchor, out string name, out float screenX, out float screenY)
        {
            name = string.Empty;
            screenX = 0;
            screenY = 0;

            if (!TryGetSelectedOverlay(out var unit, out var node))
                return false;

            name = unit->NameString.ToString();
            screenX = node->X;
            screenY = node->Y;

            ApplyAnchorOffset(anchor, node, ref screenX, ref screenY);

            return true;
        }

        public bool SetSelectedScreenPosition(float x, float y)
            => SetSelectedScreenPosition(x, y, "TopLeft");

        public bool SetSelectedScreenPosition(float x, float y, string anchor)
        {
            if (!TryGetSelectedOverlay(out _, out var node))
                return false;

            var targetX = x;
            var targetY = y;

            ApplyReverseAnchorOffset(anchor, node, ref targetX, ref targetY);

            var dx = (int)MathF.Round(targetX - node->X);
            var dy = (int)MathF.Round(targetY - node->Y);

            MoveSelected(dx, dy);
            return true;
        }

        public bool TryGetSelectedBounds(out HudSnapRect rect)
        {
            rect = default;

            if (!TryGetSelectedOverlay(out var unit, out var node))
                return false;

            rect = new HudSnapRect(
                unit->NameString.ToString(),
                node->X,
                node->Y,
                node->Width,
                node->Height);

            return true;
        }

        public bool TryGetSelectedNodeId(out uint nodeId)
        {
            nodeId = 0;

            if (!TryGetSelectedOverlay(out _, out var node))
                return false;

            nodeId = node->NodeId;
            return true;
        }

        public IReadOnlyList<HudSnapRect> GetVisibleHudElementBounds()
        {
            var result = new List<HudSnapRect>();
            var screen = GetHudLayoutScreen();

            if (screen == null)
                return result;

            var unit = (AtkUnitBase*)screen;

            if (unit->RootNode == null)
                return result;

            var selectedNode = screen->SelectedOverlayNode == null
                                   ? null
                                   : &screen->SelectedOverlayNode->AtkResNode;

            CollectBounds(unit->RootNode, result, selectedNode);

            return result;
        }

        public IReadOnlyList<(Vector2 position, uint nodeId, ushort hudId, string name, float width, float height)> GetVisibleHudElements()
        {
            var result = new List<(Vector2, uint, ushort, string, float, float)>();
            var screen = GetHudLayoutScreen();

            if (screen == null)
                return result;

            var unit = (AtkUnitBase*)screen;

            if (unit->RootNode == null)
                return result;

            var addonNames = GetLoadedHudAddonNamesById();
            CollectVisibleHudElements(unit->RootNode, result, addonNames);

            return result;
        }

        public static AddonHudLayoutScreen* GetHudLayoutScreen()
        {
            var ptr = Plugin.GameGui.GetAddonByName("_HudLayoutScreen");
            return ptr == nint.Zero ? null : (AddonHudLayoutScreen*)ptr.Address;
        }

        private static AtkUnitBase* GetHudLayoutWindow()
        {
            var ptr = Plugin.GameGui.GetAddonByName("_HudLayoutWindow");
            return ptr == nint.Zero ? null : (AtkUnitBase*)ptr.Address;
        }

        private static AtkUnitBase* GetHudLayoutCallbackWindow()
        {
            var ptr = Plugin.GameGui.GetAddonByName("HudLayout");
            if (ptr != nint.Zero)
                return (AtkUnitBase*)ptr.Address;

            return GetHudLayoutWindow();
        }

        private static bool TryGetSelected(
            AddonHudLayoutScreen* screen,
            out void* info,
            out AtkUnitBase* unit)
        {
            info = null;
            unit = null;

            if (screen->SelectedAddon == null)
                return false;

            if (screen->SelectedAddon->SelectedAtkUnit == null)
                return false;

            info = screen->SelectedAddon;
            unit = screen->SelectedAddon->SelectedAtkUnit;

            return true;
        }

        private static bool TryGetSelectedOverlay(out AtkUnitBase* unit, out AtkResNode* node)
        {
            unit = null;
            node = null;

            var screen = GetHudLayoutScreen();

            if (screen == null)
                return false;

            if (!TryGetSelected(screen, out _, out unit))
                return false;

            if (screen->SelectedOverlayNode == null)
                return false;

            node = &screen->SelectedOverlayNode->AtkResNode;
            return true;
        }

        private static void MoveOverlayByDelta(AddonHudLayoutScreen* screen, int dx, int dy)
        {
            if (screen->SelectedOverlayNode == null)
                return;

            var node = &screen->SelectedOverlayNode->AtkResNode;
            node->SetPositionFloat(node->X + dx, node->Y + dy);
        }

        private static void MarkChanged(void* info)
        {
            var raw = (byte*)info;
            *(byte*)(raw + 0x4F) = 1;

            var agent = AgentHUDLayout.Instance();

            if (agent == null)
                return;

            agent->NeedToSave = true;

            var agentRaw = (byte*)agent;
            *(byte*)(agentRaw + 0x70) = 1;
        }

        private static void ActivateSaveButton()
        {
            var window = GetHudLayoutWindow();

            if (window == null || window->RootNode == null)
                return;

            var node = FindNode(window->RootNode, SaveButtonNodeId);

            if (node == null || (int)node->Type != 1001)
                return;

            var component = ((AtkComponentNode*)node)->Component;

            if (component == null)
                return;

            component->SetEnabledState(true);
        }

        private static void ApplyAnchorOffset(string anchor, AtkResNode* node, ref float x, ref float y)
        {
            switch (anchor)
            {
                case "TopRight":
                    x += node->Width;
                    break;

                case "BottomLeft":
                    y += node->Height;
                    break;

                case "BottomRight":
                    x += node->Width;
                    y += node->Height;
                    break;

                case "Center":
                    x += node->Width / 2f;
                    y += node->Height / 2f;
                    break;
            }
        }

        private static void ApplyReverseAnchorOffset(string anchor, AtkResNode* node, ref float x, ref float y)
        {
            switch (anchor)
            {
                case "TopRight":
                    x -= node->Width;
                    break;

                case "BottomLeft":
                    y -= node->Height;
                    break;

                case "BottomRight":
                    x -= node->Width;
                    y -= node->Height;
                    break;

                case "Center":
                    x -= node->Width / 2f;
                    y -= node->Height / 2f;
                    break;
            }
        }

        private static AtkResNode* FindNode(AtkResNode* node, uint id)
        {
            if (node == null)
                return null;

            if (node->NodeId == id)
                return node;

            var child = node->ChildNode;

            while (child != null)
            {
                var found = FindNode(child, id);

                if (found != null)
                    return found;

                child = child->PrevSiblingNode;
            }

            return null;
        }

        private static void CollectBounds(AtkResNode* node, List<HudSnapRect> result, AtkResNode* selectedNode)
        {
            if (node == null)
                return;

            if (IsSnapCandidate(node, selectedNode))
            {
                result.Add(new HudSnapRect(
                               $"node_{node->NodeId}",
                               node->X,
                               node->Y,
                               node->Width,
                               node->Height));
            }

            var child = node->ChildNode;

            while (child != null)
            {
                CollectBounds(child, result, selectedNode);
                child = child->PrevSiblingNode;
            }
        }

        private static void CollectVisibleHudElements(
            AtkResNode* node,
            List<(Vector2 position, uint nodeId, ushort hudId, string name, float width, float height)> result,
            IReadOnlyDictionary<ushort, string> addonNames)
        {
            if (node == null)
                return;

            if (IsVisibleHudElement(node))
            {
                var hudId = (ushort)(node->NodeId - HudNodeIdMin);
                var name = addonNames.TryGetValue(hudId, out var addonName)
                               ? addonName
                               : $"node_{node->NodeId}";

                result.Add((
                               new Vector2(node->X, node->Y),
                               node->NodeId,
                               hudId,
                               name,
                               node->Width,
                               node->Height));
            }

            var child = node->ChildNode;

            while (child != null)
            {
                CollectVisibleHudElements(child, result, addonNames);
                child = child->PrevSiblingNode;
            }
        }

        private static Dictionary<ushort, string> GetLoadedHudAddonNamesById()
        {
            var result = new Dictionary<ushort, string>();
            var manager = RaptureAtkUnitManager.Instance();

            if (manager == null)
                return result;

            foreach (var entry in manager->AllLoadedUnitsList.Entries)
            {
                var unit = (AtkUnitBase*)entry;

                if (unit == null)
                    continue;

                if (!unit->IsReady || unit->RootNode == null)
                    continue;

                var name = unit->NameString.ToString();

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                result.TryAdd(unit->Id, name);
            }

            return result;
        }

        private static bool IsSnapCandidate(AtkResNode* node, AtkResNode* selectedNode)
        {
            return node != selectedNode
                   && IsVisibleHudElement(node);
        }

        private static bool IsVisibleHudElement(AtkResNode* node)
        {
            return node->IsVisible()
                   && node->Width > MinSnapNodeSize
                   && node->Height > MinSnapNodeSize
                   && node->NodeId >= HudNodeIdMin
                   && node->NodeId <= HudNodeIdMax;
        }

        public void SelectHudElementByNodeId(uint nodeId)
        {
            if (nodeId < HudNodeIdMin || nodeId > HudNodeIdMax)
            {
                LogDebug($"Node ID {nodeId} is outside the HUD element node range.");
                return;
            }

            var screen = GetHudLayoutScreen();

            if (screen == null || screen->RootNode == null)
            {
                LogDebug("HUD layout screen is not available.");
                return;
            }

            var node = FindVisibleHudElementNode(screen->RootNode, nodeId);

            if (node == null)
            {
                LogDebug($"Visible HUD overlay Node ID {nodeId} was not found.");
                return;
            }

            var centerX = (int)MathF.Round(node->X + node->Width / 2f);
            var centerY = (int)MathF.Round(node->Y + node->Height / 2f);

            Plugin.ClickScreenPosition(centerX, centerY);

            var selectedText = TryGetSelectedNodeId(out var selectedNodeId)
                                   ? selectedNodeId.ToString()
                                   : "none";

            LogDebug($"Clicked Node ID {nodeId} at ({centerX}, {centerY}). Current selected node: {selectedText}.");
        }

        public void SelectVisibleHudElementByName(string name)
        {
            var elements = GetVisibleHudElements();
            var element = elements.FirstOrDefault(e => e.name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (element.nodeId == 0)
            {
                LogDebug($"Visible HUD addon '{name}' was not found.");
                return;
            }

            ClickVisibleHudElement(element, $"HUD addon {element.name}");
        }

        private void ClickVisibleHudElement(
            (Vector2 position, uint nodeId, ushort hudId, string name, float width, float height) element,
            string label)
        {
            var centerX = (int)MathF.Round(element.position.X + element.width / 2f);
            var centerY = (int)MathF.Round(element.position.Y + element.height / 2f);

            ClickHudLayoutPosition(centerX, centerY);
            LogDebug($"Clicked {label} at ({centerX}, {centerY}).");
        }

        public void ClickHudLayoutPosition(int x, int y)
        {
            Plugin.ClickScreenPosition(x, y);

            var selectedText = TryGetSelectedNodeId(out var selectedNodeId)
                                   ? selectedNodeId.ToString()
                                   : "none";

            LogDebug($"Clicked HUD layout position ({x}, {y}). Current selected node: {selectedText}.");
        }

        private static AtkResNode* FindVisibleHudElementNode(AtkResNode* node, uint id)
        {
            if (node == null)
                return null;

            if (node->NodeId == id && IsVisibleHudElement(node))
                return node;

            var child = node->ChildNode;

            while (child != null)
            {
                var found = FindVisibleHudElementNode(child, id);

                if (found != null)
                    return found;

                child = child->PrevSiblingNode;
            }

            return null;
        }

        public void UndoLastMove()
        {
            if (pendingUndoRestore.HasValue)
            {
                LogDebug($"Undo button: continue pending restore {FormatUndoEntry(pendingUndoRestore.Value)}.");
                RestorePosition(pendingUndoRestore.Value, false, pendingUndoRestoreIsRedo);
                return;
            }

            if (undoHistory.Count == 0)
            {
                LogDebug("Nothing to undo.");
                return;
            }

            var entry = undoHistory[^1];
            undoHistory.RemoveAt(undoHistory.Count - 1);
            lastUndoReason = string.Empty;
            LogDebug($"Undo button: pop {FormatUndoEntry(entry)}. Undo left after pop: {UndoCount}.");
            RestorePosition(entry, false, false);
        }

        public void RedoLastMove()
        {
            if (redoHistory.Count == 0)
            {
                LogDebug("Nothing to redo.");
                return;
            }

            var entry = redoHistory[^1];
            redoHistory.RemoveAt(redoHistory.Count - 1);
            LogDebug($"Redo button: pop {FormatUndoEntry(entry)}. Redo left after pop: {RedoCount}.");
            RestorePosition(entry, false, true);
        }

        public void ContinuePendingUndoRestore()
        {
            if (!pendingUndoRestore.HasValue)
                return;

            var entry = pendingUndoRestore.Value;
            if (TryGetSelectedOverlay(out var selectedUnit, out var selectedNode)
                && IsSameHudElement(entry, selectedUnit, selectedNode))
            {
                RestorePosition(entry, false, pendingUndoRestoreIsRedo);
                pendingUndoRestoreWaitFrames = 0;
                return;
            }

            pendingUndoRestoreWaitFrames++;
            if (pendingUndoRestoreWaitFrames > UndoRestoreMaxWaitFrames)
            {
                LogDebug($"Undo restore timed out waiting for {entry.Name}.");
                pendingUndoRestore = null;
                pendingUndoRestoreIsRedo = false;
                pendingUndoRestoreWaitFrames = 0;
            }
        }

        public void UpdateExternalMovementTracking()
        {
            var now = Dalamud.Bindings.ImGui.ImGui.GetTime();
            var isLeftMouseDown = Dalamud.Bindings.ImGui.ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
            var leftMouseReleased = wasLeftMouseDown && !isLeftMouseDown;
            wasLeftMouseDown = isLeftMouseDown;

            if (!TryCreateUndoEntry(out var current))
            {
                FlushPendingVanillaMove();
                lastObservedEntry = null;
                suppressNextObservedMovement = false;
                return;
            }

            if (!lastObservedEntry.HasValue || !IsSameUndoTarget(lastObservedEntry.Value, current))
            {
                FlushPendingVanillaMove();
                lastObservedEntry = current;
                suppressNextObservedMovement = false;
                return;
            }

            var previous = lastObservedEntry.Value;
            if (Vector2.DistanceSquared(previous.Position, current.Position) < 0.01f)
            {
                if (pendingVanillaMoveStart.HasValue
                    && (leftMouseReleased || now - lastVanillaMoveTime >= VanillaMoveSettleDelay))
                {
                    FlushPendingVanillaMove();
                }

                return;
            }

            if (suppressNextObservedMovement || isRestoringUndo)
            {
                suppressNextObservedMovement = false;
                lastObservedEntry = current;
                return;
            }

            pendingVanillaMoveStart ??= previous;
            pendingVanillaMoveLatest = current;
            lastVanillaMoveTime = now;
            lastObservedEntry = current;

            if (leftMouseReleased)
            {
                FlushPendingVanillaMove();
            }
        }

        private void RecordUndoSnapshot()
        {
            FlushPendingVanillaMove();

            if (isRestoringUndo || !TryCreateUndoEntry(out var entry))
                return;

            RecordUndoEntry(entry, "HudNudge move", true);
        }

        private void FlushPendingVanillaMove()
        {
            if (!pendingVanillaMoveStart.HasValue)
                return;

            var start = pendingVanillaMoveStart.Value;
            var latest = pendingVanillaMoveLatest ?? start;
            pendingVanillaMoveStart = null;
            pendingVanillaMoveLatest = null;

            if (Vector2.DistanceSquared(start.Position, latest.Position) < 0.01f)
                return;

            RecordUndoEntry(start, $"vanilla move to ({latest.Position.X}, {latest.Position.Y})", false);
        }

        private void RecordUndoEntry(HudUndoEntry entry, string reason, bool skipSameTarget)
        {
            if (undoHistory.Count > 0)
            {
                var latest = undoHistory[^1];
                if (skipSameTarget
                    && lastUndoReason == reason
                    && IsSameUndoTarget(latest, entry))
                {
                    LogDebug($"Undo write skipped same target ({reason}): {FormatUndoEntry(entry)}. Undo count: {UndoCount}.");
                    return;
                }
            }

            PushUndoEntry(entry, reason, true);
        }

        private void PushUndoEntry(HudUndoEntry entry, string reason, bool clearRedo)
        {
            undoHistory.Add(entry);
            lastUndoReason = reason;
            if (clearRedo)
            {
                redoHistory.Clear();
            }

            if (undoHistory.Count > MaxUndoHistory)
            {
                undoHistory.RemoveAt(0);
            }

            LogDebug($"Undo write ({reason}): {FormatUndoEntry(entry)}. Undo count: {UndoCount}.");
        }

        private void PushRedoEntry(HudUndoEntry entry)
        {
            redoHistory.Add(entry);
            if (redoHistory.Count > MaxUndoHistory)
            {
                redoHistory.RemoveAt(0);
            }

            LogDebug($"Redo write: {FormatUndoEntry(entry)}. Redo count: {RedoCount}.");
        }

        private static bool IsSameUndoTarget(HudUndoEntry left, HudUndoEntry right)
        {
            if (left.HudId == right.HudId && left.HudId != 0)
                return true;

            return left.Name.Equals(right.Name, StringComparison.Ordinal);
        }

        private bool TryCreateUndoEntry(out HudUndoEntry entry)
        {
            entry = default;

            if (!TryGetSelectedOverlay(out var unit, out var node))
                return false;

            entry = new HudUndoEntry(
                new Vector2(node->X, node->Y),
                node->NodeId,
                unit->Id,
                unit->NameString.ToString());
            return true;
        }

        private static bool TryGetSelectedAddonName(out string addonName)
        {
            addonName = string.Empty;

            var screen = GetHudLayoutScreen();
            if (screen == null || screen->SelectedAddon == null || screen->SelectedAddon->SelectedAtkUnit == null)
                return false;

            addonName = screen->SelectedAddon->SelectedAtkUnit->NameString.ToString();
            return !string.IsNullOrWhiteSpace(addonName);
        }

        private void RestorePosition(HudUndoEntry entry, bool keepHistory, bool isRedo)
        {
            if (TryGetSelectedOverlay(out var selectedUnit, out var selectedNode)
                && IsSameHudElement(entry, selectedUnit, selectedNode))
            {
                LogDebug($"{(isRedo ? "Redo" : "Undo")} restore: target already selected, moving {FormatUndoEntry(entry)}.");
                if (!keepHistory && TryCreateUndoEntry(out var inverseEntry))
                {
                    if (isRedo)
                    {
                        PushUndoEntry(inverseEntry, "redo inverse", false);
                    }
                    else
                    {
                        PushRedoEntry(inverseEntry);
                    }
                }

                isRestoringUndo = !keepHistory;
                SetSelectedScreenPosition(entry.Position.X, entry.Position.Y);
                isRestoringUndo = false;
                pendingUndoRestore = null;
                pendingUndoRestoreIsRedo = false;
                HistoryRestoreVersion++;
                LogDebug($"Restored HUD position: Node ID {entry.NodeId} ({entry.Name}) to ({entry.Position.X}, {entry.Position.Y}). Undo left: {UndoCount}. Redo left: {RedoCount}.");
                return;
            }

            pendingUndoRestore = entry;
            pendingUndoRestoreIsRedo = isRedo;
            pendingUndoRestoreWaitFrames = 0;

            LogDebug($"{(isRedo ? "Redo" : "Undo")} restore: need select {FormatUndoEntry(entry)}.");

            if (SelectHudEditorElementByName(entry.Name))
            {
                LogDebug($"Selected HUD editor addon {entry.Name} for restore.");
            }
            else if (SelectHudEditorElementById(entry.HudId))
            {
                LogDebug($"Selected HUD editor ID {entry.HudId} ({entry.Name}) for restore.");
            }
            else if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                SelectVisibleHudElementByName(entry.Name);
            }
            else if (entry.NodeId >= HudNodeIdMin && entry.NodeId <= HudNodeIdMax)
            {
                SelectHudElementByNodeId(entry.NodeId);
            }

            LogDebug($"Selected HUD element for restore: Node ID {entry.NodeId} ({entry.Name}). Press undo again if it did not move yet.");
        }

        private static bool IsSameHudElement(HudUndoEntry entry, AtkUnitBase* unit, AtkResNode* node)
        {
            if (entry.HudId != 0 && unit->Id == entry.HudId)
                return true;

            return unit->NameString.ToString().Equals(entry.Name, StringComparison.Ordinal);
        }

        private static string FormatUndoEntry(HudUndoEntry entry)
            => $"HudId={entry.HudId}, NodeId={entry.NodeId}, Name={entry.Name}, Pos=({entry.Position.X}, {entry.Position.Y})";

        private static bool TryGetAtkInt(nint valuesPtr, int index, out int result)
        {
            result = 0;
            var value = new Dalamud.Game.NativeWrapper.AtkValuePtr(valuesPtr + (index * sizeof(AtkValue)));
            try
            {
                if (value.GetValue() is int intValue)
                {
                    result = intValue;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

    }
}

