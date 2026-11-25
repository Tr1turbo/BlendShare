using System;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Identifies the element index space addressed by sparse buffers and vertex maps.
    /// </summary>
    public enum IndexDomain : byte
    {
        Unknown = 0,
        ControlPoint = 1,
        FaceCorner = 2,
        UnityVertex = 3
    }

    public static class IndexDomainUtility
    {
        public static bool IsDefinedValid(IndexDomain domain)
        {
            return domain == IndexDomain.ControlPoint ||
                   domain == IndexDomain.FaceCorner ||
                   domain == IndexDomain.UnityVertex;
        }

        public static void ThrowIfInvalid(IndexDomain domain, string paramName)
        {
            if (!IsDefinedValid(domain))
            {
                throw new ArgumentException($"Invalid or undefined IndexDomain value '{domain}' ({(byte)domain}).", paramName);
            }
        }
    }
}
