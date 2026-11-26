using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Evaluates FBX node transform stacks (Euler orders, <c>PreRotation</c>, <c>PostRotation</c>,
    /// rotation/scaling pivots and offsets, and <c>RSrs</c>/<c>RrSs</c>/<c>Rrs</c> scale inheritance)
    /// and solves local TRS channels in pure binary64 arithmetic without SDK dependencies.
    /// </summary>
    public static class FbxNodeTransformEvaluator
    {
        private const double k_ScaleEpsilon = 1e-10d;
        private const double k_OrthoTolerance = 1e-4d;
        private const double k_GimbalSinClip = 1d - 1e-12d;
        private const double k_EulerRoundTripTolerance = 1e-5d;

        /// <summary>
        /// Evaluated world-space hierarchy state for an FBX node, tracking separate unscaled basis
        /// and inherited scale factors required by <see cref="FbxTransformInheritMode.RrSs"/> and
        /// <see cref="FbxTransformInheritMode.Rrs"/> (<c>ufbx.c:23078-23110</c>).
        /// </summary>
        public readonly struct FbxHierarchyNodeState
        {
            public static readonly FbxHierarchyNodeState Identity =
                new FbxHierarchyNodeState(FbxMatrix4x4.Identity, FbxMatrix4x4.Identity, Vector3d.one, null);

            public readonly FbxMatrix4x4 NodeToWorld;
            public readonly FbxMatrix4x4 UnscaledNodeToWorld;
            public readonly Vector3d InheritScale;
            public readonly Vector3d? InheritedScaleNodeScale;

            public FbxHierarchyNodeState(
                FbxMatrix4x4 nodeToWorld,
                FbxMatrix4x4 unscaledNodeToWorld,
                Vector3d inheritScale,
                Vector3d? inheritedScaleNodeScale)
            {
                NodeToWorld = nodeToWorld;
                UnscaledNodeToWorld = unscaledNodeToWorld;
                InheritScale = inheritScale;
                InheritedScaleNodeScale = inheritedScaleNodeScale;
            }
        }

        /// <summary>
        /// Evaluates the row-vector local matrix (<c>node_to_parent</c>) of <paramref name="raw"/>:
        /// <c>T(-Sp) * S * T(Sp + Soff - Rp) * R_total * T(Rp + Roff + T_lcl)</c>, where
        /// <c>R_total = R_post^-1 * R_lcl * R_pre</c> when <see cref="FbxNodeRawTransform.RotationActive"/> is true.
        /// </summary>
        public static FbxMatrix4x4 EvaluateLocalMatrix(in FbxNodeRawTransform raw)
        {
            FbxMatrix4x4 rTotal = EvaluateTotalRotationMatrix(raw);
            Vector3d sp = raw.ScalingPivot;
            Vector3d soff = raw.ScalingOffset;
            Vector3d rp = raw.RotationPivot;
            Vector3d roff = raw.RotationOffset;
            Vector3d s = raw.LclScaling;

            return FbxMatrix4x4.Translate(sp * -1d) *
                   FbxMatrix4x4.Scale(s) *
                   FbxMatrix4x4.Translate(sp + soff - rp) *
                   rTotal *
                   FbxMatrix4x4.Translate(rp + roff + raw.LclTranslation);
        }

        /// <summary>
        /// Evaluates the composite local rotation matrix <c>R_total</c> of <paramref name="raw"/>.
        /// </summary>
        public static FbxMatrix4x4 EvaluateTotalRotationMatrix(in FbxNodeRawTransform raw)
        {
            FbxRotationOrder order = raw.RotationActive && raw.RotationOrder != FbxRotationOrder.SphericXYZ
                ? raw.RotationOrder
                : FbxRotationOrder.XYZ;
            FbxMatrix4x4 rLcl = FbxMatrix4x4.RotateEulerDegrees(raw.LclRotation, order);
            if (!raw.RotationActive)
            {
                return rLcl;
            }

            FbxMatrix4x4 rPre = FbxMatrix4x4.RotateEulerDegrees(raw.PreRotation, FbxRotationOrder.XYZ);
            FbxMatrix4x4 rPost = FbxMatrix4x4.RotateEulerDegrees(raw.PostRotation, FbxRotationOrder.XYZ);
            return rPost.Transpose() * rLcl * rPre;
        }

        /// <summary>
        /// Evaluates a child node's <see cref="FbxHierarchyNodeState"/> from its local matrix, scale, and inherit mode
        /// relative to <paramref name="parentState"/>.
        /// </summary>
        public static bool TryEvaluateHierarchyState(
            FbxMatrix4x4 localMatrix,
            Vector3d localScale,
            FbxTransformInheritMode inheritMode,
            FbxTransformInheritMode originalInheritMode,
            bool hasParent,
            in FbxHierarchyNodeState parentState,
            out FbxHierarchyNodeState state)
        {
            state = FbxHierarchyNodeState.Identity;
            if (Math.Abs(localScale.x) <= k_ScaleEpsilon ||
                Math.Abs(localScale.y) <= k_ScaleEpsilon ||
                Math.Abs(localScale.z) <= k_ScaleEpsilon)
            {
                return false;
            }

            var u0 = new Vector3d(localMatrix[0, 0] / localScale.x, localMatrix[0, 1] / localScale.x, localMatrix[0, 2] / localScale.x);
            var u1 = new Vector3d(localMatrix[1, 0] / localScale.y, localMatrix[1, 1] / localScale.y, localMatrix[1, 2] / localScale.y);
            var u2 = new Vector3d(localMatrix[2, 0] / localScale.z, localMatrix[2, 1] / localScale.z, localMatrix[2, 2] / localScale.z);
            Vector3d localTrans = localMatrix.Translation;

            var unscaledLocal = new FbxMatrix4x4(
                u0.x, u0.y, u0.z, 0d,
                u1.x, u1.y, u1.z, 0d,
                u2.x, u2.y, u2.z, 0d,
                localTrans.x, localTrans.y, localTrans.z, 1d);

            if (!hasParent)
            {
                state = new FbxHierarchyNodeState(localMatrix, unscaledLocal, localScale, inheritedScaleNodeScale: null);
                return true;
            }

            if (inheritMode == FbxTransformInheritMode.RSrs)
            {
                state = new FbxHierarchyNodeState(
                    localMatrix * parentState.NodeToWorld,
                    unscaledLocal * parentState.NodeToWorld,
                    localScale,
                    inheritedScaleNodeScale: null);
                return true;
            }

            Vector3d? inheritedNodeScale = originalInheritMode == FbxTransformInheritMode.RrSs
                ? (Vector3d?)parentState.InheritScale
                : parentState.InheritedScaleNodeScale;
            Vector3d parentScaleFactor = inheritedNodeScale ?? Vector3d.one;
            Vector3d effectiveScale = new Vector3d(
                localScale.x * parentScaleFactor.x,
                localScale.y * parentScaleFactor.y,
                localScale.z * parentScaleFactor.z);
            Vector3d scaledTrans = new Vector3d(
                localTrans.x * parentState.InheritScale.x,
                localTrans.y * parentState.InheritScale.y,
                localTrans.z * parentState.InheritScale.z);

            var nodeToUnscaledParent = new FbxMatrix4x4(
                u0.x * effectiveScale.x, u0.y * effectiveScale.x, u0.z * effectiveScale.x, 0d,
                u1.x * effectiveScale.y, u1.y * effectiveScale.y, u1.z * effectiveScale.y, 0d,
                u2.x * effectiveScale.z, u2.y * effectiveScale.z, u2.z * effectiveScale.z, 0d,
                scaledTrans.x, scaledTrans.y, scaledTrans.z, 1d);
            var unscaledNodeToUnscaledParent = new FbxMatrix4x4(
                u0.x, u0.y, u0.z, 0d,
                u1.x, u1.y, u1.z, 0d,
                u2.x, u2.y, u2.z, 0d,
                scaledTrans.x, scaledTrans.y, scaledTrans.z, 1d);

            state = new FbxHierarchyNodeState(
                nodeToUnscaledParent * parentState.UnscaledNodeToWorld,
                unscaledNodeToUnscaledParent * parentState.UnscaledNodeToWorld,
                effectiveScale,
                inheritedNodeScale);
            return true;
        }

        /// <summary>
        /// Builds an <see cref="FbxHierarchyNodeState"/> from a known global matrix <paramref name="globalMatrix"/>
        /// when propagating rest states from a skin-seeded parent node.
        /// </summary>
        public static bool TryCreateStateFromGlobalMatrix(
            FbxMatrix4x4 globalMatrix,
            out FbxHierarchyNodeState state)
        {
            state = FbxHierarchyNodeState.Identity;
            if (!TryDecomposeShearFreeTrs(globalMatrix, preferredScaleSigns: Vector3d.one, out var trans, out var rotMatrix, out var signedScale, out _))
            {
                if (!FbxAffineSpaceTransform.IsInvertibleAffine(globalMatrix))
                {
                    return false;
                }

                // Skewed bind matrix fallback: RSrs children only consume NodeToWorld.
                state = new FbxHierarchyNodeState(globalMatrix, globalMatrix, Vector3d.one, inheritedScaleNodeScale: null);
                return true;
            }

            var unscaledGlobal = new FbxMatrix4x4(
                rotMatrix[0, 0], rotMatrix[0, 1], rotMatrix[0, 2], 0d,
                rotMatrix[1, 0], rotMatrix[1, 1], rotMatrix[1, 2], 0d,
                rotMatrix[2, 0], rotMatrix[2, 1], rotMatrix[2, 2], 0d,
                trans.x, trans.y, trans.z, 1d);
            state = new FbxHierarchyNodeState(globalMatrix, unscaledGlobal, signedScale, inheritedScaleNodeScale: null);
            return true;
        }

        /// <summary>
        /// Solves <c>(LclTranslation, LclRotation, LclScaling)</c> to realize <paramref name="desiredGlobalMatrix"/>
        /// while preserving <paramref name="preservedStack"/>'s <c>PreRotation</c>, <c>PostRotation</c>,
        /// <c>RotationOrder</c>, <c>RotationActive</c>, pivots/offsets, and <c>InheritMode</c>.
        /// Returns false if <paramref name="desiredGlobalMatrix"/> induces shear that cannot be represented by TRS channels.
        /// </summary>
        public static bool TrySolveNodeChannels(
            FbxMatrix4x4 desiredGlobalMatrix,
            bool hasParent,
            in FbxHierarchyNodeState parentState,
            in FbxNodeRawTransform preservedStack,
            out FbxNodeRawTransform solvedRaw,
            out FbxMatrix4x4 solvedLocalMatrix,
            out FbxHierarchyNodeState solvedState,
            out string error,
            string nodeLabel = null)
        {
            solvedRaw = preservedStack;
            solvedLocalMatrix = FbxMatrix4x4.Identity;
            solvedState = FbxHierarchyNodeState.Identity;
            error = null;
            string label = string.IsNullOrEmpty(nodeLabel) ? "<unknown>" : nodeLabel;

            FbxRotationOrder effectiveOrder = preservedStack.RotationOrder == FbxRotationOrder.SphericXYZ
                ? FbxRotationOrder.XYZ
                : preservedStack.RotationOrder;

            Vector3d preferredSigns = new Vector3d(
                preservedStack.LclScaling.x < 0d ? -1d : 1d,
                preservedStack.LclScaling.y < 0d ? -1d : 1d,
                preservedStack.LclScaling.z < 0d ? -1d : 1d);

            Vector3d localEvalTrans;
            FbxMatrix4x4 rTotal;
            Vector3d lclScale;

            if (!hasParent)
            {
                if (!TryDecomposeShearFreeTrs(desiredGlobalMatrix, preferredSigns, out localEvalTrans, out rTotal, out lclScale, out error, label))
                {
                    return false;
                }
            }
            else if (preservedStack.InheritMode == FbxTransformInheritMode.RSrs)
            {
                if (!parentState.NodeToWorld.TryInverse(out var parentInv))
                {
                    error = $"Parent world matrix for FBX node '{label}' is singular.";
                    return false;
                }

                FbxMatrix4x4 desiredLocal = desiredGlobalMatrix * parentInv;
                if (!TryDecomposeShearFreeTrs(desiredLocal, preferredSigns, out localEvalTrans, out rTotal, out lclScale, out error, label))
                {
                    return false;
                }
            }
            else
            {
                if (!parentState.UnscaledNodeToWorld.TryInverse(out var unscaledParentInv))
                {
                    error = $"Parent unscaled world matrix for FBX node '{label}' is singular.";
                    return false;
                }

                Vector3d? inheritedNodeScale = preservedStack.OriginalInheritMode == FbxTransformInheritMode.RrSs
                    ? (Vector3d?)parentState.InheritScale
                    : parentState.InheritedScaleNodeScale;
                Vector3d parentScaleFactor = inheritedNodeScale ?? Vector3d.one;
                if (Math.Abs(parentScaleFactor.x) <= k_ScaleEpsilon ||
                    Math.Abs(parentScaleFactor.y) <= k_ScaleEpsilon ||
                    Math.Abs(parentScaleFactor.z) <= k_ScaleEpsilon ||
                    Math.Abs(parentState.InheritScale.x) <= k_ScaleEpsilon ||
                    Math.Abs(parentState.InheritScale.y) <= k_ScaleEpsilon ||
                    Math.Abs(parentState.InheritScale.z) <= k_ScaleEpsilon)
                {
                    error = $"Parent inherited scale for FBX node '{label}' is near-zero.";
                    return false;
                }

                Vector3d effectivePreferredSigns = new Vector3d(
                    preferredSigns.x * (parentScaleFactor.x < 0d ? -1d : 1d),
                    preferredSigns.y * (parentScaleFactor.y < 0d ? -1d : 1d),
                    preferredSigns.z * (parentScaleFactor.z < 0d ? -1d : 1d));

                FbxMatrix4x4 nodeToUnscaledParent = desiredGlobalMatrix * unscaledParentInv;
                if (!TryDecomposeShearFreeTrs(nodeToUnscaledParent, effectivePreferredSigns, out var scaledTrans, out rTotal, out var effectiveScale, out error, label))
                {
                    return false;
                }

                lclScale = new Vector3d(
                    effectiveScale.x / parentScaleFactor.x,
                    effectiveScale.y / parentScaleFactor.y,
                    effectiveScale.z / parentScaleFactor.z);
                localEvalTrans = new Vector3d(
                    scaledTrans.x / parentState.InheritScale.x,
                    scaledTrans.y / parentState.InheritScale.y,
                    scaledTrans.z / parentState.InheritScale.z);
            }

            // R_total = R_post^T * R_lcl * R_pre  =>  R_lcl = R_post * R_total * R_pre^T
            FbxMatrix4x4 rLclMatrix;
            FbxRotationOrder decomposeOrder;
            if (preservedStack.RotationActive)
            {
                FbxMatrix4x4 rPre = FbxMatrix4x4.RotateEulerDegrees(preservedStack.PreRotation, FbxRotationOrder.XYZ);
                FbxMatrix4x4 rPost = FbxMatrix4x4.RotateEulerDegrees(preservedStack.PostRotation, FbxRotationOrder.XYZ);
                rLclMatrix = rPost * rTotal * rPre.Transpose();
                decomposeOrder = effectiveOrder;
            }
            else
            {
                rLclMatrix = rTotal;
                decomposeOrder = FbxRotationOrder.XYZ;
            }

            if (!TryDecomposeEulerDegrees(rLclMatrix, decomposeOrder, out var lclRotation))
            {
                error = $"Failed decomposing Euler angles ({decomposeOrder}) for FBX node '{label}'.";
                return false;
            }

            // Trans(M_local) = ((-Sp * S) + Sp + Soff - Rp) * R_total + Rp + Roff + LclTranslation
            Vector3d sp = preservedStack.ScalingPivot;
            Vector3d soff = preservedStack.ScalingOffset;
            Vector3d rp = preservedStack.RotationPivot;
            Vector3d roff = preservedStack.RotationOffset;
            var scaledNegSp = new Vector3d(-sp.x * lclScale.x, -sp.y * lclScale.y, -sp.z * lclScale.z);
            Vector3d pivotContrib = rTotal.TransformVector(scaledNegSp + sp + soff - rp) + rp + roff;
            Vector3d lclTranslation = localEvalTrans - pivotContrib;

            solvedRaw = new FbxNodeRawTransform(
                lclTranslation,
                lclRotation,
                lclScale,
                effectiveOrder,
                preservedStack.InheritMode,
                preservedStack.OriginalInheritMode,
                preservedStack.RotationActive,
                preservedStack.PreRotation,
                preservedStack.PostRotation,
                rp,
                sp,
                roff,
                soff);

            solvedLocalMatrix = EvaluateLocalMatrix(solvedRaw);
            if (!TryEvaluateHierarchyState(
                    solvedLocalMatrix,
                    solvedRaw.LclScaling,
                    solvedRaw.InheritMode,
                    solvedRaw.OriginalInheritMode,
                    hasParent,
                    parentState,
                    out solvedState))
            {
                error = $"Solved FBX hierarchy state for node '{label}' is degenerate.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Decomposes a shear-free row-vector affine matrix <c>S * R * T</c> into translation,
        /// proper orthogonal <c>SO(3)</c> rotation matrix, and signed scale.
        /// </summary>
        public static bool TryDecomposeShearFreeTrs(
            FbxMatrix4x4 matrix,
            Vector3d preferredScaleSigns,
            out Vector3d translation,
            out FbxMatrix4x4 rotationMatrix,
            out Vector3d signedScale,
            out string error,
            string nodeLabel = null)
        {
            translation = Vector3d.zero;
            rotationMatrix = FbxMatrix4x4.Identity;
            signedScale = Vector3d.one;
            error = null;
            string label = string.IsNullOrEmpty(nodeLabel) ? "<unknown>" : nodeLabel;

            if (!FbxAffineSpaceTransform.IsInvertibleAffine(matrix))
            {
                error = $"Matrix for FBX node '{label}' is singular, projective, or non-finite.";
                return false;
            }

            var r0 = new Vector3d(matrix[0, 0], matrix[0, 1], matrix[0, 2]);
            var r1 = new Vector3d(matrix[1, 0], matrix[1, 1], matrix[1, 2]);
            var r2 = new Vector3d(matrix[2, 0], matrix[2, 1], matrix[2, 2]);
            double m0 = r0.magnitude;
            double m1 = r1.magnitude;
            double m2 = r2.magnitude;
            if (m0 <= k_ScaleEpsilon || m1 <= k_ScaleEpsilon || m2 <= k_ScaleEpsilon)
            {
                error = $"Matrix for FBX node '{label}' has near-zero axis scale.";
                return false;
            }

            double s0 = preferredScaleSigns.x < 0d ? -m0 : m0;
            double s1 = preferredScaleSigns.y < 0d ? -m1 : m1;
            double s2 = preferredScaleSigns.z < 0d ? -m2 : m2;

            double det = FbxLinearAlgebra3d.Determinant3x3(matrix);
            if ((s0 * s1 * s2) * det < 0d)
            {
                // Flip one sign so R = S^-1 * Lin(M) has det(R) = +1 (SO(3)).
                s0 = -s0;
            }

            var u0 = r0 / s0;
            var u1 = r1 / s1;
            var u2 = r2 / s2;
            if (Math.Abs(Vector3d.Dot(u0, u1)) > k_OrthoTolerance ||
                Math.Abs(Vector3d.Dot(u0, u2)) > k_OrthoTolerance ||
                Math.Abs(Vector3d.Dot(u1, u2)) > k_OrthoTolerance)
            {
                error = $"Matrix for FBX node '{label}' contains shear and cannot be decomposed into FBX TRS.";
                return false;
            }

            // Re-orthonormalize via Gram-Schmidt to remove ~1e-15 floating-point drift before asin/atan2.
            u0 = u0.normalized;
            u1 = (u1 - u0 * Vector3d.Dot(u1, u0)).normalized;
            u2 = Vector3d.Cross(u0, u1).normalized;

            translation = matrix.Translation;
            signedScale = new Vector3d(s0, s1, s2);
            rotationMatrix = new FbxMatrix4x4(
                u0.x, u0.y, u0.z, 0d,
                u1.x, u1.y, u1.z, 0d,
                u2.x, u2.y, u2.z, 0d,
                0d, 0d, 0d, 1d);
            return true;
        }

        /// <summary>
        /// Extracts FBX Euler angles (in degrees) for a proper orthogonal row-vector rotation matrix
        /// under <paramref name="order"/>, matching <see cref="FbxMatrix4x4.RotateEulerDegrees"/>.
        /// </summary>
        public static bool TryDecomposeEulerDegrees(
            FbxMatrix4x4 rotationMatrix,
            FbxRotationOrder order,
            out Vector3d eulerDegrees)
        {
            eulerDegrees = Vector3d.zero;
            if (order == FbxRotationOrder.SphericXYZ)
            {
                return false;
            }

            double x, y, z;
            switch (order)
            {
                case FbxRotationOrder.XYZ:
                {
                    double sy = ClampSin(-rotationMatrix[0, 2]);
                    y = Math.Asin(sy);
                    if (Math.Abs(sy) < k_GimbalSinClip)
                    {
                        x = Math.Atan2(rotationMatrix[1, 2], rotationMatrix[2, 2]);
                        z = Math.Atan2(rotationMatrix[0, 1], rotationMatrix[0, 0]);
                    }
                    else
                    {
                        x = Math.Atan2(-rotationMatrix[2, 1], rotationMatrix[1, 1]);
                        z = 0d;
                    }
                    break;
                }
                case FbxRotationOrder.XZY:
                {
                    double sz = ClampSin(rotationMatrix[0, 1]);
                    z = Math.Asin(sz);
                    if (Math.Abs(sz) < k_GimbalSinClip)
                    {
                        x = Math.Atan2(-rotationMatrix[2, 1], rotationMatrix[1, 1]);
                        y = Math.Atan2(-rotationMatrix[0, 2], rotationMatrix[0, 0]);
                    }
                    else
                    {
                        x = Math.Atan2(rotationMatrix[1, 2], rotationMatrix[2, 2]);
                        y = 0d;
                    }
                    break;
                }
                case FbxRotationOrder.YZX:
                {
                    double sz = ClampSin(-rotationMatrix[1, 0]);
                    z = Math.Asin(sz);
                    if (Math.Abs(sz) < k_GimbalSinClip)
                    {
                        y = Math.Atan2(rotationMatrix[2, 0], rotationMatrix[0, 0]);
                        x = Math.Atan2(rotationMatrix[1, 2], rotationMatrix[1, 1]);
                    }
                    else
                    {
                        y = Math.Atan2(-rotationMatrix[0, 2], rotationMatrix[2, 2]);
                        x = 0d;
                    }
                    break;
                }
                case FbxRotationOrder.YXZ:
                {
                    double sx = ClampSin(rotationMatrix[1, 2]);
                    x = Math.Asin(sx);
                    if (Math.Abs(sx) < k_GimbalSinClip)
                    {
                        y = Math.Atan2(-rotationMatrix[0, 2], rotationMatrix[2, 2]);
                        z = Math.Atan2(-rotationMatrix[1, 0], rotationMatrix[1, 1]);
                    }
                    else
                    {
                        y = Math.Atan2(rotationMatrix[2, 0], rotationMatrix[0, 0]);
                        z = 0d;
                    }
                    break;
                }
                case FbxRotationOrder.ZXY:
                {
                    double sx = ClampSin(-rotationMatrix[2, 1]);
                    x = Math.Asin(sx);
                    if (Math.Abs(sx) < k_GimbalSinClip)
                    {
                        z = Math.Atan2(rotationMatrix[0, 1], rotationMatrix[1, 1]);
                        y = Math.Atan2(rotationMatrix[2, 0], rotationMatrix[2, 2]);
                    }
                    else
                    {
                        z = Math.Atan2(-rotationMatrix[1, 0], rotationMatrix[0, 0]);
                        y = 0d;
                    }
                    break;
                }
                case FbxRotationOrder.ZYX:
                {
                    double sy = ClampSin(rotationMatrix[2, 0]);
                    y = Math.Asin(sy);
                    if (Math.Abs(sy) < k_GimbalSinClip)
                    {
                        z = Math.Atan2(-rotationMatrix[1, 0], rotationMatrix[0, 0]);
                        x = Math.Atan2(-rotationMatrix[2, 1], rotationMatrix[2, 2]);
                    }
                    else
                    {
                        z = Math.Atan2(rotationMatrix[0, 1], rotationMatrix[1, 1]);
                        x = 0d;
                    }
                    break;
                }
                default:
                    return false;
            }

            const double radToDeg = 180d / Math.PI;
            var candidate = new Vector3d(x * radToDeg, y * radToDeg, z * radToDeg);
            FbxMatrix4x4 reconstructed = FbxMatrix4x4.RotateEulerDegrees(candidate, order);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    if (Math.Abs(reconstructed[r, c] - rotationMatrix[r, c]) > k_EulerRoundTripTolerance)
                    {
                        return false;
                    }
                }
            }

            eulerDegrees = candidate;
            return true;
        }

        private static double ClampSin(double value)
        {
            if (value >= 1d) return 1d;
            if (value <= -1d) return -1d;
            return value;
        }
    }
}
