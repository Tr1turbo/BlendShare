using System;
using System.Collections.Generic;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    public enum MeshFbxCompatibilityState
    {
        Unknown,
        Verified,
        MissingTargetMesh,
        Incompatible
    }

    public readonly struct MeshFbxCompatibilityStatus
    {
        public MeshFbxCompatibilityStatus(MeshFbxCompatibilityState state, string label, string message)
        {
            State = state;
            Label = label;
            Message = message;
        }

        public MeshFbxCompatibilityState State { get; }
        public string Label { get; }
        public string Message { get; }

        public StatusKind Kind => State switch
        {
            MeshFbxCompatibilityState.Verified => StatusKind.Success,
            MeshFbxCompatibilityState.Incompatible => StatusKind.Error,
            MeshFbxCompatibilityState.MissingTargetMesh => StatusKind.Warning,
            _ => StatusKind.Neutral
        };
    }

    public readonly struct MeshMappingStatus
    {
        public MeshMappingStatus(string label, string message, bool canCreateMappings, StatusKind kind)
        {
            Label = label;
            Message = message;
            CanCreateMappings = canCreateMappings;
            Kind = kind;
        }

        public string Label { get; }
        public string Message { get; }
        public bool CanCreateMappings { get; }
        public StatusKind Kind { get; }
        public bool IsReady => Kind == StatusKind.Success;
    }

    public static class BlendShareInspectorUtility
    {
        private const string k_EditorPrefPrefix = "com.triturbo.blendshare";

        public static Texture LoadUnityIconTexture(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName))
            {
                return null;
            }

            return EditorGUIUtility.IconContent(iconName)?.image;
        }

        public static Texture LoadIconTexture(string iconNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(iconNameOrPath))
            {
                return null;
            }

            return LoadUnityIconTexture(iconNameOrPath) ?? LoadAssetTexture(iconNameOrPath);
        }

        public static Texture LoadAssetTexture(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<Texture>(NormalizeAssetPath(assetPath));
        }

        private static string NormalizeAssetPath(string path)
        {
            path = path.Replace('\\', '/');
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "/Assets".Length);
            if (path.StartsWith(projectRoot + "/", StringComparison.Ordinal))
            {
                return path.Substring(projectRoot.Length + 1);
            }

            return path;
        }

        public static BlendShareAsset FindOwnerPatch(UnityEngine.Object child)
        {
            if (child == null)
            {
                return null;
            }

            if (child is BlendShareAsset patchAsset)
            {
                return patchAsset;
            }

            if (child is BlendShareMeshAsset meshAsset && meshAsset.Root != null)
            {
                return meshAsset.Root;
            }

            string path = AssetDatabase.GetAssetPath(child);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<BlendShareAsset>(path);
        }

        public static string GetAssetEditorPrefKey(UnityEngine.Object asset, string key)
        {
            if (asset == null || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string path = AssetDatabase.GetAssetPath(asset);
            string guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                return string.Empty;
            }

            return $"{k_EditorPrefPrefix}.{guid}.{key}";
        }

        public static bool GetAssetEditorPrefBool(UnityEngine.Object asset, string key, bool defaultValue)
        {
            string prefKey = GetAssetEditorPrefKey(asset, key);
            return string.IsNullOrEmpty(prefKey) ? defaultValue : EditorPrefs.GetBool(prefKey, defaultValue);
        }

        public static void SetAssetEditorPrefBool(UnityEngine.Object asset, string key, bool value, bool defaultValue)
        {
            string prefKey = GetAssetEditorPrefKey(asset, key);
            if (string.IsNullOrEmpty(prefKey))
            {
                return;
            }

            if (value == defaultValue)
            {
                EditorPrefs.DeleteKey(prefKey);
                return;
            }

            EditorPrefs.SetBool(prefKey, value);
        }

        public static MeshFbxCompatibilityStatus GetFbxCompatibilityStatus(
            GameObject targetFbx,
            MeshDocument mesh)
        {
            if (mesh == null)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.unknown"),
                    Localization.S("patch.mesh_data.missing"));
            }

            var stored = mesh.Topology;
            if (stored == null || !stored.IsValid || stored.ControlPointCount < 0 || string.IsNullOrEmpty(stored.Hash))
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.unknown"),
                    Localization.S("patch.compatibility.signature_missing"));
            }

            if (targetFbx == null)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.unknown"),
                    Localization.S("patch.mapping.target_missing"));
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(targetFbx, UfbxLoadOptions.Default);
            if (!sceneResult.Success || sceneResult.Value == null)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.unknown"),
                    sceneResult.Message);
            }

            using var scene = sceneResult.Value;
            return EvaluateMeshAgainstScene(scene, mesh);
        }

        public static Dictionary<string, MeshFbxCompatibilityStatus> GetFbxCompatibilityStatuses(
            GameObject targetFbx,
            IEnumerable<MeshDocument> meshes)
        {
            var statuses = new Dictionary<string, MeshFbxCompatibilityStatus>(StringComparer.Ordinal);
            var pending = new List<MeshDocument>();
            foreach (var mesh in meshes ?? Array.Empty<MeshDocument>())
            {
                if (mesh == null || string.IsNullOrEmpty(mesh.MeshId))
                {
                    continue;
                }

                var stored = mesh.Topology;
                if (stored == null || !stored.IsValid || stored.ControlPointCount < 0 || string.IsNullOrEmpty(stored.Hash))
                {
                    statuses[mesh.MeshId] = new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Unknown,
                        Localization.S("common.status.unknown"),
                        Localization.S("patch.compatibility.signature_missing"));
                    continue;
                }

                pending.Add(mesh);
            }

            if (pending.Count == 0)
            {
                return statuses;
            }

            if (targetFbx == null)
            {
                foreach (var mesh in pending)
                {
                    statuses[mesh.MeshId] = new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Unknown,
                        Localization.S("common.status.unknown"),
                        Localization.S("patch.mapping.target_missing"));
                }

                return statuses;
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(targetFbx, UfbxLoadOptions.Default);
            if (!sceneResult.Success || sceneResult.Value == null)
            {
                foreach (var mesh in pending)
                {
                    statuses[mesh.MeshId] = new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Unknown,
                        Localization.S("common.status.unknown"),
                        sceneResult.Message);
                }

                return statuses;
            }

            using var scene = sceneResult.Value;
            foreach (var mesh in pending)
            {
                statuses[mesh.MeshId] = EvaluateMeshAgainstScene(scene, mesh);
            }

            return statuses;
        }

        public static MeshMappingStatus GetMappingStatus(GameObject targetFbx, MeshDocument mesh)
        {
            if (mesh == null)
            {
                return new MeshMappingStatus(
                    Localization.S("common.status.unknown"),
                    Localization.S("patch.mesh_data.missing"),
                    false,
                    StatusKind.Neutral);
            }

            bool hasEmbedded = mesh.OriginBinding != null && mesh.OriginBinding.HasPayload;
            if (targetFbx == null)
            {
                if (hasEmbedded)
                {
                    return new MeshMappingStatus(
                        Localization.S("common.status.ready"),
                        Localization.S("patch.mapping.embedded_ready", "Embedded OriginBinding ready (no base FBX required)."),
                        false,
                        StatusKind.Success);
                }

                return new MeshMappingStatus(
                    Localization.S("common.status.missing_target"),
                    Localization.S("patch.mapping.target_missing"),
                    false,
                    StatusKind.Warning);
            }

            var fbxStatus = GetFbxCompatibilityStatus(targetFbx, mesh);
            if (fbxStatus.State == MeshFbxCompatibilityState.Verified)
            {
                return new MeshMappingStatus(
                    Localization.S("common.status.ready"),
                    hasEmbedded
                        ? Localization.S("patch.mapping.embedded_and_fbx_ready", "Embedded OriginBinding + Target FBX verified.")
                        : targetFbx.name,
                    false,
                    StatusKind.Success);
            }

            if (hasEmbedded)
            {
                return new MeshMappingStatus(
                    Localization.S("common.status.ready"),
                    Localization.S("patch.mapping.embedded_ready", "Embedded OriginBinding ready."),
                    false,
                    StatusKind.Success);
            }

            return new MeshMappingStatus(
                fbxStatus.Label,
                fbxStatus.Message,
                false,
                fbxStatus.Kind);
        }

        public static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return "-";
            }

            return hash.Length <= 10 ? hash : hash.Substring(0, 10);
        }

        private static MeshFbxCompatibilityStatus EvaluateMeshAgainstScene(IFbxScene scene, MeshDocument mesh)
        {
            var stored = mesh.Topology;
            string nodePath = FbxNameUtility.NormalizePath(mesh.NodePath ?? string.Empty);
            var targetMesh = scene.FindMeshByNodePath(nodePath);
            if (targetMesh == null)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.MissingTargetMesh,
                    Localization.S("common.status.missing_target_mesh"),
                    Localization.SF("patch.compatibility.missing_mesh", nodePath));
            }

            if (targetMesh.ControlPointCount < stored.ControlPointCount)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    Localization.S("common.status.incompatible"),
                    Localization.SF("patch.compatibility.control_points_fewer", targetMesh.ControlPointCount, stored.ControlPointCount));
            }

            var targetDigest = FbxTopologyUtility.ComputeDigest(targetMesh, stored.ControlPointCount);
            if (!targetDigest.IsValid)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.unknown"),
                    Localization.S("patch.compatibility.signature_unavailable"));
            }

            return StringComparer.Ordinal.Equals(stored.Hash, targetDigest.Hash)
                ? new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Verified,
                    Localization.S("common.status.verified"),
                    Localization.S("patch.compatibility.verified"))
                : new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    Localization.S("common.status.incompatible"),
                    Localization.S("patch.compatibility.signature_mismatch"));
        }
    }
}
