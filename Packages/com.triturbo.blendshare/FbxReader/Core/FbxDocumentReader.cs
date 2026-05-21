using System.Collections.Generic;
using System.Linq;

namespace Triturbo.Fbx
{
    public static class FbxDocumentReader
    {
        public static FbxReadResult<FbxDocument> Read(string assetPath, IEnumerable<string> nodePaths = null)
        {
            var ufbxResult = UfbxDocumentReader.Read(assetPath, nodePaths);
            if (ufbxResult.Success)
            {
                return ufbxResult;
            }

            var binaryResult = BinaryFbxDocumentReader.Read(assetPath, nodePaths);
            if (!binaryResult.Success)
            {
                return binaryResult;
            }

            var diagnostics = binaryResult.Diagnostics.Concat(new[]
            {
                new FbxDiagnostic(
                    FbxDiagnosticSeverity.Warning,
                    ufbxResult.Status,
                    string.IsNullOrEmpty(ufbxResult.Message)
                        ? "ufbx reader failed; fell back to binary FBX reader."
                        : "ufbx reader failed; fell back to binary FBX reader: " + ufbxResult.Message)
            });
            return FbxReadResult<FbxDocument>.Succeeded(binaryResult.Value, diagnostics);
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
