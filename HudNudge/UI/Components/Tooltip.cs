using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace HudNudge.Windows.Components
{
    public static class Tooltip
    {
        public static void DrawCircularButtonWithTooltip(string tooltipText)
        {
            var buttonLabel = "?";
            var buttonSize = new Vector2(16f, 16f);
            var buttonColor = new Vector4(0.75f, 0.75f, 0.75f, 1f);

            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));

            ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, buttonColor);

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 1f));

            if (ImGui.Button(buttonLabel, buttonSize)) { }

            if (ImGui.IsItemHovered())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
                ImGui.SetTooltip(tooltipText);
                ImGui.PopStyleColor();
            }

            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);
        }
    }
}
