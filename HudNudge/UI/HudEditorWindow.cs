using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using HudNudge.Windows.Components;

namespace HudNudge.Windows
{
    public unsafe sealed class HudEditorWindow : Window, IDisposable
    {
        private const float DefaultHeight = 250f;
        private const double SnapPreviewDuration = 0.15;
        private const double SnapGuideDuration = 0.45;
        private const double SnapApplyDelay = 0.25;
        private const double SnapMoveWindow = 0.75;
        private const double MoveButtonFirstRepeatDelay = 0.50;
        private const double MoveButtonRepeatDelay = 0.10;
        private const float OffsetX = 0f;
        private const float OffsetY = 4f;
        private const float LearningHeight = 150f;
        private const float LearningWindowWidth = 360f;
        private const float MinEditorWindowWidth = 220f;
        private const float MoveColumnWidth = 128f;
        private const float PositionColumnWidth = 210f;
        private const float SnapColumnWidth = 230f;

        private readonly HudLayoutController hudLayout;
        private readonly HudSnapController hudSnap;

        private readonly string[] anchorKeys = { "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Center" };
        private readonly string[] anchorNames = { "Top left", "Top right", "Bottom left", "Bottom right", "Center" };

        private bool wasHudLayoutOpen;
        private float contentHeight = DefaultHeight;
        private float targetWindowHeight = DefaultHeight;
        private float contentWidth = MinEditorWindowWidth;
        private bool hasWindowPlacement;
        private Vector2 lastHudLayoutWindowPos;
        private Vector2 lastEditorFollowPos;

        private int anchorIndex;
        private int moveStep = 10;
        private int screenXInput;
        private int screenYInput;

        private bool inputsInitialized;
        private bool isEditingPositionInput;
        private bool positionInputsDirty;
        private string lastSelectedName = string.Empty;
        private int lastAnchorIndex = -1;

        private bool snapOnDrag = false;
        private int snapThreshold = 30;
        private int snapModeIndex;
        private readonly string[] snapModeNames = { "Both", "HUD elements", "Screen center" };

        private float lastScreenX;
        private float lastScreenY;
        private double lastMoveTime;
        private double suppressSnapUntil;
        private double showSnapGuideUntil;
        private int observedHistoryRestoreVersion;
        private string activeMoveButton = string.Empty;
        private double moveButtonHoldStartTime;
        private double lastMoveButtonRepeatTime;
        private bool moveButtonMovedThisHold;

        private HudSnapResult activeSnap;
        private HudSnapResult lastVisibleSnap;
        public int MoveStep => moveStep;

        public HudEditorWindow(HudLayoutController hudLayout, HudSnapController hudSnap)
            : base("HudNudge HUD Editor")
        {
            this.hudLayout = hudLayout;
            this.hudSnap = hudSnap;

            Size = new Vector2(0, DefaultHeight);
            SizeCondition = ImGuiCond.Always;
            HideExtraTitleBarButtons();

            Flags |= ImGuiWindowFlags.NoSavedSettings;
        }

        internal void HideExtraTitleBarButtons()
        {
            ShowCloseButton = false;
            AllowPinning = false;
            AllowClickthrough = false;
            AllowBackgroundBlur = false;
            IsPinned = false;
            IsClickthrough = false;
        }

        public override void Draw()
        {
            UpdateWindowPlacement();

            var hasSelection = hudLayout.TryGetSelectedScreenPosition(
                anchorKeys[anchorIndex],
                out var selectedName,
                out var posX,
                out var posY);
            var isLearning = hudLayout.IsHudEditorScanActive;

            if (isLearning)
            {
                CenterLearningWindow();
                ImGui.SetWindowSize(new Vector2(LearningWindowWidth, LearningHeight), ImGuiCond.Always);
                DrawLearningScreen();
                UpdateContentHeight(LearningHeight);

                var learningWindowWidth = MathF.Max(LearningWindowWidth, ImGui.GetCursorPosX() + ImGui.GetStyle().WindowPadding.X * 2);
                ImGui.SetWindowSize(new Vector2(learningWindowWidth, contentHeight), ImGuiCond.Always);
                return;
            }

            UpdateSelectionState(hasSelection, selectedName, posX, posY);

            if (hasSelection && !isLearning) UpdateMovementTimer(posX, posY);
            SuppressSnapAfterHistoryRestore();

            var snapPreview = HandleSnapPreviewAndDelayedSnap(hasSelection && !isLearning);

            if (hasSelection && !isLearning)
            {
                DrawCross(posX, posY, new Vector4(1f, 0.5f, 0.1f, 1f));
                DrawSnapPreview(snapPreview);
            }

            contentWidth = MinEditorWindowWidth;
            DrawTopBar();
            DrawPrimaryControls(hasSelection);
            UpdateContentWidth(GetPrimaryControlsWidth());
            UpdateContentWidth();
            UpdateContentHeight();

            ImGui.SetWindowSize(new Vector2(contentWidth, targetWindowHeight), ImGuiCond.Always);
        }

