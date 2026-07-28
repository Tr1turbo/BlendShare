using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEditorInternal;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    internal static class BlendShareSkinWeightSettings
    {
        private const string LimitSkinWeightsPreferenceKey =
            "com.triturbo.blendshare.limitSkinWeightsToFour";
        private const string LimitSkinWeightsMenuPath =
            "Tools/BlendShare/Limit Skin Weights to 4 Influences";

        internal static bool LimitSkinWeightsToFour =>
            EditorPrefs.GetBool(LimitSkinWeightsPreferenceKey, true);

        [MenuItem(LimitSkinWeightsMenuPath, false, 200)]
        private static void ToggleSkinWeightLimit()
        {
            EditorPrefs.SetBool(
                LimitSkinWeightsPreferenceKey,
                !LimitSkinWeightsToFour);
            ForceResetNdmfPreviewIfAvailable();
            InternalEditorUtility.RepaintAllViews();
        }

        [MenuItem(LimitSkinWeightsMenuPath, true)]
        private static bool ValidateSkinWeightLimit()
        {
            Menu.SetChecked(LimitSkinWeightsMenuPath, LimitSkinWeightsToFour);
            return true;
        }

        private static void ForceResetNdmfPreviewIfAvailable()
        {
            var previewType = Type.GetType("nadena.dev.ndmf.preview.NDMFPreview, nadena.dev.ndmf");
            var forceResetPreview = previewType?.GetMethod(
                "ForceResetPreview",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            forceResetPreview?.Invoke(null, null);
        }
    }

    internal readonly struct BlendShareSkinWeightInfluenceSummary : IEquatable<BlendShareSkinWeightInfluenceSummary>
    {
        internal BlendShareSkinWeightInfluenceSummary(
            int verticesOverFour,
            int verticesOverByteLimit,
            int maximumInfluences)
        {
            VerticesOverFour = verticesOverFour;
            VerticesOverByteLimit = verticesOverByteLimit;
            MaximumInfluences = maximumInfluences;
        }

        internal int VerticesOverFour { get; }
        internal int VerticesOverByteLimit { get; }
        internal int MaximumInfluences { get; }

        public bool Equals(BlendShareSkinWeightInfluenceSummary other)
        {
            return VerticesOverFour == other.VerticesOverFour &&
                   VerticesOverByteLimit == other.VerticesOverByteLimit &&
                   MaximumInfluences == other.MaximumInfluences;
        }

        public override bool Equals(object obj)
        {
            return obj is BlendShareSkinWeightInfluenceSummary other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = VerticesOverFour;
                hashCode = (hashCode * 397) ^ VerticesOverByteLimit;
                hashCode = (hashCode * 397) ^ MaximumInfluences;
                return hashCode;
            }
        }
    }

    internal static class BlendShareSkinWeightInfluenceDiagnostics
    {
        private static readonly ConditionalWeakTable<BlendShareMesh, Entry> Entries = new();

        internal static void Set(
            IEnumerable<BlendShareMesh> components,
            BlendShareSkinWeightInfluenceSummary summary)
        {
            bool changed = false;
            foreach (var component in (components ?? Enumerable.Empty<BlendShareMesh>())
                         .Where(component => component != null)
                         .Distinct())
            {
                if (!Entries.TryGetValue(component, out var existing))
                {
                    Entries.Add(component, new Entry(summary));
                    changed = true;
                    continue;
                }

                if (!existing.Summary.Equals(summary))
                {
                    existing.Summary = summary;
                    changed = true;
                }
            }

            if (changed)
            {
                InternalEditorUtility.RepaintAllViews();
            }
        }

        internal static bool TryGet(
            BlendShareMesh component,
            out BlendShareSkinWeightInfluenceSummary summary)
        {
            if (component != null &&
                Entries.TryGetValue(component, out var entry))
            {
                summary = entry.Summary;
                return true;
            }

            summary = default;
            return false;
        }

        internal static void Clear(BlendShareMesh component)
        {
            if (component != null)
            {
                Entries.Remove(component);
            }
        }

        private sealed class Entry
        {
            internal Entry(BlendShareSkinWeightInfluenceSummary summary)
            {
                Summary = summary;
            }

            internal BlendShareSkinWeightInfluenceSummary Summary { get; set; }
        }
    }
}
