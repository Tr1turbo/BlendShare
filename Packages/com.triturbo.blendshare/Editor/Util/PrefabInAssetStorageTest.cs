using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShapeShare.Util
{
    public sealed class PrefabInAssetTestContainer : ScriptableObject
    {
        public GameObject m_PrefabReference;
        public Object m_AttemptedSubAssetReference;
    }

    public static class PrefabInAssetStorageTest
    {
        private const string OutputFolder = "Assets/BlendSharePrefabInAssetTest";

        [MenuItem("Tools/BlendShare/Debug/Test Prefab In .asset Storage")]
        public static void Run()
        {
            EnsureOutputFolder();

            string containerPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/GeneratedMeshAssetLikeContainer.asset");
            string prefabWithAssetExtensionPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/ArmaturePrefabSavedAsAsset.asset");
            string prefabControlPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/ArmaturePrefabControl.prefab");

            var container = ScriptableObject.CreateInstance<PrefabInAssetTestContainer>();
            AssetDatabase.CreateAsset(container, containerPath);

            GameObject subAssetAttempt = null;
            GameObject prefabAssetExtensionAttempt = null;
            GameObject prefabControlAttempt = null;

            try
            {
                subAssetAttempt = CreateSampleArmature("SubAssetAttemptArmature");
                TryAddGameObjectAsSubAsset(subAssetAttempt, container, containerPath);

                prefabAssetExtensionAttempt = CreateSampleArmature("AssetExtensionPrefabArmature");
                TrySavePrefabWithAssetExtension(prefabAssetExtensionAttempt, container, prefabWithAssetExtensionPath);

                prefabControlAttempt = CreateSampleArmature("ControlPrefabArmature");
                TrySaveControlPrefab(prefabControlAttempt, container, prefabControlPath);

                EditorUtility.SetDirty(container);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                LogContainerContents(containerPath);
                Selection.activeObject = container;
            }
            finally
            {
                DestroyIfSceneObject(subAssetAttempt);
                DestroyIfSceneObject(prefabAssetExtensionAttempt);
                DestroyIfSceneObject(prefabControlAttempt);
            }
        }

        private static void TryAddGameObjectAsSubAsset(
            GameObject armature,
            PrefabInAssetTestContainer container,
            string containerPath)
        {
            try
            {
                AssetDatabase.AddObjectToAsset(armature, container);
                container.m_AttemptedSubAssetReference = armature;
                Debug.Log($"[BlendShare Prefab Test] AddObjectToAsset(GameObject, .asset) did not throw. Container: {containerPath}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BlendShare Prefab Test] AddObjectToAsset(GameObject, .asset) failed: {exception.Message}");
            }
        }

        private static void TrySavePrefabWithAssetExtension(
            GameObject armature,
            PrefabInAssetTestContainer container,
            string prefabWithAssetExtensionPath)
        {
            try
            {
                var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    armature,
                    prefabWithAssetExtensionPath,
                    InteractionMode.AutomatedAction);

                container.m_PrefabReference = saved;
                Debug.Log(saved != null
                    ? $"[BlendShare Prefab Test] SaveAsPrefabAssetAndConnect succeeded with .asset extension: {prefabWithAssetExtensionPath}"
                    : $"[BlendShare Prefab Test] SaveAsPrefabAssetAndConnect returned null for .asset extension: {prefabWithAssetExtensionPath}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BlendShare Prefab Test] SaveAsPrefabAssetAndConnect(... .asset) failed: {exception.Message}");
            }
        }

        private static void TrySaveControlPrefab(
            GameObject armature,
            PrefabInAssetTestContainer container,
            string prefabControlPath)
        {
            try
            {
                var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    armature,
                    prefabControlPath,
                    InteractionMode.AutomatedAction);

                if (container.m_PrefabReference == null)
                {
                    container.m_PrefabReference = saved;
                }

                Debug.Log(saved != null
                    ? $"[BlendShare Prefab Test] Control .prefab save succeeded: {prefabControlPath}"
                    : $"[BlendShare Prefab Test] Control .prefab save returned null: {prefabControlPath}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BlendShare Prefab Test] Control .prefab save failed: {exception.Message}");
            }
        }

        private static GameObject CreateSampleArmature(string rootName)
        {
            var root = new GameObject(rootName);
            var hips = new GameObject("Hips");
            var spine = new GameObject("Spine");
            var jaw = new GameObject("Jaw");

            hips.transform.SetParent(root.transform, false);
            spine.transform.SetParent(hips.transform, false);
            jaw.transform.SetParent(spine.transform, false);

            hips.transform.localPosition = new Vector3(0f, 1f, 0f);
            spine.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            jaw.transform.localPosition = new Vector3(0f, 0.25f, 0.05f);
            jaw.transform.localRotation = Quaternion.Euler(5f, 0f, 0f);

            return root;
        }

        private static void LogContainerContents(string containerPath)
        {
            var contents = AssetDatabase.LoadAllAssetsAtPath(containerPath);
            string summary = string.Join(
                ", ",
                contents.Select(asset => asset == null ? "<null>" : $"{asset.GetType().Name}:{asset.name}"));

            Debug.Log($"[BlendShare Prefab Test] Container contents at {containerPath}: {summary}");
        }

        private static void EnsureOutputFolder()
        {
            if (AssetDatabase.IsValidFolder(OutputFolder))
            {
                return;
            }

            AssetDatabase.CreateFolder("Assets", "BlendSharePrefabInAssetTest");
        }

        private static void DestroyIfSceneObject(GameObject gameObject)
        {
            if (gameObject != null && !EditorUtility.IsPersistent(gameObject))
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
