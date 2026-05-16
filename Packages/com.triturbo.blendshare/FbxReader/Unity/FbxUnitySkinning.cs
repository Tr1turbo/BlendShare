using UnityEngine;

namespace Triturbo.Fbx.Unity
{
    public static class FbxUnitySkinning
    {
        public static Matrix4x4 ToUnityMatrix(FbxMatrix4x4 matrix)
        {
            var unityMatrix = new Matrix4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    unityMatrix[row, column] = (float)matrix[row, column];
                }
            }

            return unityMatrix;
        }

        public static float GetImportScale(GameObject FbxGo)
        {
            return FbxUnityAssetReader.GetImportScale(FbxGo);
        }
    }
}
