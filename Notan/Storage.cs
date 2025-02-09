﻿using Notan.Collections;
using Notan.Reflection;
using Notan.Serialization;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Notan;

//For storing in collections
public abstract class Storage
{
    private protected FastList<int> indexToGeneration = new();

    internal readonly bool Impermanent;

    internal readonly int Id;

    public Type EntityType { get; }

    private protected Storage(Type entityType, int id, bool impermanent)
    {
        EntityType = entityType;
        Id = id;
        Impermanent = impermanent;
    }

    internal bool Alive(int index, int generation)
    {
        return indexToGeneration.Count > index && indexToGeneration[index] == generation;
    }

    internal abstract void Serialize<T>(T serializer) where T : ISerializer<T>;

    internal abstract void Deserialize<T>(T deserializer) where T : IDeserializer<T>;

    internal abstract void LateDeserialize();

    internal abstract void HandleMessage(Client client, MessageType type, int index, int generation);

    internal abstract void FinalizeFrame();
}

//Common
public abstract class Storage<T> : Storage where T : struct, IEntity<T>
{
    private protected FastList<T> entities = new();
    private protected FastList<int> entityToGeneration = new();
    private protected FastList<int> entityToIndex = new();
    private protected FastList<int> indexToEntity = new();

    private protected int nextIndex;
    private protected int remaniningHandles = 0;

    public Associated<T>? Associated { get; set; } = null;

    internal Storage(int id, bool impermanent, Type? associated)
        : base(typeof(T), id, impermanent)
    {
        if (associated != null)
        {
            Associated = (Associated<T>)Activator.CreateInstance(associated)!;
        }
    }

    internal ref T Get(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        return ref entities[indexToEntity[index]];
    }
}

//For servers
public sealed class ServerStorage<T> : Storage<T> where T : struct, IEntity<T>
{
    private FastList<int> destroyedEntityIndices = new();

    private FastList<bool> entityIsDead = new();
    private FastList<FastList<Client>> entityToObservers = new();
    private FastList<Client?> entityToAuthority = new();

    private readonly ClientAuthority authority;

    internal ServerStorage(int id, StorageOptionsAttribute? options)
        : base(id, options != null && options.Impermanent, options?.Associated)
    {
        authority = options == null ? ClientAuthority.None : options.ClientAuthority;
    }

    public ServerHandle<T> Create(T entity)
    {
        var entind = entities.Count;
        entityToObservers.Add(new());
        entityToAuthority.Add(null);
        entityIsDead.Add(false);
        int hndind;
        if (remaniningHandles > 0)
        {
            remaniningHandles--;
            hndind = nextIndex;
            nextIndex = indexToEntity[nextIndex];
            indexToEntity[hndind] = entind;
        }
        else
        {
            hndind = indexToEntity.Count;
            indexToEntity.Add(entind);
            indexToGeneration.Add(0);
        }

        entities.Add(entity);
        entityToIndex.Add(hndind);

        var generation = indexToGeneration[hndind];
        entityToGeneration.Add(generation);
        var handle = new ServerHandle<T>(this, hndind, generation);
        Associated?.PostUpdate(handle, ref Get(hndind, generation));
        return handle;
    }

    internal void Destroy(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            return;
        }

        ref var entity = ref Get(index, generation);
        Associated?.PreUpdate(new(this, index, generation), ref entity);

        foreach (var observer in entityToObservers[indexToEntity[index]].AsSpan())
        {
            observer.Send(Id, MessageType.Destroy, index, generation, ref Unsafe.NullRef<T>());
        }

        entityIsDead[indexToEntity[index]] = true;
        indexToGeneration[index]++;
        destroyedEntityIndices.Add(index);

