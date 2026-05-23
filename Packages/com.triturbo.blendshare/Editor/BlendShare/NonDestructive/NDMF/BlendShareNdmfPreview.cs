using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Persistence;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NonDestructive.NDMF
{
    internal sealed class BlendShareNdmfPreview : IRenderFilter
    {
        public static readonly TogglablePreviewNode PreviewNode =
            TogglablePreviewNode.Create(() => "BlendShare", "com.triturbo.blendshare", false);

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(PreviewNode.IsEnabled);
        }

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return PreviewNode;
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            return context.GetComponentsByType<BlendShareMeshApplierComponent>()
                .Where(applier => context.Observe(applier, item => item.EnabledForBuild) &&
                                  !context.Observe(applier, item => item.IsStale) &&
                                  context.Observe(applier, item => item.TargetRenderer) != null)
                .GroupBy(applier => context.Observe(applier, item => item.TargetRenderer))
                .Select(group => RenderGroup.For(group.Key).WithData(group.ToArray(), Enumerable.SequenceEqual))
                .ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
        {
            var node = new Node(group.GetData<BlendShareMeshApplierComponent[]>(), pairs, context);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly ComputeContext meshContext = new("BlendShare.NDMFPreview.MeshContext");
            private readonly BlendShareMeshApplierComponent[] appliers;
            private Mesh mesh;
            private Transform[] bones = System.Array.Empty<Transform>();
            private Transform rootBone;

            public RenderAspects WhatChanged { get; private set; } = RenderAspects.Mesh | RenderAspects.Shapes;

            public Node(BlendShareMeshApplierComponent[] appliers, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
            {
                this.appliers = appliers;
                Process(pairs, context);
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects aspects)
            {
                if (meshContext.IsInvalidated || aspects.HasFlag(RenderAspects.Mesh) || aspects.HasFlag(RenderAspects.Shapes))
                {
                    Dispose();
                    var node = new Node(appliers, pairs, context);
                    return Task.FromResult<IRenderFilterNode>(node);
                }

                WhatChanged = 0;
                meshContext.Invalidates(context);
                return Task.FromResult<IRenderFilterNode>(this);
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (proxy is SkinnedMeshRenderer skinnedMeshRenderer)
                {
                    skinnedMeshRenderer.sharedMesh = mesh;
                    skinnedMeshRenderer.rootBone = rootBone;
                    skinnedMeshRenderer.bones = bones ?? System.Array.Empty<Transform>();
                }
            }

            public void Dispose()
            {
                meshContext.Invalidate();
                if (mesh != null)
                {
                    Object.DestroyImmediate(mesh);
                    mesh = null;
                }
            }

            private void Process(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
            {
                var (original, proxy) = pairs.Single();
                if (original is not SkinnedMeshRenderer originalRenderer || proxy is not SkinnedMeshRenderer proxyRenderer)
                {
                    return;
                }

                var enabledAppliers = appliers
                    .Where(applier => meshContext.Observe(applier, item => item.EnabledForBuild) &&
                                      !meshContext.Observe(applier, item => item.IsStale))
                    .ToArray();
                var owners = enabledAppliers
                    .Select(applier => meshContext.Observe(applier, item => item.Owner))
                    .Where(owner => owner != null)
                    .Distinct()
                    .ToArray();
                var sourceRoot = owners
                    .Select(BlendShareApplierSetupService.ResolveTargetRoot)
                    .FirstOrDefault(root => root != null) ?? originalRenderer.transform.root;
                var boneProxies = sourceRoot != null
                    ? sourceRoot.GetComponentsInChildren<BlendShareBoneProxyComponent>(true)
                    : System.Array.Empty<BlendShareBoneProxyComponent>();
                var proxyRoot = proxyRenderer.transform.root;
                var source = new BlendShareComponentGenerationSource(
                    sourceRoot != null ? sourceRoot.gameObject : originalRenderer.transform.root.gameObject,
                    enabledAppliers,
                    boneProxies);
                var artifact = BlendShareArtifactService.CreateInMemoryArtifact(source);
                if (artifact == null)
                {
                    Debug.LogWarning("[BlendShare Preview] Failed to generate BlendShare artifact.", original);
                    return;
                }

                var result = BlendShareArtifactService.ApplyArtifact(
                    artifact,
                    proxyRoot,
                    new BlendShareArtifactApplyOptions
                    {
                        UseUndo = false,
                        RecordDestructiveMarkers = false,
                        MarkObjectsDirty = false
                    });

                if (!result.Success)
                {
                    foreach (string diagnostic in result.Diagnostics)
                    {
                        Debug.LogWarning($"[BlendShare Preview] {diagnostic}", original);
                    }
                    return;
                }

                mesh = proxyRenderer.sharedMesh;
                bones = proxyRenderer.bones;
                rootBone = proxyRenderer.rootBone;
                meshContext.Observe(originalRenderer, renderer => renderer.sharedMesh);
                meshContext.Observe(originalRenderer, renderer => renderer.bones);
                meshContext.Observe(originalRenderer, renderer => renderer.rootBone);
                meshContext.Invalidates(context);
            }
        }
    }
}
