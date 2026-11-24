using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.EditorAssets.Settings;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Shared context passed to IFeatureInspectorDrawer implementations.
    /// </summary>
    public sealed class FeatureInspectorContext
    {
        public BlendShareAsset PatchAsset { get; }
        public string AssetGuid { get; }
        public string MeshId { get; }
        public BlendShareSettingsUndoBuffer Buffer { get; }
        public bool IsReadOnly { get; }

        public FeatureInspectorContext(
            BlendShareAsset patchAsset,
            string assetGuid,
            string meshId,
            BlendShareSettingsUndoBuffer buffer,
            bool isReadOnly)
        {
            PatchAsset = patchAsset;
            AssetGuid = assetGuid ?? string.Empty;
            MeshId = meshId ?? string.Empty;
            Buffer = buffer;
            IsReadOnly = isReadOnly;
        }

        private static readonly HashSet<string> s_PendingFlushes = new HashSet<string>(StringComparer.Ordinal);

        public void RecordEditAndCommit(string undoLabel, Action<BlendShareImportSettings> mutate, bool commitOnEndOfGui = true)
        {
            if (IsReadOnly || Buffer == null || mutate == null) return;
            Undo.RecordObject(Buffer, undoLabel);
            mutate(Buffer.Settings);
            EditorUtility.SetDirty(Buffer);
            BlendShareSettingsSession.NotifyBufferMutated(Buffer);
            if (commitOnEndOfGui && !string.IsNullOrEmpty(AssetGuid))
            {
                ScheduleDeferredFlush(AssetGuid);
            }
        }

        internal static void ScheduleDeferredFlush(string guid)
        {
            if (string.IsNullOrEmpty(guid) || !s_PendingFlushes.Add(guid)) return;
            string captured = guid;
            EditorApplication.delayCall += () =>
            {
                s_PendingFlushes.Remove(captured);
                BlendShareSettingsSession.Flush(captured);
            };
        }
    }

    /// <summary>
    /// Per-feature Inspector UI drawer contract (Capability Registry #7).
    /// </summary>
    public interface IFeatureInspectorDrawer
    {
        string FeatureTypeId { get; }
        int DisplayOrder { get; }
        void DrawInspector(BlendShareFeatureData feature, FeatureInspectorContext context);
    }

    /// <summary>
    /// Deterministic, lazily initialized registry of IFeatureInspectorDrawer keyed by FeatureTypeId.
    /// Falls back to UnknownFeatureInspectorDrawer for unregistered or forward-versioned features.
    /// </summary>
    public static class FeatureInspectorRegistry
    {
        private static readonly object s_Lock = new object();
        private static Dictionary<string, IFeatureInspectorDrawer> s_Drawers;
        private static readonly IFeatureInspectorDrawer s_FallbackDrawer = new UnknownFeatureInspectorDrawer();

        public static void Register(IFeatureInspectorDrawer drawer)
        {
            if (drawer == null) throw new ArgumentNullException(nameof(drawer));
            if (string.IsNullOrWhiteSpace(drawer.FeatureTypeId))
            {
                throw new ArgumentException("Drawer FeatureTypeId must be non-empty.", nameof(drawer));
            }

            lock (s_Lock)
            {
                EnsureInitializedLocked();
                if (s_Drawers.ContainsKey(drawer.FeatureTypeId))
                {
                    throw new InvalidOperationException($"Duplicate IFeatureInspectorDrawer registered for '{drawer.FeatureTypeId}'.");
                }
                s_Drawers.Add(drawer.FeatureTypeId, drawer);
            }
        }

        public static IFeatureInspectorDrawer Resolve(BlendShareFeatureData feature)
        {
            if (feature == null || feature is UnknownFeatureData)
            {
                return s_FallbackDrawer;
            }

            return Resolve(feature.TypeId);
        }

        public static IFeatureInspectorDrawer Resolve(string featureTypeId)
        {
            if (string.IsNullOrEmpty(featureTypeId)) return s_FallbackDrawer;
            lock (s_Lock)
            {
                EnsureInitializedLocked();
                return s_Drawers.TryGetValue(featureTypeId, out var drawer) ? drawer : s_FallbackDrawer;
            }
        }

        private static void EnsureInitializedLocked()
        {
            if (s_Drawers != null) return;
            s_Drawers = new Dictionary<string, IFeatureInspectorDrawer>(StringComparer.Ordinal)
            {
                { BlendShapeFeature.FeatureTypeId, new BlendShapeInspectorDrawer() },
                { SkinWeightFeature.FeatureTypeId, new SkinWeightInspectorDrawer() }
            };
        }
    }

    /// <summary>
    /// Read-only fallback card for unknown or future-version features.
    /// </summary>
    public sealed class UnknownFeatureInspectorDrawer : IFeatureInspectorDrawer
    {
        public string FeatureTypeId => "*";
        public int DisplayOrder => int.MaxValue;

        public void DrawInspector(BlendShareFeatureData feature, FeatureInspectorContext context)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            string typeId = feature != null ? feature.TypeId : "(null)";
            int version = feature != null ? feature.SchemaVersion : 0;
            bool required = feature != null && feature.Required;

            EditorGUILayout.LabelField($"Unsupported Feature: {typeId} (v{version})", EditorStyles.boldLabel);
            if (feature is UnknownFeatureData unknown)
            {
                EditorGUILayout.LabelField("Estimated Payload Bytes", $"{unknown.EstimatedPayloadBytes:N0} B");
            }

            EditorGUILayout.HelpBox(
                required
                    ? "This feature is marked Required and is not supported by this version of BlendShare. Apply/Export operations will be blocked."
                    : "This feature is Optional and is preserved read-only without blocking supported features.",
                required ? MessageType.Error : MessageType.Info);
            EditorGUILayout.EndVertical();
        }
    }
}