        Associated?.OnDestroy(new(this, index, generation), ref entity);
    }

    private void Recycle(int index)
    {
        if (remaniningHandles > 0)
        {
            indexToEntity[index] = nextIndex;
        }
        nextIndex = index;
        remaniningHandles++;
    }

    internal void AddObserver(int index, int generation, Client client)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        ref var list = ref entityToObservers[indexToEntity[index]];
        if (list.IndexOf(client) == -1)
        {
            list.Add(client);
            client.Send(Id, MessageType.Create, index, generation, ref Get(index, generation));
        }
    }

    internal void AddObservers(int index, int generation, ReadOnlySpan<Client> clients)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        ref var list = ref entityToObservers[indexToEntity[index]];
        list.EnsureCapacity(list.Count + clients.Length);
        foreach (var client in clients)
        {
            if (list.IndexOf(client) == -1)
            {
                list.Add(client);
                client.Send(Id, MessageType.Create, index, generation, ref Get(index, generation));
            }
        }
    }

    internal void RemoveObserver(int index, int generation, Client client)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        if (entityToObservers[indexToEntity[index]].Remove(client))
        {
            client.Send(Id, MessageType.Destroy, index, generation, ref Unsafe.NullRef<T>());
        }
    }

    internal void UpdateObservers(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        ref var entity = ref Get(index, generation);
        ref var list = ref entityToObservers[indexToEntity[index]];
        var span = list.AsSpan();
        var i = span.Length;
        while (i > 0)
        {
            i--;
            var observer = span[i];
            if (observer.Connected)
            {
                observer.Send(Id, MessageType.Update, index, generation, ref entity);
            }
            else
            {
                list.RemoveAt(i);
            }
        }
    }

    internal void ClearObservers(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        ref var list = ref entityToObservers[indexToEntity[index]];
        var i = list.Count;
        while (i > 0)
        {
            i--;
            list[i].Send(Id, MessageType.Destroy, index, generation, ref Unsafe.NullRef<T>());
            list.RemoveAt(i);
        }
    }

    internal ReadOnlySpan<Client> GetObservers(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        return entityToObservers[indexToEntity[index]].AsSpan();
    }

    internal void SetAuthority(int index, int generation, Client? client)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        entityToAuthority[indexToEntity[index]] = client;
        if (client != null)
        {
            AddObserver(index, generation, client);
        }
    }

    internal Client? GetAuthority(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        return entityToAuthority[indexToEntity[index]];
    }

    public void Run<TSystem>(ref TSystem system) where TSystem : IServerSystem<T>
    {
        var i = entities.Count;
        while (i > 0)
        {
            i--;
            if (!entityIsDead[i])
            {
                var index = entityToIndex[i];
                system.Work(new(this, index, entityToGeneration[i]), ref entities[i]);
            }
        }
    }

    public TSystem Run<TSystem>(TSystem system) where TSystem : IServerSystem<T>
    {
        Run(ref system);
        return system;
    }

    internal override void Serialize<TSer>(TSer serializer)
    {
        serializer.ArrayBegin();
        var i = 0;
        foreach (var index in indexToEntity.AsSpan())
        {
            serializer.ArrayNext().ObjectBegin();
            serializer.ObjectNext("gen").Serialize(indexToGeneration[i]);
            if (entityToIndex.Count > index && entityToIndex[index] == i)
            {
                entities[index].Serialize(serializer.ObjectNext("entity"));
            }
            else
            {
                serializer.ObjectNext("dead").Serialize(true);
            }
            serializer.ObjectEnd();
            i++;
        }
        serializer.ArrayEnd();
    }

    internal override void Deserialize<TDeser>(TDeser deserializer)
    {
        destroyedEntityIndices.Clear();
        remaniningHandles = 0;

        entities.Clear();
        entityToIndex.Clear();
        entityIsDead.Clear();
        entityToObservers.Clear();
        entityToAuthority.Clear();
        entityToGeneration.Clear();
        indexToEntity.Clear();
        indexToGeneration.Clear();

        deserializer.ArrayBegin();
        var i = 0;
        while (deserializer.ArrayTryNext())
        {
            deserializer.ObjectBegin();

            var dead = false;
            var t = new T();
            var gen = -1;
            while (deserializer.ObjectTryNext(out var key))
            {
                if (key == "gen")
                {
                    deserializer.Deserialize(ref gen);
                }
                else if (key == "dead")
                {
                    Unsafe.SkipInit(out bool dummy);
                    deserializer.Deserialize(ref dummy);
                    dead = true;
                }
                else if (key == "entity")
                {
                    t.Deserialize(deserializer);
                }
                else
                {
                    throw new IOException();
                }
            }

            indexToGeneration.Add(gen);

            if (!dead)
            {
                entities.Add(t);
                entityToIndex.Add(i);
                entityIsDead.Add(false);
                entityToObservers.Add(new());
                entityToAuthority.Add(null);
                entityToGeneration.Add(gen);
                indexToEntity.Add(entities.Count - 1);
            }
            else
            {
                indexToEntity.Add(0);
                Recycle(i);
            }
            i++;
        }
    }

    internal override void LateDeserialize()
    {
        var i = 0;
        foreach (ref var entity in entities.AsSpan())
        {
            Associated?.PostUpdate(new(this, entityToIndex[i], entityToGeneration[i]), ref entity);
            i++;
        }
    }

    internal sealed override void HandleMessage(Client client, MessageType type, int index, int generation)
    {
        switch (type)
        {
            case MessageType.Create:
                if (authority == ClientAuthority.Unauthenticated || (authority == ClientAuthority.Authenticated && client.Authenticated))
                {
                    var entity = new T();
                    client.ReadIntoEntity(ref entity);
                    var handle = Create(entity);
                    SetAuthority(handle.Index, handle.Generation, client);
                }
                else
                {
                    Unsafe.SkipInit(out T entity);
                    client.ReadIntoEntity(ref entity);
                }
                break;
            case MessageType.Update:
                if (Alive(index, generation) && entityToAuthority[indexToEntity[index]] == client)
                {
                    ref var entity = ref Get(index, generation);
                    Associated?.PreUpdate(new(this, index, generation), ref entity);
                    client.ReadIntoEntity(ref entity);
                    Associated?.PostUpdate(new(this, index, generation), ref entity);
                }
                else
                {
                    Unsafe.SkipInit(out T entity);
                    client.ReadIntoEntity(ref entity);
                }
                break;
            case MessageType.Destroy:
                if (Alive(index, generation) && entityToAuthority[indexToEntity[index]] == client)
                {
                    Destroy(index, generation);
                }
                break;
        }
    }

    internal override void FinalizeFrame()
    {
        foreach (var index in destroyedEntityIndices.AsSpan())
        {
            var entityIndex = indexToEntity[index];
            entityToObservers.RemoveAt(entityIndex);
            entityToAuthority.RemoveAt(entityIndex);
            entityIsDead.RemoveAt(entityIndex);
            entityToGeneration.RemoveAt(entityIndex);
            entities.RemoveAt(entityIndex);
            indexToEntity[entityToIndex[^1]] = entityIndex;
            entityToIndex.RemoveAt(entityIndex);
            Recycle(index);
        }
        destroyedEntityIndices.Clear();
    }
}

