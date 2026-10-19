using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Network ids for host-owned objects. The host assigns; the client maps ids to its replicas.
    /// Ids are never reused within a session.
    /// </summary>
    internal static class NetIds
    {
        static uint next = 1;
        static readonly Dictionary<int, uint> byInstance = new Dictionary<int, uint>();
        static readonly Dictionary<uint, Object> byId = new Dictionary<uint, Object>();

        public static uint Assign(Object o)
        {
            int k = o.GetInstanceID();
            if (byInstance.TryGetValue(k, out var id)) return id;
            id = next++;
            byInstance[k] = id;
            byId[id] = o;
            return id;
        }

        public static uint Of(Object o) => o != null && byInstance.TryGetValue(o.GetInstanceID(), out var id) ? id : 0;

        public static bool Has(Object o) => o != null && byInstance.ContainsKey(o.GetInstanceID());

        public static void Bind(uint id, Object o)
        {
            byId[id] = o;
            byInstance[o.GetInstanceID()] = id;
        }

        public static T Get<T>(uint id) where T : Object => byId.TryGetValue(id, out var o) ? o as T : null;

        public static void Forget(uint id)
        {
            if (byId.TryGetValue(id, out var o))
            {
                byId.Remove(id);
                if (o != null) byInstance.Remove(o.GetInstanceID());
            }
        }

        /// <summary>Ids whose objects Unity has destroyed (dead references).</summary>
        public static List<uint> Dead()
        {
            var list = new List<uint>();
            foreach (var kv in byId) if (kv.Value == null) list.Add(kv.Key);
            return list;
        }

        public static IEnumerable<KeyValuePair<uint, Object>> All => byId;

        public static void Clear()
        {
            byInstance.Clear();
            byId.Clear();
        }
    }
}
