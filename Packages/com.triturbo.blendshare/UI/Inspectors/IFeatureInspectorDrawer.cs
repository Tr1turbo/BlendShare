using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.UI.ValueDrawers;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShapeShare;
using UnityEngine.UIElements;
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

        public PatchSpaceProvenance SpaceProvenance { get; }
        public SkeletonDocument Skeleton { get; }
        public MeshDocument MeshHeader { get; }
        public ILazyPayloadSource PayloadSource { get; }

        public FeatureInspectorContext(
            BlendShareAsset patchAsset,
            string assetGuid,
            string meshId,
            BlendShareSettingsUndoBuffer buffer,
            bool isReadOnly,
            MeshDocument meshHeader = null,
            PatchSpaceProvenance spaceProvenance = null,
            SkeletonDocument skeleton = null,
            ILazyPayloadSource payloadSource = null)
        {
            MeshHeader = meshHeader;
            SpaceProvenance = spaceProvenance ?? BlendShareAssetTocMapper.GetSpaceProvenance(patchAsset);
            Skeleton = skeleton;
            PayloadSource = payloadSource ?? BlendShareSourceRepository.Shared.PayloadStore;
            PatchAsset = patchAsset;
            AssetGuid = assetGuid ?? string.Empty;
            MeshId = meshId ?? string.Empty;
            Buffer = buffer;
            IsReadOnly = isReadOnly;
        }

        internal Foldout Advanced(object record)
        {
            var foldout = new Foldout { text = Localization.S("common.advanced"), value = false };
            foldout.Add(new BlendShareRecordView(record, PayloadSource, startExpanded: false,
                positionUnitMeters: SpaceProvenance?.OriginRawMetersPerUnit ?? 1d));
            return foldout;
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
        VisualElement CreateInspectorElement(BlendShareFeatureData feature, FeatureInspectorContext context);
        VisualElement CreateCompactBadgeElement(BlendShareFeatureData feature, FeatureInspectorContext context);
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

        public VisualElement CreateInspectorElement(BlendShareFeatureData feature, FeatureInspectorContext context)
        {
            var box = BlendShareInspectorUi.Box();
            box.Add(new Label($"{feature?.TypeId} (v{feature?.SchemaVersion})"));
            if (feature is UnknownFeatureData unknown)
                box.Add(BlendShareInspectorUi.Row("Payload", $"{unknown.EstimatedPayloadBytes:N0} B"));
            bool required = feature?.Required == true;
            box.Add(new HelpBox(required ? "Unsupported required feature. Generation is blocked."
                : "Unsupported optional feature. Its data is preserved.", required ? HelpBoxMessageType.Error : HelpBoxMessageType.Info));
            return box;
        }

        public VisualElement CreateCompactBadgeElement(BlendShareFeatureData feature, FeatureInspectorContext context) =>
            new BlendShareFeatureBadge(feature?.TypeId, CreateInspectorElement(feature, context));
    }
}
