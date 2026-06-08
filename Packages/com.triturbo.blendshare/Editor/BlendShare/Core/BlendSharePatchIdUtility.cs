using System.Collections.Generic;
using System.Linq;

namespace Triturbo.BlendShare.Core
{
    internal static class BlendSharePatchIdUtility
    {
        internal static IReadOnlyList<BlendShareObject> DeduplicateByPatchId(IEnumerable<BlendShareObject> blendShares)
        {
            var shares = (blendShares ?? Enumerable.Empty<BlendShareObject>())
                .Where(share => share != null)
                .ToList();
            var result = new List<BlendShareObject>();
            var patchIdSlots = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var noPatchIdShares = new HashSet<BlendShareObject>();
            foreach (var share in shares)
            {
                string patchId = GetPatchId(share);
                if (string.IsNullOrEmpty(patchId))
                {
                    if (noPatchIdShares.Add(share))
                    {
                        result.Add(share);
                    }

                    continue;
                }

                if (patchIdSlots.TryGetValue(patchId, out int slot))
                {
                    result[slot] = share;
                    continue;
                }

                patchIdSlots.Add(patchId, result.Count);
                result.Add(share);
            }

            return result;
        }

        internal static bool HasDuplicatePatchIds(IEnumerable<BlendShareObject> blendShares)
        {
            var seenPatchIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                string patchId = GetPatchId(share);
                if (!string.IsNullOrEmpty(patchId) && !seenPatchIds.Add(patchId))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetPatchId(BlendShareObject share)
        {
            return share?.m_PatchId ?? string.Empty;
        }
    }
}
