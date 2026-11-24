using System;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    public static class BlendShareInspectorWidgets
    {
        private const float k_HeadFadeWidth = 24f;

        private static readonly Lazy<GUIStyle> s_PathLeftStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            });

        private static readonly Lazy<GUIStyle> s_PathRightStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip
            });

        /// <summary>Gets the width required to display a path without clipping.</summary>
        public static float GetPathLabelWidth(string path)
        {
            return s_PathLeftStyle.Value.CalcSize(new GUIContent(path ?? string.Empty)).x;
        }

        /// <summary>
        /// Draws a path left-aligned when it fits and right-aligned when clipped so its tail remains visible.
        /// </summary>
        public static void DrawPathLabel(
            Rect position,
            string path,
            string tooltip = null,
            bool fadeClippedHead = false)
        {
            string text = path ?? string.Empty;
            var content = new GUIContent(text, tooltip ?? text);
            if (GetPathLabelWidth(text) <= position.width)
            {
                GUI.Label(position, content, s_PathLeftStyle.Value);
                return;
            }

            if (!fadeClippedHead)
            {
                GUI.Label(position, content, s_PathRightStyle.Value);
                return;
            }

            DrawFadedClippedLabel(position, content);
        }

        private static void DrawFadedClippedLabel(Rect position, GUIContent content)
        {
            float fadeWidth = Mathf.Min(k_HeadFadeWidth, position.width);
            int fadeSteps = Mathf.Max(1, Mathf.CeilToInt(fadeWidth));
            float stepWidth = fadeWidth / fadeSteps;
            for (int step = 0; step < fadeSteps; step++)
            {
                var segment = new Rect(
                    position.x + step * stepWidth,
                    position.y,
                    stepWidth,
                    position.height);
                DrawClippedLabelSegment(
                    position,
                    segment,
                    content,
                    (step + 0.5f) / fadeSteps);
            }

            var opaqueSegment = new Rect(
                position.x + fadeWidth,
                position.y,
                Mathf.Max(0f, position.width - fadeWidth),
                position.height);
            DrawClippedLabelSegment(position, opaqueSegment, content, 1f);
        }

        private static void DrawClippedLabelSegment(
            Rect position,
            Rect segment,
            GUIContent content,
            float alpha)
        {
            if (segment.width <= 0f || segment.height <= 0f)
            {
                return;
            }

            GUI.BeginGroup(segment);
            Color previousColor = GUI.contentColor;
            try
            {
                GUI.contentColor = new Color(
                    previousColor.r,
                    previousColor.g,
                    previousColor.b,
                    previousColor.a * alpha);
                GUI.Label(
                    new Rect(
                        position.x - segment.x,
                        position.y - segment.y,
                        position.width,
                        position.height),
                    content,
                    s_PathRightStyle.Value);
            }
            finally
            {
                GUI.contentColor = previousColor;
                GUI.EndGroup();
            }
        }
    }
}
