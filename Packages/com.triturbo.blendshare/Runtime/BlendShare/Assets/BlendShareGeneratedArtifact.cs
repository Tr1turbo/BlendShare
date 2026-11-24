using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Runtime.Assets
{
    [Serializable]
    public sealed class GeneratedBoneBindingRecord
    {
        [SerializeField] private string m_PatchId = string.Empty;
        [SerializeField] private string m_ArmatureId = string.Empty;
        [SerializeField] private string m_SourceBonePath = string.Empty;
        [SerializeField] private string m_TargetBonePath = string.Empty;
        [SerializeField] private Matrix4x4 m_BindPose = Matrix4x4.identity;
        [SerializeField] private int m_BoneIndex;
        [SerializeField] private bool m_IsNewBone;

        public string PatchId => m_PatchId;
        public string ArmatureId => m_ArmatureId;
        public string SourceBonePath => m_SourceBonePath;
        public string TargetBonePath => m_TargetBonePath;
        public Matrix4x4 BindPose => m_BindPose;
        public int BoneIndex => m_BoneIndex;
        public bool IsNewBone => m_IsNewBone;

        public GeneratedBoneBindingRecord()
        {
        }

        public GeneratedBoneBindingRecord(
            string patchId,
            string armatureId,
            string sourceBonePath,
            string targetBonePath,
            Matrix4x4 bindPose,
            int boneIndex,
            bool isNewBone)
        {
            m_PatchId = patchId ?? string.Empty;
            m_ArmatureId = armatureId ?? string.Empty;
            m_SourceBonePath = sourceBonePath ?? string.Empty;
            m_TargetBonePath = targetBonePath ?? string.Empty;
            m_BindPose = bindPose;
            m_BoneIndex = boneIndex;
            m_IsNewBone = isNewBone;
        }
    }

    [Serializable]
    public sealed class GeneratedMeshEntry
    {
        [SerializeField] private string m_MeshId = string.Empty;
        [SerializeField] private string m_NodePath = string.Empty;
        [SerializeField] private Mesh m_Mesh;
        [SerializeField] private string[] m_BonePaths = Array.Empty<string>();
        [SerializeField] private GeneratedBoneBindingRecord[] m_BoneBindings = Array.Empty<GeneratedBoneBindingRecord>();

        public string MeshId => m_MeshId;
        public string NodePath => m_NodePath;
        public Mesh Mesh => m_Mesh;
        public IReadOnlyList<string> BonePaths => m_BonePaths ?? Array.Empty<string>();
        public IReadOnlyList<GeneratedBoneBindingRecord> BoneBindings => m_BoneBindings ?? Array.Empty<GeneratedBoneBindingRecord>();

        public GeneratedMeshEntry()
        {
        }

        public GeneratedMeshEntry(
            string meshId,
            string nodePath,
            Mesh mesh,
            string[] bonePaths,
            GeneratedBoneBindingRecord[] boneBindings)
        {
            m_MeshId = meshId ?? string.Empty;
            m_NodePath = nodePath ?? string.Empty;
            m_Mesh = mesh;
            m_BonePaths = bonePaths ?? Array.Empty<string>();
            m_BoneBindings = boneBindings ?? Array.Empty<GeneratedBoneBindingRecord>();
        }
    }

    /// <summary>
    /// BlendShare 2.0 generated mesh artifact receiving generated meshes and exact applied bone-bindings
    /// directly from the shared Unity generation pipeline. Zero Editor or FBX dependencies.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class BlendShareGeneratedArtifact : ScriptableObject
    {
        [SerializeField] private UnityEngine.Object m_TargetSource;
        [SerializeField] private string[] m_AppliedPatchIds = Array.Empty<string>();
        [SerializeField] private GeneratedMeshEntry[] m_Meshes = Array.Empty<GeneratedMeshEntry>();

        public UnityEngine.Object TargetSource => m_TargetSource;
        public IReadOnlyList<string> AppliedPatchIds => m_AppliedPatchIds ?? Array.Empty<string>();
        public IReadOnlyList<GeneratedMeshEntry> Meshes => m_Meshes ?? Array.Empty<GeneratedMeshEntry>();

        public void Initialize(
            UnityEngine.Object targetSource,
            string[] appliedPatchIds,
            GeneratedMeshEntry[] meshes)
        {
            m_TargetSource = targetSource;
            m_AppliedPatchIds = appliedPatchIds ?? Array.Empty<string>();
            m_Meshes = meshes ?? Array.Empty<GeneratedMeshEntry>();
        }
    }
}