        public void Dispose() { }

        private static void CenterLearningWindow()
        {
            var viewport = ImGui.GetMainViewport();
            var windowSize = new Vector2(LearningWindowWidth, LearningHeight);
            var center = viewport.Pos + (viewport.Size - windowSize) / 2f;

            ImGui.SetWindowPos(center, ImGuiCond.Always);
        }

        private void DrawLearningScreen()
        {
            var current = Math.Clamp(hudLayout.HudEditorScanCurrentId, 0, hudLayout.MaxHudEditorScanId);
            var max = Math.Max(1, hudLayout.MaxHudEditorScanId);
            var progress = Math.Clamp(current / (float)max, 0f, 1f);
            var width = MathF.Max(220f, LearningWindowWidth - ImGui.GetStyle().WindowPadding.X * 2);

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextUnformatted("Learning HUD elements");
            ImGui.TextDisabled($"Scanning {current}/{max}");
            ImGui.ProgressBar(progress, new Vector2(width, 18), string.Empty);
            ImGui.Dummy(new Vector2(0, 8));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped("HudNudge is mapping the HUD editor list.\nThe selected element and native HUD editor are hidden until this finishes.");
            ImGui.PopTextWrapPos();

        }

        private void DrawPrimaryControls(bool hasSelection)
        {
            if (!ImGui.BeginTable("primary_controls", 3, ImGuiTableFlags.SizingFixedFit))
                return;

            ImGui.TableSetupColumn("Move", ImGuiTableColumnFlags.WidthFixed, MoveColumnWidth);
            ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, PositionColumnWidth);
            ImGui.TableSetupColumn("Snap", ImGuiTableColumnFlags.WidthFixed, SnapColumnWidth);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawMoveControls(hasSelection);

            ImGui.TableSetColumnIndex(1);
            DrawPositionControls(hasSelection);

            ImGui.TableSetColumnIndex(2);
            DrawSnapControls(hasSelection);

            ImGui.EndTable();
        }

        private void DrawTopBar()
        {
            if (ImGui.Button($"Undo ({hudLayout.UndoCount})", new Vector2(96, 28)))
            {
                hudLayout.UndoLastMove();
            }

            ImGui.SameLine();

            if (ImGui.Button($"Redo ({hudLayout.RedoCount})", new Vector2(96, 28)))
            {
                hudLayout.RedoLastMove();
            }

            ImGui.Separator();
        }

        private void UpdateWindowPlacement()
        {
            var isHudLayoutOpen = hudLayout.IsHudLayoutOpen;

            if (isHudLayoutOpen && !wasHudLayoutOpen)
                hasWindowPlacement = false;

            wasHudLayoutOpen = isHudLayoutOpen;

            if (!hudLayout.TryGetHudLayoutWindowBounds(out var hudPos, out var hudSize))
            {
                wasHudLayoutOpen = false;
                hasWindowPlacement = false;
                return;
            }

            targetWindowHeight = MathF.Max(180f, hudSize.Y);
            var followPos = hudPos + new Vector2(hudSize.X + OffsetX, OffsetY);
            var currentPos = ImGui.GetWindowPos();
            var hudLayoutMoved = !hasWindowPlacement || HasMoved(hudPos, lastHudLayoutWindowPos);
            var editorMoved = hasWindowPlacement
                              && HasMoved(currentPos, lastEditorFollowPos)
                              && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                              && ImGui.IsMouseDragging(ImGuiMouseButton.Left);

            if (editorMoved && !hudLayoutMoved)
            {
                var targetHudPos = currentPos - new Vector2(hudSize.X + OffsetX, OffsetY);
                if (hudLayout.SetHudLayoutWindowPosition(targetHudPos))
                {
                    hudPos = targetHudPos;
                    followPos = currentPos;
                }
            }
            else
            {
                ImGui.SetWindowPos(followPos, ImGuiCond.Always);
            }

            hasWindowPlacement = true;
            lastHudLayoutWindowPos = hudPos;
            lastEditorFollowPos = followPos;
        }

        private static bool HasMoved(Vector2 current, Vector2 previous)
            => Vector2.DistanceSquared(current, previous) > 1f;

