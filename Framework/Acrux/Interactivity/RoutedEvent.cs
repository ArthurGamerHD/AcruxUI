using System;
using System.Collections.Generic;
using Acrux.Core;

namespace Acrux.Interactivity
{
    [Flags]
    public enum RoutingStrategies
    {
        Direct = 0x01,
        Tunnel = 0x02,
        Bubble = 0x04
    }

    public class RoutedEventArgs : EventArgs
    {
        public RoutedEventArgs()
        {
        }

        public RoutedEventArgs(RoutedEvent routedEvent)
        {
            RoutedEvent = routedEvent;
            Route = routedEvent == null ? RoutingStrategies.Direct : routedEvent.RoutingStrategies;
        }

        public RoutedEventArgs(RoutedEvent routedEvent, object source)
            : this(routedEvent)
        {
            Source = source;
        }

        public bool Handled { get; set; }
        public RoutedEvent RoutedEvent { get; set; }
        public RoutingStrategies Route { get; set; }
        public object Source { get; set; }
    }

    /// <summary>
    /// RoutedEvent subset: events are raised on their source only (no tunnelling or bubbling yet), and class
    /// handlers see every raise of the event.
    /// </summary>
    public class RoutedEvent
    {
        readonly List<Action<object, RoutedEventArgs>> _classHandlers = new List<Action<object, RoutedEventArgs>>();

        protected RoutedEvent(string name, RoutingStrategies routingStrategies, Type ownerType)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            Name = name;
            RoutingStrategies = routingStrategies;
            OwnerType = ownerType;
        }

        public string Name { get; }
        public Type OwnerType { get; }
        public RoutingStrategies RoutingStrategies { get; }

        public static RoutedEvent<TEventArgs> Register<TOwner, TEventArgs>(
            string name,
            RoutingStrategies routingStrategy)
            where TEventArgs : RoutedEventArgs
        {
            return new RoutedEvent<TEventArgs>(name, routingStrategy, typeof(TOwner));
        }

        internal IDisposable AddClassHandler(
            Action<object, RoutedEventArgs> handler,
            RoutingStrategies routes,
            bool handledEventsToo)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            Action<object, RoutedEventArgs> filtered = (sender, e) =>
            {
                if ((e.Route == RoutingStrategies.Direct || (e.Route & routes) != 0) && (!e.Handled || handledEventsToo))
                    handler(sender, e);
            };
            _classHandlers.Add(filtered);
            return new Subscription(_classHandlers, filtered);
        }

        internal void RaiseClassHandlers(object sender, RoutedEventArgs e)
        {
            if (_classHandlers.Count == 0)
                return;
            Action<object, RoutedEventArgs>[] handlers = _classHandlers.ToArray();
            for (int i = 0; i < handlers.Length; i++)
                handlers[i](sender, e);
        }

        public override string ToString()
        {
            return (OwnerType == null ? string.Empty : OwnerType.Name + ".") + Name;
        }

        sealed class Subscription : IDisposable
        {
            List<Action<object, RoutedEventArgs>> _handlers;
            readonly Action<object, RoutedEventArgs> _handler;

            public Subscription(List<Action<object, RoutedEventArgs>> handlers, Action<object, RoutedEventArgs> handler)
            {
                _handlers = handlers;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_handlers == null)
                    return;
                _handlers.Remove(_handler);
                _handlers = null;
            }
        }
    }

    public class RoutedEvent<TEventArgs> : RoutedEvent
        where TEventArgs : RoutedEventArgs
    {
        public RoutedEvent(string name, RoutingStrategies routingStrategies, Type ownerType)
            : base(name, routingStrategies, ownerType)
        {
        }

        public IDisposable AddClassHandler<TTarget>(
            Action<TTarget, TEventArgs> handler,
            RoutingStrategies routes = RoutingStrategies.Direct | RoutingStrategies.Bubble,
            bool handledEventsToo = false)
            where TTarget : Control
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            return AddClassHandler((sender, e) =>
            {
                TTarget target = sender as TTarget;
                TEventArgs args = e as TEventArgs;
                if (target != null && args != null)
                    handler(target, args);
            }, routes, handledEventsToo);
        }
    }
}
