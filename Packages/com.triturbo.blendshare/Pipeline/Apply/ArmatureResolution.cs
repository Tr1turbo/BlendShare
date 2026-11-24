using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public static class ArmatureBindTransformMath
    {
        public static bool ComputeCanonicalBindPose(FbxMatrix4x4 transformMatrix, FbxMatrix4x4 transformLinkMatrix,
            out FbxMatrix4x4 canonicalBindPose)
        {
            canonicalBindPose = default;
            if (!FbxAffineSpaceTransform.TryCreate(transformLinkMatrix, out var link) ||
                !FbxAffineSpaceTransform.TryCreate(transformMatrix, out _)) return false;
            canonicalBindPose = transformMatrix * link.InverseOffset;
            return FbxAffineSpaceTransform.TryCreate(canonicalBindPose, out _);
        }

        public static bool ConvertCanonicalBindPoseToUnity(FbxMatrix4x4 canonicalBindPose,
            FbxMatrix4x4 canonicalToUnityMeshLocal, out Matrix4x4 unityBindPose) =>
            ConvertCanonicalMatrixToUnity(canonicalBindPose, canonicalToUnityMeshLocal, out unityBindPose);

        public static bool ConvertCanonicalMatrixToUnity(FbxMatrix4x4 canonicalMatrix,
            FbxMatrix4x4 canonicalToUnityMeshLocal, out Matrix4x4 unityMatrix)
        {
            unityMatrix = default;
            if (!FbxAffineSpaceTransform.TryCreate(canonicalToUnityMeshLocal, out var space)) return false;
            unityMatrix = (space.InverseOffset * canonicalMatrix * space.Offset).ToUnityColumnMatrix();
            return IsFinite(unityMatrix);
        }

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i])) return false;
            return true;
        }

        public static bool TryDecomposeUnityLocalTransform(Matrix4x4 localMatrix, out Vector3 position,
            out Quaternion rotation, out Vector3 scale, out string diagnostic)
        {
            position = localMatrix.GetColumn(3);
            rotation = Quaternion.identity;
            scale = default;
            diagnostic = "Non-finite or perspective local transform.";
            if (!IsFinite(localMatrix) || Mathf.Abs(localMatrix.m30) > 1e-5f || Mathf.Abs(localMatrix.m31) > 1e-5f ||
                Mathf.Abs(localMatrix.m32) > 1e-5f || Mathf.Abs(localMatrix.m33 - 1f) > 1e-5f) return false;

            Vector3 x = localMatrix.GetColumn(0), y = localMatrix.GetColumn(1), z = localMatrix.GetColumn(2);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            diagnostic = "Singular or unrepresentable local scale.";
            if (scale.x == 0 || scale.y == 0 || scale.z == 0 || float.IsInfinity(scale.x) ||
                float.IsInfinity(scale.y) || float.IsInfinity(scale.z)) return false;
            x /= scale.x;
            y /= scale.y;
            z /= scale.z;
            diagnostic = "Sheared local transform cannot be represented by Unity TRS.";
            if (Mathf.Abs(Vector3.Dot(x, y)) > 1e-5f || Mathf.Abs(Vector3.Dot(x, z)) > 1e-5f ||
                Mathf.Abs(Vector3.Dot(y, z)) > 1e-5f) return false;
            if (Vector3.Dot(x, Vector3.Cross(y, z)) < 0) scale.x = -scale.x;
            rotation = Quaternion.LookRotation(z, y);
            diagnostic = null;
            return true;
        }
    }

    public static class ArmatureBoneResolver
    {
        public static bool ResolveBoneTransform(Transform targetRoot, IReadOnlyDictionary<BoneOverrideKey, Transform> overrides,
            string patchId, string armatureId, string sourceBonePath, out Transform resolvedTransform,
            out string resolvedTargetPath, out bool isNewBone)
        {
            var path = FbxNameUtility.NormalizePath(sourceBonePath);
            isNewBone = false;
            resolvedTransform = null;
            if (overrides != null) overrides.TryGetValue(new BoneOverrideKey(patchId, armatureId, path), out resolvedTransform);
            if (!resolvedTransform && targetRoot && path.Length > 0) resolvedTransform = targetRoot.Find(path);
            resolvedTargetPath = resolvedTransform ? AnimationUtility.CalculateTransformPath(resolvedTransform, targetRoot) : path;
            return resolvedTransform;
        }

        public static bool EnsureBoneHierarchy(Transform targetRoot, ArmatureDocument armature, string sourceBonePath,
            FbxMatrix4x4 canonicalToUnityMeshLocal, out Transform createdTransform, out string diagnostic)
        {
            createdTransform = null;
            diagnostic = "Missing root, bone path or armature record.";
            var path = FbxNameUtility.NormalizePath(sourceBonePath);
            if (!targetRoot || path.Length == 0) return false;
            var pending = new Stack<(string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale)>();
            Transform parent = null;
            while (path.Length > 0 && !(parent = targetRoot.Find(path)))
            {
                var record = armature?.Bones?.Find(b => FbxNameUtility.NormalizePath(b.Path) == path);
                if (record == null || !record.CreateIfMissing)
                {
                    diagnostic = record == null
                        ? $"Missing armature record for bone '{path}'."
                        : $"Bone '{path}' is marked CreateIfMissing = false and does not exist in target hierarchy.";
                    return false;
                }
                diagnostic = "Invalid local bind matrix for " + path;
                if (!ArmatureBindTransformMath.ConvertCanonicalMatrixToUnity(record.LocalBindMatrix, canonicalToUnityMeshLocal, out var local) ||
                    !ArmatureBindTransformMath.TryDecomposeUnityLocalTransform(local, out var position, out var rotation, out var scale, out diagnostic))
                    return false;
                int slash = path.LastIndexOf('/');
                pending.Push((path.Substring(slash + 1), position, rotation, scale));
                path = slash < 0 ? "" : path.Substring(0, slash);
            }
            if (!parent) parent = targetRoot;
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                var child = new GameObject(item.Name).transform;
                child.SetParent(parent, false);
                child.localPosition = item.Position;
                child.localRotation = item.Rotation;
                child.localScale = item.Scale;
                parent = child;
            }
            createdTransform = parent;
            diagnostic = null;
            return true;
        }
    }
}
