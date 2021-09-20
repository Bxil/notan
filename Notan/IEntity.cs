﻿using Notan.Serialization;

namespace Notan
{
    public interface IEntity<T> where T : struct, IEntity<T>
    {
        void Serialize<TSer>(TSer serializer) where TSer : ISerializer<TSer>;
        void Deserialize<TDeser>(Key key, TDeser deserializer) where TDeser : IDeserializer<TDeser>;
        void LateDeserialize(StrongHandle<T> handle) { }
        void LateCreate(StrongHandle<T> handle) { }
        void OnDestroy(StrongHandle<T> handle) { }
    }
}
