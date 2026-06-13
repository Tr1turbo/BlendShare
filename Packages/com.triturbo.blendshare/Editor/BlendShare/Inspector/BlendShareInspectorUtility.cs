using System;
using System.Collections.Generic;
using System.Linq;
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
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Mesh data is missing.");
            }

            var stored = mesh.m_FbxTopologySignature;
            if (stored == null || !stored.IsValid || stored.ControlPointCount < 0 || string.IsNullOrEmpty(stored.Hash))
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Stored topology signature is missing. Re-extract or rebuild the signature from the source FBX.");
            }

            if (patch == null || patch.m_Original == null)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Target FBX is not assigned.");
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(patch.m_Original);
            if (!sceneResult.Success)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", sceneResult.Message);
            }

            using var scene = sceneResult.Value;
            var meshResult = FbxUnityAssetReader.FindMesh(scene, mesh.m_Path);
            if (!meshResult.Success)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.MissingTargetMesh, "Missing Target Mesh", meshResult.Message);
            }

            var targetSignature = FbxTopologyHash.Calculate(meshResult.Value, stored.ControlPointCount);
            if (meshResult.Value.ControlPointCount < stored.ControlPointCount)
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    "Incompatible",
                    $"Target mesh has fewer control points ({meshResult.Value.ControlPointCount}) than the stored source mesh ({stored.ControlPointCount}).");
            }

            if (!targetSignature.IsValid)
            {
                return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Target topology signature could not be calculated.");
            }

            if (!StringComparer.Ordinal.Equals(stored.Hash, targetSignature.Hash))
            {
                return new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    "Incompatible",
                    "Target mesh does not match the stored source topology signature.");
            }

            return new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Verified, "Verified", "Target FBX topology matches the stored mesh signature.");
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
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Stored topology signature is missing. Re-extract or rebuild the signature from the source FBX.");
                    continue;
                }

                pending.Add(mesh);
            }

            if (pending.Count == 0)
            {
                return statuses;
            }

            if (patch == null || patch.m_Original == null)
            {
                foreach (var mesh in pending)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Target FBX is not assigned.");
                }

                return statuses;
            }

            var sceneResult = FbxUnityAssetReader.ReadScene(patch.m_Original);
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
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.MissingTargetMesh, "Missing Target Mesh", meshResult.Message);
                    continue;
                }

                var targetSignature = FbxTopologyHash.Calculate(meshResult.Value, stored.ControlPointCount);
                if (meshResult.Value.ControlPointCount < stored.ControlPointCount)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Incompatible,
                        "Incompatible",
                        $"Target mesh has fewer control points ({meshResult.Value.ControlPointCount}) than the stored source mesh ({stored.ControlPointCount}).");
                    continue;
                }

                if (!targetSignature.IsValid)
                {
                    statuses[mesh] = new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, "Unknown", "Target topology signature could not be calculated.");
                    continue;
                }

                statuses[mesh] = StringComparer.Ordinal.Equals(stored.Hash, targetSignature.Hash)
                    ? new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Verified, "Verified", "Target FBX topology matches the stored mesh signature.")
                    : new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Incompatible,
                        "Incompatible",
                        "Target mesh does not match the stored source topology signature.");
            }

            return statuses;
        }

        public static MeshMappingStatus GetMappingStatus(BlendShareObject patch, MeshDataObject mesh)
        {
            if (mesh == null)
            {
                return new MeshMappingStatus("Unknown", "Mesh data is missing.", false, StatusKind.Neutral);
            }

            if (patch == null || patch.m_Original == null)
            {
                return new MeshMappingStatus("Missing Target", "Original FBX is not assigned.", false, StatusKind.Warning);
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Original);
            if (targetLookup == null)
            {
                return new MeshMappingStatus("Unknown", "Unity mesh target cannot be read.", false, StatusKind.Neutral);
            }

            if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
            {
                return new MeshMappingStatus("Missing", targetLookup.GetResolutionError(mesh), false, StatusKind.Warning);
            }

            var mappings = mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>();
            bool hasMapping = mappings.Any(mapping => mapping != null);
            bool hasValidMapping = mappings.Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh));
            if (hasValidMapping)
            {
                return new MeshMappingStatus("Ready", targetMesh.name, false, StatusKind.Success);
            }

            return new MeshMappingStatus(
                hasMapping ? "Invalid" : "Missing",
                hasMapping ? "No stored mapping matches the current Unity mesh import." : "No Unity mapping is stored for this mesh.",
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
