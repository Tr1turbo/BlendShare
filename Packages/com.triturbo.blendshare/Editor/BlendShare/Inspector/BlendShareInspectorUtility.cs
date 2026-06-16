using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Hashing;
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
        private const string EditorPrefPrefix = "com.triturbo.blendshare";

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

        public static BlendShareObject FindOwnerPatch(UnityEngine.Object child)
        {
            if (child == null)
            {
                return null;
            }

            string path = AssetDatabase.GetAssetPath(child);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var patch = AssetDatabase.LoadAssetAtPath<BlendShareObject>(path);
            if (patch == child)
            {
                return patch;
            }

            return patch != null && Owns(patch, child) ? patch : null;
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

            return $"{EditorPrefPrefix}.{guid}.{key}";
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

        public static MeshDataObject FindOwnerMesh(UnityEngine.Object child, BlendShareObject patch = null)
        {
            if (child == null)
            {
                return null;
            }

            patch ??= FindOwnerPatch(child);
            foreach (var mesh in patch?.Meshes ?? Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                if (mesh == child)
                {
                    return mesh;
                }

                if (child is MeshFeatureObject feature && mesh.Features.Contains(feature))
                {
                    return mesh;
                }

                if (child is UnityVertexMappingObject mapping && (mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>()).Contains(mapping))
                {
                    return mesh;
                }
            }

            return null;
        }

        public static MeshFbxCompatibilityStatus GetFbxCompatibilityStatus(
            BlendShareObject patch,
            MeshDataObject mesh)
        {
            if (mesh == null)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.mesh_data.missing"));
            }

            var stored = mesh.m_FbxTopologySignature;
            if (stored == null || !stored.IsValid || stored.ControlPointCount < 0 || string.IsNullOrEmpty(stored.Hash))
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.compatibility.signature_missing"));
            }

            if (patch == null || patch.m_Target == null)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.mapping.target_missing"));
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(patch.m_Target);
            if (!sceneResult.Success)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", sceneResult.Message);
            }

            using var scene = sceneResult.Value;
            var meshResult = FbxUnityAssetReader.FindMesh(scene, mesh.m_Path);
            if (!meshResult.Success)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.MissingTargetMesh, Localization.S("common.status.missing_target_mesh"), meshResult.Message);
            }

            var targetSignature = FbxTopologyHash.Calculate(meshResult.Value, stored.ControlPointCount);
            if (meshResult.Value.ControlPointCount < stored.ControlPointCount)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    Localization.S("common.status.incompatible"),
                    Localization.SF("patch.compatibility.control_points_fewer", meshResult.Value.ControlPointCount, stored.ControlPointCount));
            }

            if (!targetSignature.IsValid)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.compatibility.signature_unavailable"));
            }

            if (!StringComparer.Ordinal.Equals(stored.Hash, targetSignature.Hash))
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    Localization.S("common.status.incompatible"),
                    Localization.S("patch.compatibility.signature_mismatch"));
            }

            return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Verified, Localization.S("common.status.verified"), Localization.S("patch.compatibility.verified"));
        }

        public static Dictionary<MeshDataObject, MeshFbxCompatibilityStatus> GetFbxCompatibilityStatuses(
            BlendShareObject patch,
            IEnumerable<MeshDataObject> meshes)
        {
            var statuses = new Dictionary<MeshDataObject, MeshFbxCompatibilityStatus>();
            var pending = new List<MeshDataObject>();
            foreach (var mesh in meshes ?? Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                var stored = mesh.m_FbxTopologySignature;
                if (stored == null || !stored.IsValid || stored.ControlPointCount < 0 || string.IsNullOrEmpty(stored.Hash))
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.compatibility.signature_missing"));
                    continue;
                }

                pending.Add(mesh);
            }

            if (pending.Count == 0)
            {
                return statuses;
            }

            if (patch == null || patch.m_Target == null)
            {
                foreach (var mesh in pending)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.mapping.target_missing"));
                }

                return statuses;
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(patch.m_Target);
            if (!sceneResult.Success)
            {
                foreach (var mesh in pending)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", sceneResult.Message);
                }

                return statuses;
            }

            using var scene = sceneResult.Value;
            foreach (var mesh in pending)
            {
                var stored = mesh.m_FbxTopologySignature;
                var meshResult = FbxUnityAssetReader.FindMesh(scene, mesh.m_Path);
                if (!meshResult.Success)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.MissingTargetMesh, Localization.S("common.status.missing_target_mesh"), meshResult.Message);
                    continue;
                }

                var targetSignature = FbxTopologyHash.Calculate(meshResult.Value, stored.ControlPointCount);
                if (meshResult.Value.ControlPointCount < stored.ControlPointCount)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Incompatible,
                        Localization.S("common.status.incompatible"),
                        Localization.SF("patch.compatibility.control_points_fewer", meshResult.Value.ControlPointCount, stored.ControlPointCount));
                    continue;
                }

                if (!targetSignature.IsValid)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.compatibility.signature_unavailable"));
                    continue;
                }

                statuses[mesh] = StringComparer.Ordinal.Equals(stored.Hash, targetSignature.Hash)
                    ? new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Verified, Localization.S("common.status.verified"), Localization.S("patch.compatibility.verified"))
                    : new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Incompatible,
                        Localization.S("common.status.incompatible"),
                        Localization.S("patch.compatibility.signature_mismatch"));
            }

            return statuses;
        }

        public static MeshMappingStatus GetMappingStatus(BlendShareObject patch, MeshDataObject mesh)
        {
            if (mesh == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mesh_data.missing"), false, StatusKind.Neutral);
            }

            if (patch == null || patch.m_Target == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.missing_target"), Localization.S("patch.mapping.target_missing"), false, StatusKind.Warning);
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Target);
            if (targetLookup == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mapping.target_unreadable"), false, StatusKind.Neutral);
            }

            if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
            {
                return new MeshMappingStatus(Localization.S("common.status.missing"), targetLookup.GetResolutionError(mesh), false, StatusKind.Warning);
            }

            var mappings = mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>();
            bool hasMapping = mappings.Any(mapping => mapping != null);
            bool hasValidMapping = mappings.Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh));
            if (hasValidMapping)
            {
                return new MeshMappingStatus(Localization.S("common.status.ready"), targetMesh.name, false, StatusKind.Success);
            }

            return new MeshMappingStatus(
                hasMapping ? Localization.S("common.status.invalid") : Localization.S("common.status.missing"),
                hasMapping ? Localization.S("patch.mapping.no_stored_match") : Localization.S("patch.mapping.no_mapping"),
                true,
                StatusKind.Warning);
        }

        public static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return "-";
            }

            return hash.Length <= 10 ? hash : hash.Substring(0, 10);
        }

        private static bool Owns(BlendShareObject patch, UnityEngine.Object child)
        {
            foreach (var mesh in patch.Meshes ?? Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                if (mesh == child)
                {
                    return true;
                }

                if (child is MeshFeatureObject feature && mesh.Features.Contains(feature))
                {
                    return true;
                }

                if (child is UnityVertexMappingObject mapping && (mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>()).Contains(mapping))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
