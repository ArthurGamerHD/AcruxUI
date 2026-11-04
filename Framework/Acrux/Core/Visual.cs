using System;
using Acrux.Core.Invalidation;

namespace Acrux.Core
{
    public abstract class Visual : IDisposable, IUiInvalidatable
    {
        // Client wrapper of this element when it is driven from another mod through the Acrux UI Toolkit bridge.
        internal object ExternalPeer;

        bool _visible = true;
        bool _enabled = true;
        UiInvalidation _pendingInvalidation =
            UiInvalidation.Measure | UiInvalidation.Arrange | UiInvalidation.Render;

        protected abstract IUiInvalidatable InvalidationParent { get; }

        public bool Visible
        {
            get { return _visible; }
            set
            {
                if (_visible == value)
                    return;
                _visible = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                Invalidate(UiInvalidation.Render);
            }
        }

        public UiInvalidation PendingInvalidation => _pendingInvalidation;

        public void Invalidate(UiInvalidation invalidation)
        {
            invalidation = ExpandInvalidation(invalidation);
            _pendingInvalidation |= invalidation;

            IUiInvalidatable parent = InvalidationParent;
            if (parent == null)
                return;
            if ((invalidation & (UiInvalidation.Measure | UiInvalidation.Arrange)) != 0)
                parent.Invalidate(invalidation);
            else if (_visible && (parent.PendingInvalidation & UiInvalidation.Render) == 0)
                parent.Invalidate(UiInvalidation.Render);
        }

        protected void ClearInvalidation(UiInvalidation invalidation)
        {
            _pendingInvalidation &= ~invalidation;
        }

        static UiInvalidation ExpandInvalidation(UiInvalidation invalidation)
        {
            if ((invalidation & UiInvalidation.Style) != 0)
                invalidation |= UiInvalidation.Measure;
            if ((invalidation & UiInvalidation.Measure) != 0)
                invalidation |= UiInvalidation.Arrange;
            if ((invalidation & UiInvalidation.Arrange) != 0)
                invalidation |= UiInvalidation.Render;
            return invalidation;
        }

        public virtual void Dispose()
        {
        }
    }
}
