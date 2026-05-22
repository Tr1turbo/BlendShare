using System;

namespace Triturbo.BlendShare.Fbx
{
    public static class FbxArrayUtility
    {
        public static Vector3d[] ToVector3dArray(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<Vector3d>();
            }

            var result = new Vector3d[values.Length / 3];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new Vector3d(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            }

            return result;
        }
    }
}
