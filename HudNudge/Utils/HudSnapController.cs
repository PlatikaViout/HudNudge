using System;

namespace HudNudge
{
    public enum HudSnapMode
    {
        Both,
        Elements,
        ScreenCenter
    }

    public readonly record struct HudSnapRect(
        string Name,
        float X,
        float Y,
        float Width,
        float Height)
    {
        public float Left => X;
        public float Right => X + Width;
        public float Top => Y;
        public float Bottom => Y + Height;
        public float CenterX => X + Width / 2f;
        public float CenterY => Y + Height / 2f;
    }

    public readonly record struct HudSnapResult(
        bool Found,
        float DeltaX,
        float DeltaY,
        string TargetName,
        string Description,
        float SourceX,
        float SourceY,
        float TargetX,
        float TargetY);

    public sealed class HudSnapController
    {
        private readonly HudLayoutController hudLayout;

        public HudSnapController(HudLayoutController hudLayout)
        {
            this.hudLayout = hudLayout;
        }

        public HudSnapResult PreviewSelectedSnap(float threshold, HudSnapMode mode)
            => FindBestSnap(threshold, mode);

        private HudSnapResult FindBestSnap(float threshold, HudSnapMode mode)
        {
            if (!hudLayout.TryGetSelectedBounds(out var selected))
                return default;

            var bestDistance = float.MaxValue;
            var best = default(HudSnapResult);

            var (screenWidth, screenHeight) = hudLayout.GetScreenDimensions();

            if (mode is HudSnapMode.Both or HudSnapMode.Elements)
            {
                foreach (var other in hudLayout.GetVisibleHudElementBounds())
                {
                    if (other.Name == selected.Name)
                        continue;

                    CheckSnapPoint(selected.Left, selected.Top, other.Right, other.Top, other, "left-top -> right-top",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Left, selected.Bottom, other.Right, other.Bottom, other,
                                   "left-bottom -> right-bottom", threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Top, other.Left, other.Top, other, "right-top -> left-top",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Bottom, other.Left, other.Bottom, other,
                                   "right-bottom -> left-bottom", threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.Left, selected.Top, other.Left, other.Bottom, other, "top-left -> bottom-left",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Top, other.Right, other.Bottom, other,
                                   "top-right -> bottom-right", threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.Left, selected.Bottom, other.Left, other.Top, other, "bottom-left -> top-left",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Bottom, other.Right, other.Top, other,
                                   "bottom-right -> top-right", threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.Left, selected.CenterY, other.Right, other.CenterY, other,
                                   "left-center -> right-center", threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.CenterY, other.Left, other.CenterY, other,
                                   "right-center -> left-center", threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.CenterX, selected.Top, other.CenterX, other.Bottom, other,
                                   "top-center -> bottom-center", threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.CenterX, selected.Bottom, other.CenterX, other.Top, other,
                                   "bottom-center -> top-center", threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.Left, selected.Top, other.Right, other.Bottom, other, "top-left -> bottom-right",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Top, other.Left, other.Bottom, other, "top-right -> bottom-left",
                                   threshold, ref bestDistance, ref best);

                    CheckSnapPoint(selected.Left, selected.Bottom, other.Right, other.Top, other, "bottom-left -> top-right",
                                   threshold, ref bestDistance, ref best);
                    CheckSnapPoint(selected.Right, selected.Bottom, other.Left, other.Top, other, "bottom-right -> top-left",
                                   threshold, ref bestDistance, ref best);
                }
            }

            if (mode is HudSnapMode.Both or HudSnapMode.ScreenCenter)
            {
                CheckScreenCenterSnap(selected, screenWidth, screenHeight, threshold, ref bestDistance, ref best);
            }

            if (!best.Found)
                return best;

            return best;
        }

        private static void CheckScreenCenterSnap(
            HudSnapRect selected,
            int screenWidth,
            int screenHeight,
            float threshold,
            ref float bestDistance,
            ref HudSnapResult best)
        {
            var centerX = screenWidth / 2f;
            var centerY = screenHeight / 2f;
            var dx = centerX - selected.CenterX;
            var dy = centerY - selected.CenterY;
            var snapX = MathF.Abs(dx) <= threshold;
            var snapY = MathF.Abs(dy) <= threshold;

            if (!snapX && !snapY)
                return;

            var finalDx = snapX ? dx : 0;
            var finalDy = snapY ? dy : 0;
            var distance = MathF.Abs(finalDx) + MathF.Abs(finalDy);

            if (distance >= bestDistance)
                return;

            bestDistance = distance;
            best = new HudSnapResult(
                true,
                finalDx,
                finalDy,
                "screen center",
                snapX && snapY ? "center -> screen-center" : snapX ? "center-x -> screen-center" : "center-y -> screen-center",
                selected.CenterX,
                selected.CenterY,
                centerX,
                centerY);
        }

        void CheckSnapPoint(
            float sourceX,
            float sourceY,
            float targetX,
            float targetY,
            HudSnapRect target,
            string description,
            float threshold,
            ref float bestDistance,
            ref HudSnapResult best)
        {
            var dx = targetX - sourceX;
            var dy = targetY - sourceY;

            if (MathF.Abs(dx) > threshold || MathF.Abs(dy) > threshold)
                return;

            var distance = MathF.Abs(dx) + MathF.Abs(dy);

            if (distance >= bestDistance)
                return;

            bestDistance = distance;

            best = new HudSnapResult(
                true,
                dx,
                dy,
                target.Name,
                description,
                sourceX,
                sourceY,
                targetX,
                targetY);
        }
    }
}
