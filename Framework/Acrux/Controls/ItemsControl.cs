using System;
using System.Collections;
using System.Collections.Generic;
using Adk.Mvvm;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Extensions;
using Acrux.Layout;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// Generates a visual for every item and reuses existing visuals by stable key.
    /// Observes collection notifications; plain collections can be refreshed explicitly.
    /// </summary>
    public class ItemsControl : TemplatedControl
    {
        sealed class ItemRecord
        {
            public object Item;
            public object Key;
            public VisualElement Content;
            public VisualElement Container;
            public bool Reused;
        }

        static ItemsControl() { }

        Panel _itemsPanel;
        readonly List<ItemRecord> _records = new List<ItemRecord>();
        readonly List<ItemRecord> _nextRecords = new List<ItemRecord>();
        readonly List<object> _virtualItems = new List<object>();
        readonly Dictionary<int, ItemRecord> _realized = new Dictionary<int, ItemRecord>();
        readonly Stack<ItemRecord> _recycled = new Stack<ItemRecord>();
        readonly List<int> _recycleScratch = new List<int>();
        IEnumerable _itemsSource;
        IDataTemplate _itemTemplate;
        Func<object, object> _itemKeySelector;

        INotifyCollectionChanged _observableSource;

        ITemplate<Panel> _appliedPanelTemplate;

        public static readonly StyledProperty<ITemplate<Panel>> ItemsPanelProperty =
            StyledProperty.Register<ItemsControl, ITemplate<Panel>>(
                nameof(ItemsPanel), null, UiInvalidation.Measure);

        public ITemplate<Panel> ItemsPanel
        {
            get { return GetValue(ItemsPanelProperty); }
            set { SetValue(ItemsPanelProperty, value); }
        }


        public ItemsControl()
        {
            _itemsPanel = CreateDefaultItemsPanel();
            AttachItemsPanel(_itemsPanel);
        }

        public IEnumerable ItemsSource
        {
            get { return _itemsSource; }
            set
            {
                if (ReferenceEquals(_itemsSource, value))
                {
                    // Re-assigning a plain (non-observable) list is how callers ask for a refresh.
                    if (_observableSource == null) RefreshItems();
                    return;
                }

                if (_observableSource != null) _observableSource.CollectionChanged -= OnCollectionChanged;

                _itemsSource = value;

                _observableSource = value as INotifyCollectionChanged;
                if (_observableSource != null) _observableSource.CollectionChanged += OnCollectionChanged;

                RefreshItems();
            }
        }

        public IDataTemplate ItemTemplate
        {
            get { return _itemTemplate; }
            set
            {
                if (ReferenceEquals(_itemTemplate, value))
                    return;
                _itemTemplate = value;
                RebuildItems();
            }
        }

        public Func<object, object> ItemKeySelector
        {
            get { return _itemKeySelector; }
            set
            {
                if (ReferenceEquals(_itemKeySelector, value))
                    return;
                _itemKeySelector = value;
                RefreshItems();
            }
        }

        public IEqualityComparer<object> ItemKeyComparer { get; set; } =
            EqualityComparer<object>.Default;

        public int ItemCount
        {
            get { return IsVirtualizing ? _virtualItems.Count : _records.Count; }
        }

        bool IsVirtualizing => _itemsPanel is IVirtualizingItemsPanel;

        public Panel ItemsPanelRoot
        {
            get { return _itemsPanel; }
        }

        public object GetItemAt(int index)
        {
            return IsVirtualizing ? _virtualItems[index] : _records[index].Item;
        }

        /// <summary>The container for an item, or null while a virtualized item is not realized.</summary>
        public VisualElement GetContainerAt(int index)
        {
            if (!IsVirtualizing)
                return _records[index].Container;
            ItemRecord record;
            return _realized.TryGetValue(index, out record) ? record.Container : null;
        }

        public int IndexOfItem(object item)
        {
            int count = ItemCount;
            for (int i = 0; i < count; i++)
            {
                object candidate = GetItemAt(i);
                if (ReferenceEquals(candidate, item) || Equals(candidate, item))
                    return i;
            }
            return -1;
        }

        public void RefreshItems()
        {
            if (!ReferenceEquals(ItemsPanel, _appliedPanelTemplate))
            {
                Invalidate(UiInvalidation.Measure);
                return;
            }
            if (IsVirtualizing)
            {
                RefreshVirtualItems();
                return;
            }

            for (int i = 0; i < _records.Count; i++)
                _records[i].Reused = false;

            List<ItemRecord> next = _nextRecords;
            next.Clear();
            bool changed = false;
            if (_itemsSource != null)
            {
                foreach (object item in _itemsSource)
                {
                    object key = GetKey(item);
                    ItemRecord record = FindReusableRecord(key);
                    if (record == null)
                    {
                        VisualElement content = BuildContent(item);
                        if (content == null)
                            throw new InvalidOperationException("An item template returned null.");
                        VisualElement container = CreateContainer(item, content);
                        if (container == null)
                            throw new InvalidOperationException("An items control returned a null container.");
                        record = new ItemRecord
                        {
                            Content = content,
                            Container = container
                        };
                        _itemsPanel.AddChild(container);
                        changed = true;
                    }

                    record.Item = item;
                    record.Key = key;
                    record.Reused = true;
                    record.Content.DataContext = item;
                    record.Container.DataContext = item;
                    PrepareContainer(record.Container, record.Content, item);
                    changed |= next.Count >= _records.Count || !ReferenceEquals(_records[next.Count], record);
                    next.Add(record);
                }
            }

            for (int i = 0; i < _records.Count; i++)
            {
                ItemRecord record = _records[i];
                if (record.Reused)
                    continue;
                _itemsPanel.RemoveChild(record.Container);
                record.Container.Dispose();
                changed = true;
            }

            _records.Clear();
            _records.AddRange(next);
            next.Clear();
            if (changed)
            {
                for (int i = 0; i < _records.Count; i++)
                    _itemsPanel.MoveChild(_records[i].Container, i);
                Invalidate(UiInvalidation.Measure);
            }
            OnItemsRefreshed();
        }

        void RefreshVirtualItems()
        {
            _virtualItems.Clear();
            if (_itemsSource != null)
                foreach (object item in _itemsSource)
                    _virtualItems.Add(item);

            _recycleScratch.Clear();
            foreach (KeyValuePair<int, ItemRecord> pair in _realized)
            {
                if (pair.Key >= _virtualItems.Count)
                    _recycleScratch.Add(pair.Key);
                else
                    Bind(pair.Value, _virtualItems[pair.Key]);
            }
            for (int i = 0; i < _recycleScratch.Count; i++)
                Recycle(_recycleScratch[i]);
            Invalidate(UiInvalidation.Measure);
            OnItemsRefreshed();
        }

        internal VisualElement RealizeContainer(int index)
        {
            ItemRecord record;
            if (_realized.TryGetValue(index, out record))
                return record.Container;

            object item = _virtualItems[index];
            if (_recycled.Count > 0)
            {
                record = _recycled.Pop();
                record.Container.Visible = true;
            }
            else
            {
                VisualElement content = BuildContent(item);
                if (content == null)
                    throw new InvalidOperationException("An item template returned null.");
                VisualElement container = CreateContainer(item, content);
                if (container == null)
                    throw new InvalidOperationException("An items control returned a null container.");
                record = new ItemRecord { Content = content, Container = container };
                _itemsPanel.AddChild(container);
            }

            Bind(record, item);
            _realized[index] = record;
            return record.Container;
        }

        internal void RecycleOutside(int first, int last)
        {
            _recycleScratch.Clear();
            foreach (int index in _realized.Keys)
                if (index < first || index > last)
                    _recycleScratch.Add(index);
            for (int i = 0; i < _recycleScratch.Count; i++)
                Recycle(_recycleScratch[i]);
        }

        void Recycle(int index)
        {
            ItemRecord record = _realized[index];
            _realized.Remove(index);
            record.Container.Visible = false;
            _recycled.Push(record);
        }

        void Bind(ItemRecord record, object item)
        {
            record.Item = item;
            record.Key = GetKey(item);
            record.Content.DataContext = item;
            record.Container.DataContext = item;
            PrepareContainer(record.Container, record.Content, item);
        }

        void ClearContainers()
        {
            for (int i = 0; i < _records.Count; i++)
            {
                _itemsPanel.RemoveChild(_records[i].Container);
                _records[i].Container.Dispose();
            }
            _records.Clear();
            foreach (ItemRecord record in _realized.Values)
            {
                _itemsPanel.RemoveChild(record.Container);
                record.Container.Dispose();
            }
            _realized.Clear();
            while (_recycled.Count > 0)
            {
                ItemRecord record = _recycled.Pop();
                _itemsPanel.RemoveChild(record.Container);
                record.Container.Dispose();
            }
            _virtualItems.Clear();
        }

        void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            RefreshItems();
        }

        protected virtual VisualElement CreateContainer(object item, VisualElement content)
        {
            return content;
        }

        protected virtual void PrepareContainer(
            VisualElement container,
            VisualElement content,
            object item)
        {
        }

        protected virtual Panel CreateDefaultItemsPanel()
        {
            return new StackPanel();
        }

        protected virtual void OnItemsRefreshed()
        {
        }

        /// <summary>Places the items panel in the visual tree; subclasses may host it inside their own presenter.</summary>
        protected virtual void AttachItemsPanel(Panel panel)
        {
            AddChild(panel);
        }

        protected virtual void DetachItemsPanel(Panel panel)
        {
            if (panel.Parent != null)
                panel.Parent.RemoveChild(panel);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureItemsPanel();
            return _itemsPanel.Measure(availableSize);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            EnsureItemsPanel();
            _itemsPanel.ArrangeInSlot(bounds);
        }

        public override void Dispose()
        {
            if (_observableSource != null) _observableSource.CollectionChanged -= OnCollectionChanged;
            _observableSource = null;
            _records.Clear();
            _realized.Clear();
            _recycled.Clear();
            _virtualItems.Clear();
            _itemsSource = null;
            base.Dispose();
        }

        void EnsureItemsPanel()
        {
            var template = ItemsPanel;
            if (ReferenceEquals(template, _appliedPanelTemplate) && _itemsPanel != null)
                return;

            var panelTemplate = template as ItemsPanelTemplate;
            Panel next = template == null
                ? CreateDefaultItemsPanel()
                : (panelTemplate != null ? panelTemplate.Build(this) : template.Build());
            if (next == null || next.Parent != null)
                throw new InvalidOperationException("ItemsPanel must build a fresh panel.");
            bool rebuild = IsVirtualizing || next is IVirtualizingItemsPanel;
            Panel previous = _itemsPanel;
            if (rebuild)
                ClearContainers();
            else
                for (int i = 0; i < _records.Count; i++)
                    previous.RemoveChild(_records[i].Container);
            DetachItemsPanel(previous);
            previous.Dispose();

            _itemsPanel = next;
            _appliedPanelTemplate = template;
            AttachItemsPanel(_itemsPanel);
            if (!rebuild)
                for (int i = 0; i < _records.Count; i++)
                    _itemsPanel.AddChild(_records[i].Container);
            RefreshItems();
        }

        void RebuildItems()
        {
            ClearContainers();
            RefreshItems();
        }

        ItemRecord FindReusableRecord(object key)
        {
            IEqualityComparer<object> comparer = ItemKeyComparer ?? EqualityComparer<object>.Default;
            for (int i = 0; i < _records.Count; i++)
            {
                ItemRecord candidate = _records[i];
                if (!candidate.Reused && comparer.Equals(candidate.Key, key))
                    return candidate;
            }
            return null;
        }

        object GetKey(object item)
        {
            return _itemKeySelector == null ? item : _itemKeySelector(item);
        }

        VisualElement BuildContent(object item)
        {
            if (_itemTemplate != null)
                return _itemTemplate.Build(item);
            return new TextBlock
            {
                Text = item == null ? string.Empty : item.ToString(),
                VerticalTextAlignment = VerticalAlignment.Center
            };
        }

    }
}
