using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace HudNudge.Windows
{
    public sealed class HudDebugWindow : Window, IDisposable
    {
        private readonly HudLayoutController hudLayout;
        private readonly Func<bool> getLogHudLayoutEvents;
        private readonly Action<bool> setLogHudLayoutEvents;

        private int hudElementIndex;

        public HudDebugWindow(
            HudLayoutController hudLayout,
            Func<bool> getLogHudLayoutEvents,
            Action<bool> setLogHudLayoutEvents)
            : base("HudNudge Debug")
        {
            this.hudLayout = hudLayout;
            this.getLogHudLayoutEvents = getLogHudLayoutEvents;
            this.setLogHudLayoutEvents = setLogHudLayoutEvents;

            Size = new Vector2(360, 210);
            SizeCondition = ImGuiCond.FirstUseEver;

            Flags |= ImGuiWindowFlags.NoSavedSettings;
        }

        public override void Draw()
        {
            DrawEventLoggingSection();
            DrawHudElementSelectorSection();
        }

        public void Dispose() { }

        private void DrawEventLoggingSection()
        {
            var logDebugActions = hudLayout.LogHudNudgeDebugActions;
            if (ImGui.Checkbox("Log HudNudge debug actions", ref logDebugActions))
            {
                hudLayout.LogHudNudgeDebugActions = logDebugActions;
            }

            var enabled = getLogHudLayoutEvents();

            if (ImGui.Checkbox("Log HUD editor input events", ref enabled))
            {
                setLogHudLayoutEvents(enabled);
            }
        }

        private void DrawHudElementSelectorSection()
        {
            DrawSectionHeader("HUD Select");

            if (hudLayout.IsHudEditorScanActive)
            {
                ImGui.TextDisabled($"Learning HUD elements: {hudLayout.HudEditorScanCurrentId}/{hudLayout.MaxHudEditorScanId}");
            }
            else
            {
                ImGui.TextDisabled($"HUD elements learned: {hudLayout.HudEditorElements.Count}");
            }

            var elements = hudLayout.HudEditorElements;
            if (hudElementIndex >= elements.Count)
            {
                hudElementIndex = Math.Max(0, elements.Count - 1);
            }

            var hasElements = elements.Count > 0;
            var preview = hasElements
                              ? FormatHudEditorElement(elements[hudElementIndex])
                              : "No HUD elements learned";

            ImGui.BeginDisabled(!hasElements);

            ImGui.SetNextItemWidth(245);
            if (ImGui.BeginCombo("##hudElementSelect", preview))
            {
                for (var i = 0; i < elements.Count; i++)
                {
                    var element = elements[i];
                    var isSelected = i == hudElementIndex;

                    if (ImGui.Selectable(FormatHudEditorElement(element), isSelected))
                    {
                        hudElementIndex = i;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();

            if (ImGui.Button("Select") && hasElements)
            {
                hudLayout.SelectHudEditorElementById(elements[hudElementIndex].Id);
            }

            ImGui.EndDisabled();
        }

        private static string FormatHudEditorElement(HudEditorElement element)
            => $"#{element.Id} {element.AddonName}";

        private static void DrawSectionHeader(string label)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(label);
        }
    }
}
