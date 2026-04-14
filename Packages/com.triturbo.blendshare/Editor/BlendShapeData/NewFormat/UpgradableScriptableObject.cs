using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public abstract class UpgradableScriptableObject : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector]
        private int dataVersion = 0;

        public int DataVersion => dataVersion;

        protected abstract int CurrentVersion { get; }

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            UpgradeInMemory();
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (dataVersion < CurrentVersion)
            {
                UpgradeInMemory();
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }
#endif

        private void UpgradeInMemory()
        {
            while (dataVersion < CurrentVersion)
            {
                int before = dataVersion;

                UpgradeStep(dataVersion);

                if (dataVersion == before)
                {
                    Debug.LogError($"{name}: UpgradeStep did not update version from {before}");
                    break;
                }
            }
        }

        protected abstract void UpgradeStep(int fromVersion);

        protected void SetVersion(int version)
        {
            dataVersion = version;
        }
    }
}