//For clients
public sealed class ClientStorage<T> : Storage<T> where T : struct, IEntity<T>
{
    private readonly Client server;

    private FastList<int> forgottenEntityIndices = new();
    private FastList<bool> entityIsForgotten = new();

    internal ClientStorage(int id, StorageOptionsAttribute? options, Client server)
        : base(id, options != null && options.Impermanent, options?.Associated)
    {
        this.server = server;
    }

    public void RequestCreate(T entity)
    {
        server.Send(Id, MessageType.Create, 0, 0, ref entity);
    }

    internal void RequestUpdate(int index, int generation, ref T entity)
    {
        server.Send(Id, MessageType.Update, index, generation, ref entity);
    }

    internal void RequestDestroy(int index, int generation)
    {
        server.Send(Id, MessageType.Destroy, index, generation, ref Unsafe.NullRef<T>());
    }

    internal void Forget(int index, int generation)
    {
        if (!Alive(index, generation))
        {
            NotanException.Throw("Entity is not alive.");
        }
        indexToGeneration[index] = -1;
        entityIsForgotten[indexToEntity[index]] = true;
        forgottenEntityIndices.Add(index);
    }

    internal override void HandleMessage(Client client, MessageType type, int index, int generation)
    {
        switch (type)
        {
            case MessageType.Create:
                {
                    var entid = entityToIndex.Count;
                    entityToIndex.Add(index);
                    var entity = new T();
                    client.ReadIntoEntity(ref entity);
                    entities.Add(entity);
                    entityIsForgotten.Add(false);
                    indexToEntity.EnsureSize(index + 1);
                    indexToEntity[index] = entid;
                    indexToGeneration.EnsureSize(index + 1);
                    indexToGeneration[index] = generation;
                    entityToGeneration.Add(generation);
                    Associated?.PostUpdate(new(this, index, generation), ref Get(index, generation));
                }
                break;
            case MessageType.Update:
                if (Alive(index, generation))
                {
                    ref var entity = ref Get(index, generation);
                    Associated?.PreUpdate(new(this, index, generation), ref entity);
                    client.ReadIntoEntity(ref entity);
                    Associated?.PostUpdate(new(this, index, generation), ref entity);
                }
                else
                {
                    Unsafe.SkipInit(out T entity);
                    client.ReadIntoEntity(ref entity);
                }
                break;
            case MessageType.Destroy:
                if (Alive(index, generation))
                {
                    Associated?.PreUpdate(new(this, index, generation), ref Get(index, generation));
                    indexToGeneration[index] = -1;
                    DestroyInternal(index);
                }
                break;
        }
    }

    private void DestroyInternal(int index)
    {
        var entityIndex = indexToEntity[index];
        entityIsForgotten.RemoveAt(entityIndex);
        entities.RemoveAt(entityIndex);
        indexToEntity[entityToIndex[^1]] = entityIndex;
        entityToIndex.RemoveAt(entityIndex);
        entityToGeneration.RemoveAt(entityIndex);
    }

    public void Run<TSystem>(ref TSystem system) where TSystem : IClientSystem<T>
    {
        var i = entities.Count;
        while (i > 0)
        {
            i--;
            if (!entityIsForgotten[i])
            {
                system.Work(new(this, entityToIndex[i], entityToGeneration[i]), ref entities[i]);
            }
        }
    }

    public TSystem Run<TSystem>(TSystem system) where TSystem : IClientSystem<T>
    {
        Run(ref system);
        return system;
    }

    internal override void LateDeserialize() => throw new NotImplementedException();

    internal override void Serialize<TSer>(TSer serializer) => throw new NotImplementedException();

    internal override void Deserialize<TDeser>(TDeser deserializer) => throw new NotImplementedException();

    internal override void FinalizeFrame()
    {
        foreach (var index in forgottenEntityIndices.AsSpan())
        {
            entityIsForgotten[indexToEntity[index]] = false;
            DestroyInternal(index);
        }
        forgottenEntityIndices.Clear();
    }
}
