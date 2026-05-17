#if ENABLE_FBX_SDK
using System.Collections.Generic;
using Triturbo.Fbx;
using ReaderBlendShapeChannel = Triturbo.Fbx.FbxBlendShapeChannel;
using ReaderBlendShapeDeformer = Triturbo.Fbx.FbxBlendShapeDeformer;
using ReaderShapeFrame = Triturbo.Fbx.FbxShapeFrame;
using SdkDeformer = Autodesk.Fbx.FbxDeformer;
using SdkMesh = Autodesk.Fbx.FbxMesh;
using SdkNode = Autodesk.Fbx.FbxNode;
using SdkShape = Autodesk.Fbx.FbxShape;
using SdkVector4 = Autodesk.Fbx.FbxVector4;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Copies raw FBX SDK mesh data into the managed FBX reader geometry model.
    /// </summary>
    internal static class FbxSdkMeshGeometryAdapter
    {
        public static FbxMeshGeometry Create(
            SdkNode rootNode,
            SdkNode meshNode)
        {
            var sdkMesh = meshNode?.GetMesh();
            if (sdkMesh == null)
            {
                return null;
            }

            int controlPointCount = sdkMesh.GetControlPointsCount();
            var controlPoints = ReadControlPoints(sdkMesh, controlPointCount);
            var deformers = new List<FbxDeformer>();
            deformers.AddRange(ReadBlendShapeDeformers(sdkMesh, controlPointCount));

            var ownerNode = new FbxSceneNode(
                0,
                meshNode.GetName(),
                BuildNodePath(rootNode, meshNode),
                FbxSceneNodeType.Mesh,
                ToTransform(meshNode));

            var mesh = new FbxMeshGeometry(
                0,
                sdkMesh.GetName(),
                ownerNode,
                controlPointCount,
                controlPoints,
                System.Array.Empty<Vector3d>(),
                System.Array.Empty<Vector3d>(),
                deformers);

            foreach (var deformer in deformers)
            {
                deformer.OwnerMesh = mesh;
            }

            ownerNode.SetAttribute(mesh);
            return mesh;
        }

        private static Vector3d[] ReadControlPoints(SdkMesh mesh, int controlPointCount)
        {
            var controlPoints = new Vector3d[controlPointCount];
            for (int i = 0; i < controlPointCount; i++)
            {
                controlPoints[i] = ToVector3d(mesh.GetControlPointAt(i));
            }

            return controlPoints;
        }

        private static IEnumerable<ReaderBlendShapeDeformer> ReadBlendShapeDeformers(
            SdkMesh mesh,
            int controlPointCount)
        {
            int deformerCount = mesh.GetDeformerCount(SdkDeformer.EDeformerType.eBlendShape);
            for (int deformerIndex = 0; deformerIndex < deformerCount; deformerIndex++)
            {
                var sdkDeformer = mesh.GetBlendShapeDeformer(deformerIndex);
                var channels = new List<ReaderBlendShapeChannel>();

                for (int channelIndex = 0; channelIndex < sdkDeformer.GetBlendShapeChannelCount(); channelIndex++)
                {
                    var sdkChannel = sdkDeformer.GetBlendShapeChannel(channelIndex);
                    var frames = new List<ReaderShapeFrame>();
                    int shapeCount = sdkChannel.GetTargetShapeCount();

                    for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
                    {
                        frames.Add(ReadShapeFrame(
                            sdkChannel.GetTargetShape(shapeIndex),
                            mesh,
                            controlPointCount,
                            100d * (shapeIndex + 1) / System.Math.Max(1, shapeCount)));
                    }

                    channels.Add(new ReaderBlendShapeChannel(channelIndex, sdkChannel.GetName(), frames));
                }

                yield return new ReaderBlendShapeDeformer(deformerIndex, sdkDeformer.GetName(), channels);
            }
        }

        private static ReaderShapeFrame ReadShapeFrame(
            SdkShape shape,
            SdkMesh baseMesh,
            int controlPointCount,
            double frameWeight)
        {
            var indices = new List<int>();
            var deltas = new List<Vector3d>();
            int count = System.Math.Min(controlPointCount, shape.GetControlPointsCount());

            for (int i = 0; i < count; i++)
            {
                var delta = ToVector3d(shape.GetControlPointAt(i) - baseMesh.GetControlPointAt(i));
                if (delta.IsZero())
                {
                    continue;
                }

                indices.Add(i);
                deltas.Add(delta);
            }

            return new ReaderShapeFrame(frameWeight, indices, deltas);
        }

        private static FbxTransform ToTransform(SdkNode node)
        {
            if (node == null)
            {
                return FbxTransform.Identity;
            }

            var matrix = node.EvaluateLocalTransform();
            return new FbxTransform(
                ToVector3d(matrix.GetT()),
                ToVector3d(matrix.GetR()),
                ToVector3d(matrix.GetS()));
        }

        private static Vector3d ToVector3d(SdkVector4 value)
        {
            return new Vector3d(value.X, value.Y, value.Z);
        }

        private static string BuildNodePath(SdkNode rootNode, SdkNode node)
        {
            if (node == null || node == rootNode)
            {
                return MeshNodePath.Root;
            }

            var names = new Stack<string>();
            var current = node;
            while (current != null && current != rootNode)
            {
                string name = current.GetName();
                if (!string.IsNullOrEmpty(name))
                {
                    names.Push(name);
                }

                current = current.GetParent();
            }

            return MeshNodePath.Normalize(string.Join("/", names));
        }
    }
}
#endif
