using System.Collections.Generic;
using System.Linq;

namespace Triturbo.BlendShare.Core
{
    internal static class BlendSharePatchIdUtility
    {
        internal static IReadOnlyList<BlendShareObject> DeduplicateByPatchId(IEnumerable<BlendShareObject> patches)
        {
            var inputPatches = (patches ?? Enumerable.Empty<BlendShareObject>())
                .Where(patch => patch != null)
                .ToList();
            var result = new List<BlendShareObject>();
            var patchIdSlots = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var noPatchIdPatches = new HashSet<BlendShareObject>();
            foreach (var patch in inputPatches)
            {
                string patchId = GetPatchId(patch);
                if (string.IsNullOrEmpty(patchId))
                {
                    if (noPatchIdPatches.Add(patch))
                    {
                        result.Add(patch);
                    }

                    continue;
                }

                if (patchIdSlots.TryGetValue(patchId, out int slot))
                {
                    result[slot] = patch;
                    continue;
                }

                patchIdSlots.Add(patchId, result.Count);
                result.Add(patch);
            }

            return result;
        }

        internal static bool HasDuplicatePatchIds(IEnumerable<BlendShareObject> patches)
        {
            var seenPatchIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var patch in patches ?? Enumerable.Empty<BlendShareObject>())
            {
                string patchId = GetPatchId(patch);
                if (!string.IsNullOrEmpty(patchId) && !seenPatchIds.Add(patchId))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetPatchId(BlendShareObject patch)
        {
            return patch?.m_PatchId ?? string.Empty;
        }
    }
}
