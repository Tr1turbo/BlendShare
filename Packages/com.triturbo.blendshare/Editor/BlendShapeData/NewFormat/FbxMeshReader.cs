using UnityEngine;

#if ENABLE_FBX_SDK
using System.Collections.Generic;
using UnityEditor;
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal sealed class FbxMeshSnapshot
    {
        public string MeshName { get; }
        public Vector3[] ControlPointPositions { get; }
        public BlendShapeRecord[] BlendShapes { get; }
        public float ImportScale { get; }
        public int ControlPointCount => ControlPointPositions.Length;

        public FbxMeshSnapshot(
            string meshName,
            Vector3[] controlPointPositions,
            BlendShapeRecord[] blendShapes,
            float importScale)
        {
            MeshName = meshName;
            ControlPointPositions = controlPointPositions ?? System.Array.Empty<Vector3>();
            BlendShapes = blendShapes ?? System.Array.Empty<BlendShapeRecord>();
            ImportScale = importScale;
        }
    }

#if ENABLE_FBX_SDK
    internal static class FbxMeshReader
    {
        public static bool TryReadMesh(GameObject fbxAsset, string meshPathOrName, out FbxMeshSnapshot snapshot)
        {
            snapshot = null;
            if (fbxAsset == null || string.IsNullOrEmpty(meshPathOrName))
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            using (var fbxManager = FbxManager.Create())
            using (var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT))
            {
                fbxManager.SetIOSettings(ios);

                int fileFormat;
                using (var registry = fbxManager.GetIOPluginRegistry())
                {
                    fileFormat = registry.FindWriterIDByDescription("FBX binary (*.fbx)");
                }

                using (var scene = FbxScene.Create(fbxManager, fbxAsset.name))
                {
                    using (var importer = FbxImporter.Create(fbxManager, ""))
                    {
                        if (!importer.Initialize(assetPath, fileFormat, ios))
                        {
                            return false;
                        }

                        importer.Import(scene);
                    }

                    FbxNode rootNode = scene.GetRootNode();
                    FbxNode node = FindMeshNode(rootNode, meshPathOrName) ?? FindMeshNodeByName(rootNode, meshPathOrName);
                    FbxMesh mesh = node?.GetMesh();
                    if (mesh == null)
                    {
                        return false;
                    }

                    snapshot = new FbxMeshSnapshot(
                        node.GetName(),
                        ReadControlPointPositions(mesh),
                        ReadBlendShapes(mesh),
                        GetImportScale(assetPath));
                    return true;
                }
            }
        }

        private static FbxNode FindMeshNode(FbxNode rootNode, string meshPath)
        {
            if (rootNode == null || string.IsNullOrEmpty(meshPath))
            {
                return null;
            }

            string[] parts = meshPath.Split('/');
            FbxNode current = rootNode;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                current = FindDirectChild(current, part);
                if (current == null)
                {
                    return null;
                }
            }

            return current.GetMesh() != null ? current : null;
        }

        private static FbxNode FindDirectChild(FbxNode node, string name)
        {
            if (node == null)
            {
                return null;
            }

            for (int i = 0; i < node.GetChildCount(); i++)
            {
                FbxNode child = node.GetChild(i);
                if (child.GetName() == name)
                {
                    return child;
                }
            }

            return null;
        }

        private static FbxNode FindMeshNodeByName(FbxNode node, string name)
        {
            if (node == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (node.GetName() == name && node.GetMesh() != null)
            {
                return node;
            }

            for (int i = 0; i < node.GetChildCount(); i++)
            {
                FbxNode result = FindMeshNodeByName(node.GetChild(i), name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static Vector3[] ReadControlPointPositions(FbxMesh mesh)
        {
            int controlPointCount = mesh.GetControlPointsCount();
            var positions = new Vector3[controlPointCount];
            for (int i = 0; i < controlPointCount; i++)
            {
                positions[i] = ToVector3(mesh.GetControlPointAt(i));
            }

            return positions;
        }

        private static BlendShapeRecord[] ReadBlendShapes(FbxMesh mesh)
        {
            var records = new List<BlendShapeRecord>();
            int deformerCount = mesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
            for (int deformerIndex = 0; deformerIndex < deformerCount; deformerIndex++)
            {
                var deformer = mesh.GetBlendShapeDeformer(deformerIndex);
                for (int channelIndex = 0; channelIndex < deformer.GetBlendShapeChannelCount(); channelIndex++)
                {
                    var channel = deformer.GetBlendShapeChannel(channelIndex);
                    if (channel == null || string.IsNullOrEmpty(channel.GetName()))
                    {
                        continue;
                    }

                    records.Add(new BlendShapeRecord(channel.GetName(), ReadBlendShapeData(channel, mesh)));
                }
            }

            return records.ToArray();
        }

        private static FbxBlendShapeData ReadBlendShapeData(FbxBlendShapeChannel channel, FbxMesh baseMesh)
        {
            int controlPointCount = baseMesh.GetControlPointsCount();
            int shapeCount = channel.GetTargetShapeCount();
            var frames = new FbxBlendShapeFrame[shapeCount];

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape shape = channel.GetTargetShape(shapeIndex);
                var frame = new FbxBlendShapeFrame();
                frames[shapeIndex] = frame;

                if (shape == null || shape.GetControlPointsCount() != controlPointCount)
                {
                    continue;
                }

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    FbxVector4 delta = shape.GetControlPointAt(pointIndex) - baseMesh.GetControlPointAt(pointIndex);
                    if (delta.X == 0.0 && delta.Y == 0.0 && delta.Z == 0.0 && delta.W == 0.0)
                    {
                        continue;
                    }

                    frame.AddDeltaControlPointAt(
                        new Vector4d(delta.X, delta.Y, delta.Z, delta.W),
                        pointIndex);
                }
            }

            return new FbxBlendShapeData(frames);
        }

        private static Vector3 ToVector3(FbxVector4 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        private static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
        }
    }
#endif
}
