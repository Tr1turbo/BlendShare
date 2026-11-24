using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEngine;
#if BS_HAS_VRCSDK3_BASE
using VRC.SDKBase;
#endif

namespace Triturbo.BlendShare.Runtime.Components
{
    /// <summary>
    /// Maps one source armature bone path to a concrete scene <see cref="Transform"/>.
    /// </summary>
    [Serializable]
    public sealed class BlendShareBoneProxyEntry
    {
        [SerializeField]
        private string m_SourceBonePath = string.Empty;

        [SerializeField]
        private Transform m_TargetTransform;

        public string SourceBonePath
        {
            get => m_SourceBonePath ?? string.Empty;
            set => m_SourceBonePath = value ?? string.Empty;
        }

        public Transform TargetTransform
        {
            get => m_TargetTransform;
            set => m_TargetTransform = value;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(m_SourceBonePath) && m_TargetTransform != null;

        public BlendShareBoneProxyEntry()
        {
        }

        public BlendShareBoneProxyEntry(string sourceBonePath, Transform targetTransform)
        {
            SourceBonePath = sourceBonePath;
            TargetTransform = targetTransform;
        }
    }

    /// <summary>
    /// BlendShare 2.0 bone proxy component scoped strictly by <c>(BlendShareAsset, ArmatureId)</c>
    /// to prevent collisions when multiple patches define armatures with the same name or ID.
    /// Pure authoring data container; never mutates scene objects or marks scenes dirty.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Patch Bone Proxy")]
    public sealed class BlendSharePatchBoneProxy : MonoBehaviour
#if BS_HAS_VRCSDK3_BASE
        , IEditorOnly
#endif
    {
        [SerializeField]
        private BlendShareAsset m_Patch;

        [SerializeField]
        private string m_ArmatureId = "armature-0";

        [SerializeField]
        private Transform m_TargetParent;

        [SerializeField]
        private bool m_UseHierarchyParent = true;

        [SerializeField]
        private List<BlendShareBoneProxyEntry> m_Bindings = new List<BlendShareBoneProxyEntry>();

        public BlendShareAsset Patch
        {
            get => m_Patch;
            set => m_Patch = value;
        }

        public string ArmatureId
        {
            get => m_ArmatureId ?? string.Empty;
            set => m_ArmatureId = value ?? string.Empty;
        }

        public Transform TargetParent
        {
            get => m_TargetParent;
            set => m_TargetParent = value;
        }

        public bool UseHierarchyParent
        {
            get => m_UseHierarchyParent;
            set => m_UseHierarchyParent = value;
        }

        public IReadOnlyList<BlendShareBoneProxyEntry> Bindings =>
            m_Bindings ?? (m_Bindings = new List<BlendShareBoneProxyEntry>());

        public void AddBinding(string sourceBonePath, Transform targetTransform)
        {
            m_Bindings ??= new List<BlendShareBoneProxyEntry>();
            m_Bindings.Add(new BlendShareBoneProxyEntry(sourceBonePath, targetTransform));
        }

        public void SetBindings(IEnumerable<BlendShareBoneProxyEntry> entries)
        {
            m_Bindings = entries != null
                ? new List<BlendShareBoneProxyEntry>(entries)
                : new List<BlendShareBoneProxyEntry>();
        }
    }
}