        private void UpdateSelectionState(bool hasSelection, string selectedName, float screenX, float screenY)
        {
            if (!hasSelection)
            {
                inputsInitialized = false;
                positionInputsDirty = false;
                lastSelectedName = string.Empty;
                lastAnchorIndex = -1;
                activeSnap = default;
                lastVisibleSnap = default;
                return;
            }

            if (inputsInitialized && lastSelectedName == selectedName && lastAnchorIndex == anchorIndex)
            {
                SyncPositionInputs(screenX, screenY);
                return;
            }

            SetPositionInputs(screenX, screenY);
            positionInputsDirty = false;
            lastSelectedName = selectedName;
            lastAnchorIndex = anchorIndex;
            inputsInitialized = true;

            lastScreenX = screenX;
            lastScreenY = screenY;
            lastMoveTime = ImGui.GetTime();
        }

        private void SyncPositionInputs(float screenX, float screenY)
        {
            if (isEditingPositionInput || positionInputsDirty)
                return;

            SetPositionInputs(screenX, screenY);
        }

        private void SetPositionInputs(float screenX, float screenY)
        {
            screenXInput = (int)MathF.Round(screenX);
            screenYInput = (int)MathF.Round(screenY);
        }

        private void UpdateMovementTimer(float screenX, float screenY)
        {
            var moved =
                MathF.Abs(screenX - lastScreenX) > 0.5f ||
                MathF.Abs(screenY - lastScreenY) > 0.5f;

            if (!moved)
                return;

            lastScreenX = screenX;
            lastScreenY = screenY;
            positionInputsDirty = false;
            SetPositionInputs(screenX, screenY);
            lastMoveTime = ImGui.GetTime();
        }

        private void SuppressSnapAfterHistoryRestore()
        {
            if (observedHistoryRestoreVersion == hudLayout.HistoryRestoreVersion)
                return;

            observedHistoryRestoreVersion = hudLayout.HistoryRestoreVersion;
            activeSnap = default;
            lastVisibleSnap = default;
            showSnapGuideUntil = 0;
            suppressSnapUntil = ImGui.GetTime() + SnapMoveWindow + SnapApplyDelay;
            lastMoveTime = double.NegativeInfinity;
        }

        private HudSnapResult HandleSnapPreviewAndDelayedSnap(bool hasSelection)
        {
            if (!hasSelection || !IsSnapActive())
            {
                activeSnap = default;
                return default;
            }

            var now = ImGui.GetTime();

            if (now < suppressSnapUntil)
            {
                if (now < showSnapGuideUntil && lastVisibleSnap.Found)
                    return lastVisibleSnap;

                return default;
            }

            if (now - lastMoveTime >= SnapMoveWindow)
            {
                activeSnap = default;
                return default;
            }

            var snap = hudSnap.PreviewSelectedSnap(snapThreshold, GetSnapMode());

            if (!snap.Found)
            {
                activeSnap = default;
                return default;
            }

            activeSnap = snap;

            if (now - lastMoveTime < SnapApplyDelay)
                return activeSnap;

            ApplySnap(activeSnap, now);
            return lastVisibleSnap;
        }

        private bool IsSnapActive()
        {
            return snapOnDrag || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        }

        private void ApplySnap(HudSnapResult snap, double now)
        {
            var moveX = (int)MathF.Round(snap.DeltaX);
            var moveY = (int)MathF.Round(snap.DeltaY);

            if (moveX != 0 || moveY != 0)
                hudLayout.MoveSelected(moveX, moveY);

            lastVisibleSnap = snap;
            showSnapGuideUntil = now + SnapGuideDuration;
            suppressSnapUntil = now + SnapGuideDuration;
            activeSnap = default;
        }

        private void DrawSnapPreview(HudSnapResult snapPreview)
        {
            if (snapPreview.Found)
            {
                lastVisibleSnap = snapPreview;
                showSnapGuideUntil = ImGui.GetTime() + SnapPreviewDuration;
                DrawSnapGuide(snapPreview);
                return;
            }

            if (ImGui.GetTime() < showSnapGuideUntil && lastVisibleSnap.Found)
                DrawSnapGuide(lastVisibleSnap);
        }

        private void DrawMoveControls(bool hasSelection)
        {
            ImGui.TextUnformatted("Move");

            ImGui.SameLine();

            Tooltip.DrawCircularButtonWithTooltip(
                "You can also use your arrow keys on your keyboard to move HUD elements.");

            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##step", ref moveStep);
            ImGui.SameLine();
            ImGui.TextDisabled("Step");

            moveStep = Math.Max(1, moveStep);

            ImGui.BeginDisabled(!hasSelection);
            DrawDpad();
            ImGui.EndDisabled();
        }

