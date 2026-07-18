using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;

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

        internal static IReadOnlyList<BlendShareMesh> DeduplicateMeshComponents(
            IEnumerable<BlendShareMesh> components)
        {
            var ordered = (components ?? Enumerable.Empty<BlendShareMesh>())
                .Where(component => component != null)
                .ToArray();
            var winningPatches = DeduplicateByPatchId(ordered
                    .Where(component => component.Patch != null)
                    .Select(component => component.Patch))
                .ToArray();
            var patchSlots = winningPatches
                .Select((patch, index) => (patch, index))
                .ToDictionary(item => item.patch, item => item.index);

            // The later duplicate supplies the component data, while the first occurrence of
            // its logical patch ID retains the ordering slot used for feature collisions.
            var winningComponents = ordered
                .Where(component => component.Patch != null && patchSlots.ContainsKey(component.Patch))
                .GroupBy(component => (component.Patch, component.MeshData, component.TargetRenderer))
                .Select(group => group.Last())
                .ToHashSet();
            return ordered
                .Select((component, index) => (component, index))
                .Where(item => item.component.Patch == null || winningComponents.Contains(item.component))
                .OrderBy(item => item.component.Patch != null ? patchSlots[item.component.Patch] : int.MaxValue)
                .ThenBy(item => item.index)
                .Select(item => item.component)
                .ToArray();
        }

        private static string GetPatchId(BlendShareObject patch)
        {
            return patch?.m_PatchId ?? string.Empty;
        }
    }
}
