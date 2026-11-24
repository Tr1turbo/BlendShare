using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.Components;
using UnityEditor;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public static class BlendShareSkinWeightSettings
    {
        public const string LimitSkinWeightsPreferenceKey =
            "com.triturbo.blendshare.limitSkinWeightsToFour";

        public static event Action Changed;

        public static bool LimitSkinWeightsToFour =>
            EditorPrefs.GetBool(LimitSkinWeightsPreferenceKey, true);

        public static int EffectiveMaxBonesPerVertex =>
            LimitSkinWeightsToFour ? 4 : 255;

        public static void SetLimitSkinWeightsToFour(bool limitToFour)
        {
            if (LimitSkinWeightsToFour == limitToFour)
            {
                return;
            }

            EditorPrefs.SetBool(LimitSkinWeightsPreferenceKey, limitToFour);
            Changed?.Invoke();
        }
    }

    public readonly struct BlendShareSkinWeightInfluenceSummary : IEquatable<BlendShareSkinWeightInfluenceSummary>
    {
        public BlendShareSkinWeightInfluenceSummary(
            int verticesOverFour,
            int verticesOverByteLimit,
            int maximumInfluences)
        {
            VerticesOverFour = verticesOverFour;
            VerticesOverByteLimit = verticesOverByteLimit;
            MaximumInfluences = maximumInfluences;
        }

        public int VerticesOverFour { get; }
        public int VerticesOverByteLimit { get; }
        public int MaximumInfluences { get; }

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

    public static class BlendShareSkinWeightInfluenceDiagnostics
    {
        private static readonly ConditionalWeakTable<BlendShareMesh, Entry> s_Entries =
            new ConditionalWeakTable<BlendShareMesh, Entry>();

        public static event Action Changed;

        public static void Set(
            IEnumerable<BlendShareMesh> components,
            BlendShareSkinWeightInfluenceSummary summary)
        {
            bool changed = false;
            foreach (var component in (components ?? Enumerable.Empty<BlendShareMesh>())
                         .Where(component => component != null)
                         .Distinct())
            {
                if (!s_Entries.TryGetValue(component, out var existing))
                {
                    s_Entries.Add(component, new Entry(summary));
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
                Changed?.Invoke();
            }
        }

        public static bool TryGet(
            BlendShareMesh component,
            out BlendShareSkinWeightInfluenceSummary summary)
        {
            if (component != null &&
                s_Entries.TryGetValue(component, out var entry))
            {
                summary = entry.Summary;
                return true;
            }

            summary = default;
            return false;
        }

        public static void Clear(BlendShareMesh component)
        {
            if (component != null)
            {
                s_Entries.Remove(component);
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