        private void DrawPositionControls(bool hasSelection)
        {
            ImGui.TextUnformatted("Position");
            ImGui.SameLine();
            Tooltip.DrawCircularButtonWithTooltip(
                "The anchor decides which point of the selected HUD element the X and Y coordinates refer to.");

            ImGui.BeginDisabled(!hasSelection);

            ImGui.SetNextItemWidth(128);
            ImGui.Combo("##anchor", ref anchorIndex, anchorNames, anchorNames.Length);
            ImGui.SameLine();
            ImGui.TextDisabled("Anchor");

            ImGui.SetNextItemWidth(68);
            if (ImGui.InputInt("##x", ref screenXInput))
                positionInputsDirty = true;

            var editingX = ImGui.IsItemActive();
            ImGui.SameLine();
            ImGui.TextDisabled("X");

            ImGui.SetNextItemWidth(68);
            if (ImGui.InputInt("##y", ref screenYInput))
                positionInputsDirty = true;

            var editingY = ImGui.IsItemActive();
            ImGui.SameLine();
            ImGui.TextDisabled("Y");

            isEditingPositionInput = editingX || editingY;

            if (ImGui.Button("Apply Position", new Vector2(128, 28)))
            {
                hudLayout.SetSelectedScreenPosition(screenXInput, screenYInput, anchorKeys[anchorIndex]);
                positionInputsDirty = false;
            }

            ImGui.EndDisabled();
        }

        private void DrawSnapControls(bool hasSelection)
        {
            ImGui.TextUnformatted("Snap");
            ImGui.SameLine();
            Tooltip.DrawCircularButtonWithTooltip(
                "When snapping is disabled, you can still press Shift to temporarily enable it.");

            ImGui.BeginDisabled(!hasSelection);

            ImGui.Checkbox("Enable snap while dragging", ref snapOnDrag);

            if (ImGui.BeginTable("snap_options", 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("ModeLabel", ImGuiTableColumnFlags.WidthFixed, 62);
                ImGui.TableSetupColumn("ModeControl", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("Mode");
                ImGui.SameLine();
                Tooltip.DrawCircularButtonWithTooltip(
                    "HUD elements: snap to nearby HUD elements. Screen center: snap to the screen midpoint. Both: snap to HUD elements and the screen center.");
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(118);
                ImGui.Combo("##snapMode", ref snapModeIndex, snapModeNames, snapModeNames.Length);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("Distance");
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(68);
                ImGui.InputInt("##snapPx", ref snapThreshold);

                ImGui.EndTable();
            }

            snapThreshold = Math.Max(1, snapThreshold);

            ImGui.EndDisabled();
        }

        private HudSnapMode GetSnapMode()
        {
            return snapModeIndex switch
            {
                1 => HudSnapMode.Elements,
                2 => HudSnapMode.ScreenCenter,
                _ => HudSnapMode.Both
            };
        }

        private void DrawDpad()
        {
            if (!ImGui.BeginTable("dpad", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadInnerX))
                return;

            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));

            ImGui.TableSetupColumn("c1", ImGuiTableColumnFlags.WidthFixed, 27);
            ImGui.TableSetupColumn("c2", ImGuiTableColumnFlags.WidthFixed, 27);
            ImGui.TableSetupColumn("c3", ImGuiTableColumnFlags.WidthFixed, 36);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);

            DrawMoveButton("##up", ImGuiDir.Up, 0, -moveStep);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            DrawMoveButton("##left", ImGuiDir.Left, -moveStep, 0);

            ImGui.TableSetColumnIndex(2);

