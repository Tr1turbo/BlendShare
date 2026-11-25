using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.UI.Inspectors;
using Triturbo.BlendShare.Model.Serialization;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.ValueDrawers
{
    public sealed class BlendShareValueDrawContext
    {
        public string Label { get; set; }
        public string Tooltip { get; set; }
        public DataSemantic Semantic { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        public bool ReadOnly { get; set; } = true;
        public Action<object> OnValueChanged { get; set; }
        public int Depth { get; set; }
        public int MaxDepth { get; set; } = 6;
        public double PositionUnitMeters { get; set; } = 1d;
        public BlendShareValueDrawerRegistry Registry { get; private set; }

        public BlendShareValueDrawContext WithChild(string label, DataSemantic semantic = DataSemantic.Default,
            string tooltip = null, bool? readOnly = null, Action<object> onValueChanged = null,
            double? positionUnitMeters = null)
        {
            return new BlendShareValueDrawContext
            {
                Label = label, Tooltip = tooltip, Semantic = semantic, PayloadSource = PayloadSource,
                ReadOnly = readOnly ?? ReadOnly, OnValueChanged = onValueChanged, Depth = Depth + 1,
                MaxDepth = MaxDepth, PositionUnitMeters = positionUnitMeters ?? PositionUnitMeters, Registry = Registry
            };
        }

        internal BlendShareValueDrawContext WithRegistry(BlendShareValueDrawerRegistry registry)
        {
            if (Registry == registry) return this;
            var copy = (BlendShareValueDrawContext)MemberwiseClone();
            copy.Registry = registry;
            return copy;
        }

        internal VisualElement Draw(object value, Type declaredType) =>
            (Registry ?? BlendShareValueDrawerRegistry.Default).CreateView(value, declaredType, this);
    }

    public interface IBlendShareValueDrawer
    {
        int Priority { get; }
        bool CanDraw(Type valueType, DataSemantic semantic);
        VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareValueDrawerAttribute : Attribute
    {
        public Type DrawerType { get; }
        public RegisterBlendShareValueDrawerAttribute(Type drawerType) => DrawerType = drawerType;
    }

    public sealed class BlendShareValueDrawerRegistry
    {
        private static readonly Lazy<BlendShareValueDrawerRegistry> s_Default =
            new Lazy<BlendShareValueDrawerRegistry>(() => new BlendShareValueDrawerRegistry());

        private readonly object m_Lock = new object();
        private IBlendShareValueDrawer[] m_Drawers = Array.Empty<IBlendShareValueDrawer>();

        public static BlendShareValueDrawerRegistry Default => s_Default.Value;

        public BlendShareValueDrawerRegistry()
        {
            var types = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                try
                {
                    foreach (var attribute in assembly.GetCustomAttributes<RegisterBlendShareValueDrawerAttribute>())
                    {
                        var type = attribute.DrawerType;
                        if (type != null && !type.IsAbstract && typeof(IBlendShareValueDrawer).IsAssignableFrom(type))
                            types.Add(type);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"BlendShare could not discover drawers in {assembly.FullName}: {ex.Message}");
                }
            }
            foreach (var type in types.OrderBy(t => t.AssemblyQualifiedName, StringComparer.Ordinal))
            {
                try { Register((IBlendShareValueDrawer)Activator.CreateInstance(type)); }
                catch (Exception ex) { Debug.LogWarning($"BlendShare could not register {type.FullName}: {ex.Message}"); }
            }
        }

        public void Register(IBlendShareValueDrawer drawer)
        {
            if (drawer == null) throw new ArgumentNullException(nameof(drawer));
            lock (m_Lock)
            {
                if (Array.Exists(m_Drawers, d => ReferenceEquals(d, drawer))) return;
                m_Drawers = m_Drawers.Append(drawer).OrderByDescending(d => d.Priority)
                    .ThenBy(d => d.GetType().AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
            }
        }

        public bool Unregister(IBlendShareValueDrawer drawer)
        {
            lock (m_Lock)
            {
                var remaining = m_Drawers.Where(d => !ReferenceEquals(d, drawer)).ToArray();
                if (remaining.Length == m_Drawers.Length) return false;
                m_Drawers = remaining;
                return true;
            }
        }

        public IBlendShareValueDrawer Resolve(Type valueType, DataSemantic semantic = DataSemantic.Default)
        {
            if (valueType == null) return null;
            IBlendShareValueDrawer[] drawers;
            lock (m_Lock) drawers = m_Drawers;
            return Array.Find(drawers, drawer => drawer.CanDraw(valueType, semantic));
        }

        public VisualElement CreateView(object value, Type declaredType, BlendShareValueDrawContext context)
        {
            context ??= new BlendShareValueDrawContext();
            var type = value?.GetType() ?? declaredType;
            VisualElement view;
            if (value == null)
                view = BlendShareInspectorUi.Row(context.Label, "-");
            else if (context.Depth >= context.MaxDepth)
                view = BlendShareInspectorUi.Row(context.Label, "Depth limit");
            else
                view = Resolve(type, context.Semantic)?.CreateView(value, type, context.WithRegistry(this))
                    ?? BlendShareInspectorUi.Row(context.Label, value.ToString());
            if (!string.IsNullOrEmpty(context.Tooltip)) view.tooltip = context.Tooltip;
            return view;
        }
    }
}
