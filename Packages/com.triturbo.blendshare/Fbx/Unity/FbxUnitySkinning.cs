using UnityEngine;

namespace Triturbo.BlendShare.Fbx.Unity
{
    public static class FbxUnitySkinning
    {
        public static Matrix4x4 ToUnityMatrix(FbxMatrix4x4 matrix, float fbxToUnityScale)
        {
            fbxToUnityScale = fbxToUnityScale == 0f ? 1f : fbxToUnityScale;

            // FbxMatrix4x4 stores ufbx/FBX matrices as row-vector transforms:
            //     [ r00 r01 r02 0 ]
            //     [ r10 r11 r12 0 ]
            //     [ r20 r21 r22 0 ]
            //     [ tx  ty  tz  1 ]
            // Translation is therefore m30/m31/m32 and points are transformed as v * M.
            // Unity Matrix4x4 is used as a column-vector transform:
            //     [ r00 r10 r20 tx ]
            //     [ r01 r11 r21 ty ]
            //     [ r02 r12 r22 tz ]
            //     [ 0   0   0   1  ]
            // Translation is m03/m13/m23 and points are transformed as M * v.
            // So conversion is a transpose, plus source FBX length units -> Unity units
            // for translation only. Rotation/scale/shear terms are unitless and must not
            // be multiplied by the import scale.
            var unityMatrix = new Matrix4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    unityMatrix[row, column] = (float)matrix[column, row];
                }
            }

            unityMatrix.m03 *= fbxToUnityScale;
            unityMatrix.m13 *= fbxToUnityScale;
            unityMatrix.m23 *= fbxToUnityScale;
            return unityMatrix;
        }

        public static float GetImportScale(GameObject FbxGo)
        {
            return FbxUnityAssetReader.GetImportScale(FbxGo);
        }
    }
}
