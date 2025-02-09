﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Notan.Reflection;
using Notan.Tests.Utility;
using System;
using System.IO;
using System.Reflection;

namespace Notan.Tests;

[TestClass]
public class Observing
{
    private ServerWorld serverWorld;
    private ClientWorld clientWorld;

    [TestInitialize]
    public void Init()
    {
        serverWorld = new ServerWorld(0);
        serverWorld.AddStorages(Assembly.GetExecutingAssembly());

        var clientTask = ClientWorld.StartAsync("localhost", serverWorld.EndPoint.Port);
        while (!clientTask.IsCompleted)
        {
            _ = serverWorld.Tick();
        }
        Assert.IsTrue(clientTask.IsCompletedSuccessfully);
        clientWorld = clientTask.Result;
        clientWorld.AddStorages(Assembly.GetExecutingAssembly());
    }

    [TestCleanup]
    public void End()
    {
        serverWorld.Exit();
        _ = serverWorld.Tick();
    }

    //TODO: make this test a lot more precise
    [TestMethod]
    public void AddAndDisconnect()
    {
        clientWorld.GetStorage<ByteEntity>().RequestCreate(new ByteEntity());
        _ = clientWorld.Tick();
        _ = serverWorld.Tick();
        var system = new ByteSystem();
        serverWorld.GetStorage<ByteEntity>().Run(ref system);
        clientWorld.Exit();
        _ = clientWorld.Tick();
        _ = serverWorld.Tick();
        serverWorld.GetStorage<ByteEntity>().Run(ref system);
        _ = serverWorld.Tick();
        serverWorld.GetStorage<ByteEntity>().Run(ref system);
    }

    [TestMethod]
    public void Malformed()
    {
        clientWorld.GetStorage<MalformedEntity>().RequestCreate(new MalformedEntity());
        _ = clientWorld.Tick();
        AssertUntil.True(() =>
        {
            _ = serverWorld.Tick();
            return 0 == serverWorld.Clients.Length;
        });
    }

    [TestMethod]
    public void MalformedWrong()
    {
        clientWorld.GetStorage<MalformedEntityWrong>().RequestCreate(new MalformedEntityWrong());
        AssertUntil.Throw(() =>
        {
            _ = clientWorld.Tick();
            _ = Assert.ThrowsException<Exception>(() => _ = serverWorld.Tick());
        });
    }

    [TestMethod]
    public void StayDead()
    {
        var byteStorage = serverWorld.GetStorage<ByteEntity>();
        var handleStorage = serverWorld.GetStorage<HandleEntity>();
        var byteEntity = byteStorage.Create(new());
        byteEntity.AddObserver(serverWorld.Clients[0]);
        var handle1 = handleStorage.Create(new HandleEntity { Value = (Handle<ByteEntity>)byteEntity });
        Assert.AreEqual(0, handle1.Index);
        Assert.AreEqual(0, handle1.Generation);
        Assert.IsTrue(new Maybe<HandleEntity>(handle1).Alive());
        var handle2 = handleStorage.Create(new HandleEntity { Value = default });
        Assert.AreEqual(1, handle2.Index);
        Assert.AreEqual(0, handle2.Generation);
        handle1.AddObserver(serverWorld.Clients[0]);
        handle2.AddObserver(serverWorld.Clients[0]);
        Assert.IsTrue(new Maybe<HandleEntity>(handle2).Alive());
        _ = serverWorld.Tick();

        AssertUntil.True(() =>
        {
            _ = clientWorld.Tick();
            return 1 == clientWorld.GetStorage<HandleEntity>().Run(new AliveCountSystem()).Count;
        });
    }

    struct ByteSystem : IServerSystem<ByteEntity>
    {
        public void Work(ServerHandle<ByteEntity> handle, ref ByteEntity entity)
        {
            handle.UpdateObservers();
        }
    }

    struct AliveCountSystem : IClientSystem<HandleEntity>
    {
        public int Count;

        void IClientSystem<HandleEntity>.Work(ClientHandle<HandleEntity> handle, ref HandleEntity entity)
        {
            if (entity.Value.Alive())
            {
                Count++;
            }
        }
    }

    [StorageOptions(ClientAuthority = ClientAuthority.Unauthenticated)]
    public struct MalformedEntity : IEntity<MalformedEntity>
    {
        void ISerializable.Deserialize<TDeser>(TDeser deserializer)
        {
            throw new IOException();
        }

        void ISerializable.Serialize<TSer>(TSer serializer)
        {
            serializer.ObjectNext("a").Serialize("b");
        }
    }

    [StorageOptions(ClientAuthority = ClientAuthority.Unauthenticated)]
    public struct MalformedEntityWrong : IEntity<MalformedEntityWrong>
    {
        void ISerializable.Deserialize<TDeser>(TDeser deserializer)
        {
            throw new Exception();
        }

        void ISerializable.Serialize<TSer>(TSer serializer)
        {
            serializer.ObjectNext("a").Serialize("b");
        }
    }

}
