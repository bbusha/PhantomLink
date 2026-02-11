using System;
using System.Collections.Generic;
using System.Threading;

namespace PhantomLink.MelonIntegration.Core
{
    public static class ObjectManager
    {
        private static int _nextId = 1000000;
        private static readonly object _lock = new object();
        private static readonly Dictionary<int, WeakReference> _objects = new Dictionary<int, WeakReference>();
        private static readonly Dictionary<int, object> _pinnedObjects = new Dictionary<int, object>();

        public static int TrackObject(object obj) => Track(obj);
        
        public static object GetObject(string idStr)
        {
            if (int.TryParse(idStr, out int id)) return Get(id);
            return null;
        }

        public static object GetObject(int id) => Get(id);

        public static int Track(object obj)
        {
            if (obj == null) return 0;
            
            // TODO: Check if object is already tracked? (Performance trade-off)
            // For now, simple ID generation
            
            lock (_lock)
            {
                int id = _nextId++;
                _objects[id] = new WeakReference(obj);
                return id;
            }
        }

        public static object Get(int id)
        {
            if (id == 0) return null;

            lock (_lock)
            {
                if (_pinnedObjects.TryGetValue(id, out var pinned))
                    return pinned;

                if (_objects.TryGetValue(id, out var weakRef))
                {
                    if (weakRef.IsAlive)
                        return weakRef.Target;
                    
                    _objects.Remove(id);
                }
            }
            return null;
        }

        public static void Pin(int id)
        {
            var obj = Get(id);
            if (obj != null)
            {
                lock (_lock)
                {
                    _pinnedObjects[id] = obj;
                }
            }
        }

        public static void Unpin(int id)
        {
            lock (_lock)
            {
                _pinnedObjects.Remove(id);
            }
        }
        
        public static void Clear()
        {
            lock (_lock)
            {
                _objects.Clear();
                _pinnedObjects.Clear();
            }
        }
    }
}
