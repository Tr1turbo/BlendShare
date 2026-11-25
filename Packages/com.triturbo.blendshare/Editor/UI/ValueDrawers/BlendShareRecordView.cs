using Triturbo.BlendShare.UI.Inspectors;
using Triturbo.BlendShare.Model.Serialization;
using UnityEditor;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.ValueDrawers
{
    public sealed class BlendShareRecordView : VisualElement
    {
        private readonly BlendShareValueDrawerRegistry m_Registry;
        private readonly Foldout m_Header;
        private readonly string m_Title;

        public VisualElement ContentContainer { get; }

        public BlendShareRecordView(object record, ILazyPayloadSource payloadSource = null, string title = null,
            bool startExpanded = true, BlendShareValueDrawerRegistry registry = null, double positionUnitMeters = 1d)
        {
            m_Registry = registry ?? BlendShareValueDrawerRegistry.Default;
            m_Title = title;
            m_Header = new Foldout { value = startExpanded };
            ContentContainer = BlendShareInspectorUi.Box();
            m_Header.Add(ContentContainer);
            Add(m_Header);
            SetRecord(record, payloadSource, positionUnitMeters);
        }

        public void SetRecord(object record, ILazyPayloadSource payloadSource = null, double positionUnitMeters = 1d)
        {
            m_Header.text = m_Title ?? ObjectNames.NicifyVariableName(record?.GetType().Name ?? "Record");
            ContentContainer.Clear();
            ContentContainer.Add(m_Registry.CreateView(record, record?.GetType(), new BlendShareValueDrawContext
            {
                PayloadSource = payloadSource,
                PositionUnitMeters = positionUnitMeters
            }));
        }
    }
}
