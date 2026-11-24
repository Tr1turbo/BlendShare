using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;
using Triturbo.BlendShare.UI.ValueDrawers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Ui = Triturbo.BlendShare.Inspector.BlendShareInspectorUi;

[assembly: RegisterBlendShareValueDrawer(typeof(PrimitiveValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(VectorValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(FbxMatrix4x4ValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(PatchSpaceProvenanceValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(TopologyDigestValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(IndexedBufferValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(LazyPayloadValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(CollectionValueDrawer))]
[assembly: RegisterBlendShareValueDrawer(typeof(BlendShareRecordValueDrawer))]

namespace Triturbo.BlendShare.UI.ValueDrawers
{
    internal static class DrawerUi
    {
        internal static string Unit(double meters)
        {
            string symbol = meters switch { 1d => "m", 0.01d => "cm", 0.001d => "mm", _ => "unit" };
            return FormattableString.Invariant($"{symbol} ({meters:G} m)");
        }

        internal static string Format(object value, string format)
        {
            return value switch
            {
                Vector2d v => v.ToString(format),
                Vector3d v => v.ToString(format),
                Vector4d v => v.ToString(format),
                Vector2 v => ((Vector2d)v).ToString(format),
                Vector3 v => ((Vector3d)v).ToString(format),
                Vector4 v => ((Vector4d)v).ToString(format),
                double d => d.ToString(format, CultureInfo.InvariantCulture),
                float f => ((double)f).ToString(format, CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? "-"
            };
        }

        internal static string Hint(BlendShareValueDrawContext context) => context.Semantic switch
        {
            DataSemantic.PositionDelta => "Position delta · " + Unit(context.PositionUnitMeters),
            DataSemantic.Default => string.Empty,
            _ => ObjectNames.NicifyVariableName(context.Semantic.ToString())
        };

        internal static VisualElement NumericRow(object value, BlendShareValueDrawContext context)
        {
            var hint = Hint(context);
            var label = Ui.ValueLabel(Format(value, "G6"));
            label.tooltip = Format(value, "G17") + (hint.Length == 0 ? "" : "\n" + hint);
            return Ui.LabeledRow(context.Label, label);
        }

        internal static Foldout Deferred(string title, Action<Foldout> populate)
        {
            var foldout = new Foldout { text = title, value = false };
            foldout.RegisterValueChangedCallback(evt =>
            {
                if (evt.target != foldout) return;
                foldout.Clear();
                if (evt.newValue) populate(foldout);
            });
            foldout.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (evt.target != foldout) return;
                foldout.SetValueWithoutNotify(false);
                foldout.Clear();
            });
            return foldout;
        }

        internal static ListView List(IList source, Func<VisualElement> make, Action<VisualElement, int> bind,
            CollectionVirtualizationMethod method)
        {
            var list = new ListView(source, 20, make, bind)
            {
                virtualizationMethod = method,
                selectionType = SelectionType.None
            };
            list.style.height = Math.Min(source.Count, 10) * 20;
            list.style.maxHeight = 200;
            return list;
        }

        internal delegate VisualElement BuildTyped(object value, BlendShareValueDrawContext context);

        internal static BuildTyped CloseGeneric(Type owner, string method, Type argument)
        {
            var definition = owner.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            return (BuildTyped)definition.MakeGenericMethod(argument).CreateDelegate(typeof(BuildTyped));
        }
    }

    public sealed class PrimitiveValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 80;

        public bool CanDraw(Type valueType, DataSemantic semantic) =>
            valueType.IsEnum || valueType == typeof(string) || valueType == typeof(bool) ||
            valueType == typeof(byte) || valueType == typeof(short) || valueType == typeof(int) ||
            valueType == typeof(long) || valueType == typeof(float) || valueType == typeof(double);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            if (context.ReadOnly || context.OnValueChanged == null) return DrawerUi.NumericRow(value, context);
            VisualElement field = value switch
            {
                string s => Bind(new TextField(), s, context),
                bool b => Bind(new Toggle(), b, context),
                byte b => Narrow(b, byte.MinValue, byte.MaxValue, context, n => (byte)n),
                short s => Narrow(s, short.MinValue, short.MaxValue, context, n => (short)n),
                int i => Bind(new IntegerField(), i, context),
                long l => Bind(new LongField(), l, context),
                float f => Bind(new FloatField(), f, context),
                double d => Bind(new DoubleField(), d, context),
                Enum e => Bind(new EnumField(e), e, context),
                _ => throw new ArgumentException("Unsupported primitive type.", nameof(value))
            };
            return Ui.LabeledRow(context.Label, Ui.RowField(field));
        }

        private static BaseField<T> Bind<T>(BaseField<T> field, T value, BlendShareValueDrawContext context)
        {
            field.SetValueWithoutNotify(value);
            field.RegisterValueChangedCallback(evt => context.OnValueChanged(evt.newValue));
            return field;
        }

        private static IntegerField Narrow(int value, int min, int max, BlendShareValueDrawContext context,
            Func<int, object> convert)
        {
            var field = new IntegerField { value = value };
            field.RegisterValueChangedCallback(evt =>
            {
                int bounded = Mathf.Clamp(evt.newValue, min, max);
                field.SetValueWithoutNotify(bounded);
                context.OnValueChanged(convert(bounded));
            });
            return field;
        }
    }

    public sealed class VectorValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 100;
        public bool CanDraw(Type valueType, DataSemantic semantic) =>
            valueType == typeof(Vector2d) || valueType == typeof(Vector3d) || valueType == typeof(Vector4d) ||
            valueType == typeof(Vector2) || valueType == typeof(Vector3) || valueType == typeof(Vector4);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context) =>
            DrawerUi.NumericRow(value, context);
    }

    public sealed class FbxMatrix4x4ValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 100;
        public bool CanDraw(Type valueType, DataSemantic semantic) => valueType == typeof(FbxMatrix4x4);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            var matrix = (FbxMatrix4x4)value;
            double Scale(int row) => new Vector3d(matrix[row, 0], matrix[row, 1], matrix[row, 2]).magnitude;
            string summary = matrix.Equals(FbxMatrix4x4.Identity) ? "Identity" :
                $"T {matrix.Translation.ToString("G4")}  S {new Vector3d(Scale(0), Scale(1), Scale(2)).ToString("G4")}";
            var foldout = new Foldout { text = $"{context.Label ?? "Matrix"} ({summary})", value = false };
            for (int r = 0; r < 4; r++)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginLeft = 15;
                row.style.marginTop = row.style.marginBottom = 1;
                row.style.flexGrow = row.style.flexShrink = 1;
                row.style.minWidth = 0;
                for (int c = 0; c < 4; c++)
                {
                    var field = new FloatField
                    {
                        value = (float)matrix[r, c],
                        tooltip = matrix[r, c].ToString("G17", CultureInfo.InvariantCulture)
                    };
                    field.labelElement.style.display = DisplayStyle.None;
                    field.style.flexGrow = field.style.flexShrink = 1;
                    field.style.minWidth = 0;
                    field.style.marginRight = 3;
                    field.SetEnabled(false);
                    row.Add(field);
                }
                foldout.Add(row);
            }
            return foldout;
        }
    }

    public sealed class PatchSpaceProvenanceValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 100;
        public bool CanDraw(Type valueType, DataSemantic semantic) => valueType == typeof(PatchSpaceProvenance);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            var space = (PatchSpaceProvenance)value;
            string Axis(FbxCoordinateAxis axis) => axis.ToString().Replace("Positive", "+").Replace("Negative", "-");
            string axes = $"{Axis(space.OriginRightAxis)} / {Axis(space.OriginUpAxis)} / {Axis(space.OriginFrontAxis)}";
            var box = Ui.Box();
            box.Add(Ui.LabeledRow(context.Label ?? "Space Provenance", Ui.BadgeRow(
                Ui.Badge(DrawerUi.Unit(space.OriginRawMetersPerUnit)), Ui.Badge(axes))));
            var rawToCanonical = CanonicalMeshSpace.BuildRawToCanonical(space.OriginAxes, space.OriginRawMetersPerUnit, out _);
            box.Add(context.WithChild("Origin Raw To Canonical").Draw(rawToCanonical, typeof(FbxMatrix4x4)));
            return box;
        }
    }

    public sealed class TopologyDigestValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 100;
        public bool CanDraw(Type valueType, DataSemantic semantic) => valueType == typeof(TopologyDigestRecord);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            var digest = (TopologyDigestRecord)value;
            string hash = digest.Hash ?? "";
            var hashBadge = Ui.Badge(hash.Length == 0 ? "-" : hash.Substring(0, Math.Min(12, hash.Length)));
            hashBadge.tooltip = hash;
            return Ui.LabeledRow(context.Label ?? "Topology", Ui.BadgeRow(
                Ui.Badge(digest.IsValid ? "Valid" : "Invalid", digest.IsValid ? StatusKind.Success : StatusKind.Error),
                hashBadge, Ui.Badge($"{digest.ControlPointCount} CPs / {digest.FaceCount} Faces")));
        }
    }

    public sealed class IndexedBufferValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 90;
        public bool CanDraw(Type valueType, DataSemantic semantic) =>
            valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IndexedBuffer<>);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context) =>
            DrawerUi.CloseGeneric(typeof(IndexedBufferValueDrawer), nameof(Build), valueType.GetGenericArguments()[0])(value, context);

        private static VisualElement Build<T>(object value, BlendShareValueDrawContext context)
        {
            var buffer = (IndexedBuffer<T>)value;
            double sparsity = buffer.DomainCount == 0 ? 0 : 100d * (buffer.DomainCount - buffer.Count) / buffer.DomainCount;
            string title = $"{context.Label ?? "Buffer"} ({buffer.Domain}, {buffer.Count} / {buffer.DomainCount}, {sparsity:0.#}% sparse)";
            return DrawerUi.Deferred(title, foldout =>
            {
                void Bind(VisualElement element, int slot)
                {
                    var label = (Label)element;
                    T entry = buffer.Values[slot];
                    string formatted = DrawerUi.Format(entry, "G6");
                    if (context.Semantic == DataSemantic.NormalizedWeight && entry is double weight)
                        formatted += $" ({weight:P1})";
                    else if (context.Semantic == DataSemantic.SignedWeightDelta && entry is double delta)
                        formatted = $"{(delta >= 0d ? "+" : string.Empty)}{delta.ToString("G6", CultureInfo.InvariantCulture)} ({(delta >= 0d ? "+" : string.Empty)}{delta:P1})";
                    label.text = $"[{buffer.Indices[slot]}] → {formatted}";
                    label.tooltip = $"{DrawerUi.Format(entry, "G17")} {DrawerUi.Hint(context)}".TrimEnd();
                }
                foldout.Add(DrawerUi.List(buffer.CopyIndices(), () => new Label(), Bind,
                    CollectionVirtualizationMethod.FixedHeight));
            });
        }
    }

    public sealed class LazyPayloadValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 90;
        public bool CanDraw(Type valueType, DataSemantic semantic) =>
            valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(LazyPayload<>);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context) =>
            DrawerUi.CloseGeneric(typeof(LazyPayloadValueDrawer), nameof(Build), valueType.GetGenericArguments()[0])(value, context);

        private static VisualElement Build<T>(object value, BlendShareValueDrawContext context) where T : class
        {
            var payload = (LazyPayload<T>)value;
            string label = context.Label ?? "Payload";
            if (!payload.HasPayload) return Ui.LabeledRow(label, Ui.Badge("Empty"));
            string state = payload.State == LazyPayloadState.Ref ? $"Lazy Ref — {payload.Reference.UncompressedBytes} B" : "Loaded";
            return DrawerUi.Deferred($"{label} ({state})", foldout =>
            {
                if (payload.State == LazyPayloadState.Ref && context.PayloadSource == null)
                {
                    Ui.AddHelpBox(foldout, "No payload source available to decode lazy payload.", HelpBoxMessageType.Info);
                    return;
                }
                try
                {
                    using (var lease = payload.Acquire(context.PayloadSource))
                    {
                        foldout.Add(context.WithChild(null, context.Semantic).Draw(lease.Value, typeof(T)));
                    }
                }
                catch (Exception exception)
                {
                    Ui.AddHelpBox(foldout, exception.Message, HelpBoxMessageType.Error);
                }
            });
        }
    }

    public sealed class CollectionValueDrawer : IBlendShareValueDrawer
    {
        public int Priority => 50;
        private static Type ReadOnlyList(Type type) => type.GetInterfaces().Prepend(type).FirstOrDefault(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));

        public bool CanDraw(Type valueType, DataSemantic semantic) => valueType != typeof(string) &&
            (typeof(IList).IsAssignableFrom(valueType) || ReadOnlyList(valueType) != null);

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            if (value is Array array && !valueType.IsSZArray)
                return Build(array.Length, i => GetArrayItem(array, i), context);
            if (value is IList list)
                return Build(list.Count, i => list[i], context, list);
            return DrawerUi.CloseGeneric(typeof(CollectionValueDrawer), nameof(BuildReadOnly),
                ReadOnlyList(valueType).GetGenericArguments()[0])(value, context);
        }

        private static object GetArrayItem(Array array, int index)
        {
            var indices = new int[array.Rank];
            for (int d = indices.Length - 1; d >= 0; d--)
            {
                indices[d] = array.GetLowerBound(d) + index % array.GetLength(d);
                index /= array.GetLength(d);
            }
            return array.GetValue(indices);
        }

        private static VisualElement BuildReadOnly<T>(object value, BlendShareValueDrawContext context)
        {
            var list = (IReadOnlyList<T>)value;
            return Build(list.Count, i => list[i], context);
        }

        private static VisualElement Build(int count, Func<int, object> get, BlendShareValueDrawContext context,
            IList source = null)
        {
            if (count == 0) return Ui.Row(context.Label, "0 items");
            VisualElement Child(int i) => context.WithChild($"[{i}]", context.Semantic).Draw(get(i), null);
            return DrawerUi.Deferred($"{context.Label ?? "Items"} ({count})", foldout =>
            {
                if (count <= 16)
                {
                    for (int i = 0; i < count; i++) foldout.Add(Child(i));
                    return;
                }
                var list = DrawerUi.List(source ?? Enumerable.Range(0, count).ToArray(), () => new VisualElement(),
                    (row, i) => { row.Clear(); row.Add(Child(i)); }, CollectionVirtualizationMethod.DynamicHeight);
                list.unbindItem = (row, _) => row.Clear();
                foldout.Add(list);
            });
        }
    }

    public sealed class BlendShareRecordValueDrawer : IBlendShareValueDrawer
    {
        private static readonly ConcurrentDictionary<Type, (FieldInfo Field, BlendShareFieldAttribute Tag)[]> s_Fields =
            new ConcurrentDictionary<Type, (FieldInfo, BlendShareFieldAttribute)[]>();

        public int Priority => 10;
        public bool CanDraw(Type valueType, DataSemantic semantic) =>
            valueType.IsDefined(typeof(BlendShareRecordAttribute), false) ||
            valueType.IsDefined(typeof(BlendShareFeatureAttribute), false) ||
            typeof(BlendShareFeatureData).IsAssignableFrom(valueType);

        private static (FieldInfo, BlendShareFieldAttribute)[] GetFields(Type type)
        {
            var fields = new List<(FieldInfo Field, BlendShareFieldAttribute Tag)>();
            for (; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var tag = field.GetCustomAttribute<BlendShareFieldAttribute>();
                    if (tag != null) fields.Add((field, tag));
                }
            }
            return fields.OrderBy(entry => entry.Tag.Tag).ToArray();
        }

        public VisualElement CreateView(object value, Type valueType, BlendShareValueDrawContext context)
        {
            var root = string.IsNullOrEmpty(context.Label) ? new VisualElement() : Ui.Section(context.Label);
            if (value is UnknownFeatureData unknown)
            {
                root.Add(Ui.Row("Type Id", unknown.TypeId));
                root.Add(Ui.Row("Schema Version", unknown.SchemaVersion.ToString()));
                root.Add(Ui.BadgeRow(Ui.Badge(unknown.Required ? "Required" : "Optional",
                    unknown.Required ? StatusKind.Warning : StatusKind.Neutral)));
                root.Add(Ui.Row("Estimated Payload Bytes", unknown.EstimatedPayloadBytes.ToString()));
                return root;
            }
            double unit = context.PositionUnitMeters;
            if (value is PatchDocument patch)
                unit = patch.SpaceProvenance != null && CanonicalMeshSpace.IsValidUnitMeters(patch.SpaceProvenance.OriginRawMetersPerUnit)
                    ? patch.SpaceProvenance.OriginRawMetersPerUnit : 1d;
            foreach (var (field, tag) in s_Fields.GetOrAdd(valueType, GetFields))
            {
                var child = context.WithChild(ObjectNames.NicifyVariableName(field.Name), tag.Semantic,
                    positionUnitMeters: unit);
                root.Add(child.Draw(field.GetValue(value), field.FieldType));
            }
            return root;
        }
    }
}
