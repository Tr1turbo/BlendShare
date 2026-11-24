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
    public sealed class GeneratedArmatureBoneRecord
    {
        [SerializeField] private string m_Path = string.Empty;
        [SerializeField] private Vector3 m_LocalPosition = Vector3.zero;
        [SerializeField] private Quaternion m_LocalRotation = Quaternion.identity;
        [SerializeField] private Vector3 m_LocalScale = Vector3.one;
        [SerializeField] private bool m_CreateIfMissing = true;

        public string Path => m_Path;
        public Vector3 LocalPosition => m_LocalPosition;
        public Quaternion LocalRotation => m_LocalRotation;
        public Vector3 LocalScale => m_LocalScale;
        public bool CreateIfMissing => m_CreateIfMissing;

        public string ParentPath
        {
            get
            {
                if (string.IsNullOrEmpty(m_Path))
                {
                    return string.Empty;
                }

                int slash = m_Path.LastIndexOf('/');
                return slash > 0 ? m_Path.Substring(0, slash) : string.Empty;
            }
        }

        public string Name
        {
            get
            {
                if (string.IsNullOrEmpty(m_Path))
                {
                    return string.Empty;
                }

                int slash = m_Path.LastIndexOf('/');
                return slash >= 0 ? m_Path.Substring(slash + 1) : m_Path;
            }
        }

        public GeneratedArmatureBoneRecord()
        {
        }

        public GeneratedArmatureBoneRecord(
            string path,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale,
            bool createIfMissing)
        {
            m_Path = path ?? string.Empty;
            m_LocalPosition = localPosition;
            m_LocalRotation = localRotation;
            m_LocalScale = localScale == Vector3.zero ? Vector3.one : localScale;
            m_CreateIfMissing = createIfMissing;
        }

        public void ApplyLocalTransform(Transform target)
        {
            if (target == null)
            {
                return;
            }

            target.localPosition = m_LocalPosition;
            target.localRotation = m_LocalRotation;
            target.localScale = m_LocalScale == Vector3.zero ? Vector3.one : m_LocalScale;
        }

        public bool ApproximatelyMatches(GeneratedArmatureBoneRecord other, float tolerance = 1e-4f)
        {
            if (other == null)
            {
                return false;
            }

            return Vector3.Distance(m_LocalPosition, other.m_LocalPosition) <= tolerance &&
                   Mathf.Abs(Quaternion.Dot(m_LocalRotation, other.m_LocalRotation)) >= 1f - tolerance &&
                   Vector3.Distance(m_LocalScale, other.m_LocalScale) <= tolerance;
        }

        public bool ApproximatelyMatches(Transform target, float tolerance = 1e-4f)
        {
            if (target == null)
            {
                return false;
            }

            return Vector3.Distance(m_LocalPosition, target.localPosition) <= tolerance &&
                   Mathf.Abs(Quaternion.Dot(m_LocalRotation, target.localRotation)) >= 1f - tolerance &&
                   Vector3.Distance(m_LocalScale, target.localScale) <= tolerance;
        }
    }

    [Serializable]
    public sealed class GeneratedMeshEntry
    {
        [SerializeField] private string m_MeshId = string.Empty;
        [SerializeField] private string m_NodePath = string.Empty;
        [SerializeField] private Mesh m_Mesh;
        [SerializeField] private string m_RootBonePath = string.Empty;
        [SerializeField] private string[] m_BonePaths = Array.Empty<string>();
        [SerializeField] private GeneratedBoneBindingRecord[] m_BoneBindings = Array.Empty<GeneratedBoneBindingRecord>();

        public string MeshId => m_MeshId;
        public string NodePath => m_NodePath;
        public Mesh Mesh => m_Mesh;
        public string RootBonePath => m_RootBonePath;
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
            GeneratedBoneBindingRecord[] boneBindings,
            string rootBonePath = "")
        {
            m_MeshId = meshId ?? string.Empty;
            m_NodePath = nodePath ?? string.Empty;
            m_Mesh = mesh;
            m_RootBonePath = rootBonePath ?? string.Empty;
            m_BonePaths = bonePaths ?? Array.Empty<string>();
            m_BoneBindings = boneBindings ?? Array.Empty<GeneratedBoneBindingRecord>();
        }
    }

    /// <summary>
    /// BlendShare 2.0 generated mesh artifact receiving generated meshes, inline armature bone transforms,
    /// and exact applied bone-bindings directly from the shared Unity generation pipeline. Zero Editor or FBX dependencies.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class BlendShareGeneratedArtifact : ScriptableObject
    {
        [SerializeField] private UnityEngine.Object m_TargetSource;
        [SerializeField] private string[] m_AppliedPatchIds = Array.Empty<string>();
        [SerializeField] private GeneratedMeshEntry[] m_Meshes = Array.Empty<GeneratedMeshEntry>();
        [SerializeField] private GeneratedArmatureBoneRecord[] m_ArmatureBones = Array.Empty<GeneratedArmatureBoneRecord>();

        public UnityEngine.Object TargetSource => m_TargetSource;
        public IReadOnlyList<string> AppliedPatchIds => m_AppliedPatchIds ?? Array.Empty<string>();
        public IReadOnlyList<GeneratedMeshEntry> Meshes => m_Meshes ?? Array.Empty<GeneratedMeshEntry>();
        public IReadOnlyList<GeneratedArmatureBoneRecord> ArmatureBones => m_ArmatureBones ?? Array.Empty<GeneratedArmatureBoneRecord>();

        public void Initialize(
            UnityEngine.Object targetSource,
            string[] appliedPatchIds,
            GeneratedMeshEntry[] meshes,
            GeneratedArmatureBoneRecord[] armatureBones = null)
        {
            m_TargetSource = targetSource;
            m_AppliedPatchIds = appliedPatchIds ?? Array.Empty<string>();
            m_Meshes = meshes ?? Array.Empty<GeneratedMeshEntry>();
            m_ArmatureBones = armatureBones ?? Array.Empty<GeneratedArmatureBoneRecord>();
        }
    }
}
