using System;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    public readonly struct BlendShareEmbeddedEditorContext
    {
        public BlendShareEmbeddedEditorContext(
            UnityEngine.Object embeddedObject,
            MeshDataObject ownerMesh,
            BlendShareObject ownerPatch,
            Action refresh,
            GameObject fbxGo = null,
            UnityMeshTargetLookup unityMeshLookup = null,
            bool createUnityMeshLookup = true)
        {
            EmbeddedObject = embeddedObject;
            OwnerMeshData = ownerMesh;
            OwnerPatch = ownerPatch;
            Refresh = refresh;
            FbxGo = fbxGo != null ? fbxGo : ownerPatch != null ? ownerPatch.m_Target : null;
            UnityMeshLookup = FbxGo != null
                ? unityMeshLookup ?? (createUnityMeshLookup ? UnityMeshTargetLookup.Create(FbxGo) : null)
                : null;
        }

        public UnityEngine.Object EmbeddedObject { get; }
        public MeshDataObject OwnerMeshData { get; }
        public BlendShareObject OwnerPatch { get; }
        public Action Refresh { get; }
        public GameObject FbxGo { get; }
        public UnityMeshTargetLookup UnityMeshLookup { get; }

        public bool TryGetUnityMesh(string path, out Mesh mesh)
        {
            mesh = null;
            return UnityMeshLookup != null && UnityMeshLookup.TryGetMesh(path, out mesh);
        }

        public bool TryGetUnityMesh(MeshDataObject meshData, out Mesh mesh)
        {
            mesh = null;
            return UnityMeshLookup != null && UnityMeshLookup.TryGetMesh(meshData, out mesh);
        }

        public string GetUnityMeshResolutionError(MeshDataObject meshData)
        {
            return UnityMeshLookup != null
                ? UnityMeshLookup.GetResolutionError(meshData)
                : Localization.S("patch.mapping.target_unreadable");
        }
    }
}
