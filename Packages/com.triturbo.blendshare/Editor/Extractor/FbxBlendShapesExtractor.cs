using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

using Triturbo.BlendShapeShare.BlendShapeData;



#if ENABLE_FBX_SDK
using Autodesk.Fbx;
namespace Triturbo.BlendShapeShare.Extractor
{
    public class FbxBlendShapeExtractor
    {
        public static GameObject ExtractFbxBlendshapes(
            ref List<MeshData> meshDataList, GameObject source, GameObject origin = null, string fbxPath = "")
        {
            if (meshDataList == null)
            {
                meshDataList = new List<MeshData>();
            }


            var fbxManager = FbxManager.Create();

            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);

            var sourceScene = FbxScene.Create(fbxManager, "Source");



            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return null;
            }


            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
            // -> 0



            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return null;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(source), pFileFormat, fbxManager.GetIOSettings()))
            {
                return null;
            }
            fbxImporter.Import(sourceScene);

            FbxScene originScene = null;
            if (origin != null)
            {
                originScene = FbxScene.Create(fbxManager, "Origin");
                if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Import origin FBX...", 0.2f))
                {
                    EditorUtility.ClearProgressBar();
                    return null;
                }
                if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(origin), pFileFormat, fbxManager.GetIOSettings()))
                {
                    return null;
                }

                fbxImporter.Import(originScene);
            }

            //


            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return null;
            }
            var sourceRootNode = sourceScene.GetRootNode();
            var originRootNode = originScene?.GetRootNode();



            foreach (var meshData in meshDataList)
            {
                FbxNode node = sourceRootNode.FindChild(meshData.m_MeshName, false);
                FbxMesh sourceMesh = node?.GetMesh();
                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                if (sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) < 1)
                {
                    continue;
                }

                var deformer = sourceMesh.GetBlendShapeDeformer(0);

                FbxNode nodeOrigin = originRootNode?.FindChild(meshData.m_MeshName, false);
                FbxMesh originMesh = nodeOrigin?.GetMesh();
                FbxBlendShape originDeformer = null;
                if (originMesh != null && originMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) > 0)
                {
                    originDeformer = originMesh.GetBlendShapeDeformer(0);
                }

                for (int i = 0; i < deformer.GetBlendShapeChannelCount(); i++)
                {
                    var channel = deformer.GetBlendShapeChannel(i);
                    string name = channel.GetName();
                    if (!meshData.ContainsBlendShape(name))
                    {
                        continue;
                    }

                    if (originDeformer != null)
                    {
                        originDeformer.AddBlendShapeChannel(CopyFbxBlendShapeChannel(channel, originMesh));
                    }
                    meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh));

                    //meshData.fbxBlendShapes.Add(data);
                }

            }

            var exporter = FbxExporter.Create(fbxManager, "");
            if (exporter.Initialize(fbxPath, pFileFormat, fbxManager.GetIOSettings()) == false)
            {
                Debug.LogError("Exporter Initialize failed.");
                return null;
            }
            exporter.Export(originScene);
            exporter.Destroy();

            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();



            return null;
        }

        public static FbxBlendShapeChannel CopyFbxBlendShapeChannel(FbxBlendShapeChannel source, FbxObject container)
        {
            // Create a new FbxBlendShapeChannel with the same name as the source
            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(container, source.GetName());

            // Iterate over each target shape in the source blend shape channel
            int shapeCount = source.GetTargetShapeCount();
            for (int shapeIndex = 0; shapeIndex < source.GetTargetShapeCount(); shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);

                int controlPointCount = sourceShape.GetControlPointsCount();


                FbxShape newShape = FbxShape.Create(container, sourceShape.GetName());
                newShape.InitControlPoints(controlPointCount);

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    newShape.SetControlPointAt(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex), pointIndex);
                }


                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }


        public static FbxBlendShapeData GetFbxBlendShapeData(FbxBlendShapeChannel source, FbxMesh sourceMesh)
        {
            int controlPointCount = sourceMesh.GetControlPointsCount();
            int shapeCount = source.GetTargetShapeCount();


            var frames = new FbxBlendShapeFrame[shapeCount];
            // Loop through each target shape in the blend shape channel
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);

                if (sourceShape.GetControlPointsCount() != controlPointCount)
                {
                    Debug.LogError("Control point count mismatch between the source mesh and target shape.");
                    return null;
                }

                frames[shapeIndex] = new FbxBlendShapeFrame();

                //var deltaControlPoints = new FbxBlendShapeData.Vector4d[controlPointCount];
                // Loop through each control point
                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var shapeControlPoint = sourceShape.GetControlPointAt(pointIndex);
                    var meshControlPoint = sourceMesh.GetControlPointAt(pointIndex);

                    var d = shapeControlPoint - meshControlPoint;


                    var delta = new Vector4d(d.X, d.Y, d.Z, d.W);

                    if (!delta.IsZero())
                        frames[shapeIndex].AddDeltaControlPointAt(delta, pointIndex);


                }


            }


            return new FbxBlendShapeData(frames);
        }
    }
}


#endif