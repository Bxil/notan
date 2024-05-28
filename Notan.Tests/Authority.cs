using Microsoft.VisualStudio.TestTools.UnitTesting;
using Notan.Reflection;
using Notan.Tests.Utility;
using System.Reflection;

namespace Notan.Tests;

[TestClass]
public class Authority
{
    private ServerWorld serverWorld;
    private ClientWorld clientWorld1;
    private ClientWorld clientWorld2;

    [TestInitialize]
    public void Init()
    {
        serverWorld = new ServerWorld(0);
        serverWorld.AddStorages(Assembly.GetExecutingAssembly());

        var client1Task = ClientWorld.StartAsync("localhost", serverWorld.EndPoint.Port);

        var client2Task = ClientWorld.StartAsync("localhost", serverWorld.EndPoint.Port);

        while (!client1Task.IsCompleted || !client2Task.IsCompleted)
        {
            _ = serverWorld.Tick();
        }
        Assert.IsTrue(client1Task.IsCompletedSuccessfully);
        Assert.IsTrue(client2Task.IsCompletedSuccessfully);

        clientWorld1 = client1Task.Result;
        clientWorld1.AddStorages(Assembly.GetExecutingAssembly());
        clientWorld2 = client2Task.Result;
        clientWorld2.AddStorages(Assembly.GetExecutingAssembly());
    }

    [TestCleanup]
    public void End()
    {
        serverWorld.Exit();
        _ = serverWorld.Tick();
        clientWorld1.Exit();
        _ = clientWorld1.Tick();
        clientWorld2.Exit();
        _ = clientWorld2.Tick();

        serverWorld.Dispose();
    }

    [TestMethod]
    public void Updates()
    {
        var storage1 = clientWorld1.GetStorage<ByteEntity>();
        var storage2 = clientWorld2.GetStorage<ByteEntity>();

        storage1.RequestCreate(new ByteEntity { Value = 1 });
        storage2.RequestCreate(new ByteEntity { Value = 3 });

        _ = clientWorld1.Tick();
        _ = clientWorld2.Tick();

        AssertUntil.True(() =>
        {
            _ = serverWorld.Tick(); //2, 4

            return 6 == serverWorld.GetStorage<ByteEntity>().Run(new SumSystem()).Sum;
        });

        _ = clientWorld1.Tick(); //3
        _ = clientWorld2.Tick(); //5

        _ = storage1.Run(new IncSystem()); //4
        _ = storage2.Run(new IncSystem()); //6

        AssertUntil.True(() =>
        {
            _ = clientWorld1.Tick();
            _ = clientWorld2.Tick();

            _ = serverWorld.Tick(); //5, 7

            return 12 == serverWorld.GetStorage<ByteEntity>().Run(new SumSystem()).Sum;
        });

        _ = serverWorld.GetStorage<ByteEntity>().Run(new DestroySystem());

        _ = serverWorld.Tick();

        AssertUntil.True(() =>
        {
            _ = clientWorld1.Tick();
            _ = clientWorld2.Tick();

            return 0 == storage1.Run(new SumSystem()).Sum && 0 == storage2.Run(new SumSystem()).Sum;
        });
    }

    struct IncSystem : IClientSystem<ByteEntity>
    {
        void IClientSystem<ByteEntity>.Work(ClientHandle<ByteEntity> handle, ref ByteEntity entity)
        {
            handle.RequestUpdate(new ByteEntity { Value = (byte)(entity.Value + 1) });
        }
    }

    struct SumSystem : IServerSystem<ByteEntity>, IClientSystem<ByteEntity>
    {
        public int Sum;

        void IServerSystem<ByteEntity>.Work(ServerHandle<ByteEntity> handle, ref ByteEntity entity)
        {
            Sum += entity.Value;
        }

        void IClientSystem<ByteEntity>.Work(ClientHandle<ByteEntity> handle, ref ByteEntity entity)
        {
            Sum += entity.Value;
        }
    }

    struct DestroySystem : IServerSystem<ByteEntity>
    {
        void IServerSystem<ByteEntity>.Work(ServerHandle<ByteEntity> handle, ref ByteEntity entity)
        {
            handle.Destroy();
        }
    }
}