            DrawMoveButton("##right", ImGuiDir.Right, moveStep, 0);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);

            DrawMoveButton("##down", ImGuiDir.Down, 0, moveStep);

            ImGui.PopStyleVar(2);
            ImGui.EndTable();
        }

        private void DrawMoveButton(string label, ImGuiDir direction, int dx, int dy)
        {
            var pressed = ImGui.ArrowButton(label, direction);
            var isHeld = ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left);
            var now = ImGui.GetTime();

            if (isHeld)
            {
                if (activeMoveButton != label)
                {
                    activeMoveButton = label;
                    moveButtonHoldStartTime = now;
                    lastMoveButtonRepeatTime = now;
                    moveButtonMovedThisHold = false;
                }

                if (!moveButtonMovedThisHold)
                {
                    MoveSelectedFromButton(dx, dy);
                    moveButtonMovedThisHold = true;
                    return;
                }

                if (now - moveButtonHoldStartTime < MoveButtonFirstRepeatDelay
                    || now - lastMoveButtonRepeatTime < MoveButtonRepeatDelay)
                    return;

                MoveSelectedFromButton(dx, dy);
                lastMoveButtonRepeatTime = now;
                return;
            }

            if (pressed && activeMoveButton != label)
                MoveSelectedFromButton(dx, dy);

            if (activeMoveButton == label && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                activeMoveButton = string.Empty;
                moveButtonMovedThisHold = false;
            }
        }

        private void MoveSelectedFromButton(int dx, int dy)
        {
            positionInputsDirty = false;
            hudLayout.MoveSelected(dx, dy);
        }

        private void UpdateContentHeight(float minHeight = DefaultHeight)
        {
            contentHeight = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y + 6f;
            contentHeight = MathF.Max(minHeight, contentHeight);
        }

        private void UpdateContentWidth()
        {
            var padding = ImGui.GetStyle().WindowPadding.X;
            var itemRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + padding;
            var cursorRight = ImGui.GetCursorPosX() + padding * 2f;

            contentWidth = MathF.Max(contentWidth, MathF.Max(itemRight, cursorRight));
        }

        private void UpdateContentWidth(float width)
        {
            contentWidth = MathF.Max(contentWidth, width);
        }

        private static float GetPrimaryControlsWidth()
        {
            var style = ImGui.GetStyle();
            var columnsWidth = MoveColumnWidth + PositionColumnWidth + SnapColumnWidth;
            var tablePadding = style.CellPadding.X * 6f;
            var windowPadding = style.WindowPadding.X * 2f;
            var breathingRoom = style.ItemSpacing.X * 2f;

            return columnsWidth + tablePadding + windowPadding + breathingRoom;
        }

        private static void DrawCross(float screenX, float screenY, Vector4 glowColor)
        {
            var drawList = ImGui.GetForegroundDrawList();
            var pos = new Vector2(screenX, screenY);

            var core = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
            var glowOuter = ImGui.GetColorU32(new Vector4(glowColor.X, glowColor.Y, glowColor.Z, 0.08f));
            var glowMid = ImGui.GetColorU32(new Vector4(glowColor.X, glowColor.Y, glowColor.Z, 0.16f));
            var glowInner = ImGui.GetColorU32(new Vector4(glowColor.X, glowColor.Y, glowColor.Z, 0.28f));

            DrawCrossLines(drawList, pos, 15f, 8f, glowOuter);
            DrawCrossLines(drawList, pos, 13f, 5f, glowMid);
            DrawCrossLines(drawList, pos, 11f, 3f, glowInner);
            DrawCrossLines(drawList, pos, 8f, 1.4f, core);
        }

        private static void DrawCrossLines(ImDrawListPtr drawList, Vector2 pos, float size, float thickness, uint color)
        {
            drawList.AddLine(new Vector2(pos.X - size, pos.Y), new Vector2(pos.X + size, pos.Y), color, thickness);
            drawList.AddLine(new Vector2(pos.X, pos.Y - size), new Vector2(pos.X, pos.Y + size), color, thickness);
        }

        private static void DrawSnapGuide(HudSnapResult snap)
        {
            var drawList = ImGui.GetForegroundDrawList();
            var viewport = ImGui.GetMainViewport();

            var guideColor = ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.75f, 0.6f));
            var pointColor = ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.75f, 0.8f));

            var start = viewport.Pos;
            var end = viewport.Pos + viewport.Size;

            if (MathF.Abs(snap.DeltaX) > 0.01f)
            {
                drawList.AddLine(
                    new Vector2(snap.TargetX, start.Y),
                    new Vector2(snap.TargetX, end.Y),
                    guideColor,
                    1.5f);
            }

            if (MathF.Abs(snap.DeltaY) > 0.01f)
            {
                drawList.AddLine(
                    new Vector2(start.X, snap.TargetY),
                    new Vector2(end.X, snap.TargetY),
                    guideColor,
                    1.5f);
            }

            const float markerSize = 5f;
            var target = new Vector2(snap.TargetX, snap.TargetY);

            drawList.AddCircleFilled(target, 3f, pointColor);

            drawList.AddLine(
                new Vector2(target.X - markerSize, target.Y),
                new Vector2(target.X + markerSize, target.Y),
                pointColor,
                1.5f);

            drawList.AddLine(
                new Vector2(target.X, target.Y - markerSize),
                new Vector2(target.X, target.Y + markerSize),
                pointColor,
                1.5f);
        }
    }
}
