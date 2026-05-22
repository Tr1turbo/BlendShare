using System.Collections.Generic;

namespace Triturbo.Fbx
{
    public static class FbxDocumentReader
    {
        public static FbxReadResult<FbxDocument> Read(string assetPath, IEnumerable<string> nodePaths = null)
        {
            return UfbxDocumentReader.Read(assetPath, nodePaths);
        }

        public static FbxDocument ReadOrThrow(string assetPath, IEnumerable<string> nodePaths = null)
        {
            var result = Read(assetPath, nodePaths);
            if (!result.Success)
            {
                throw new FbxReadException(result.Status, result.Message, result.Diagnostics);
            }

            return result.Value;
        }
    }
}
