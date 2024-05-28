using Notan.Collections;
using Notan.Reflection;
using Notan.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Notan;

public abstract class World
{
    private protected readonly Dictionary<string, Storage> TypeNameToStorage = [];
    internal FastList<Storage> IdToStorage = new(); //Element 0 is always null.

    private protected static string certificateName = "Notan";

    public IPEndPoint EndPoint { get; protected set; } = null!;

    private protected World()
    {
        IdToStorage.Add(null!);
    }

    public Storage<T> GetStorage<T>() where T : struct, IEntity<T>
    {
        return Unsafe.As<Storage<T>>(TypeNameToStorage[typeof(T).ToString()]);
    }

    private protected volatile bool exit = false;
    public void Exit() => exit = true;

    public abstract void AddStorage<T>(StorageOptionsAttribute? options = default) where T : struct, IEntity<T>;
}

public sealed class ServerWorld : World, IDisposable
{
    private readonly TcpListener listener;

    private FastList<(TcpClient, SslStream, Task)> clientsPendingSslAuth = new();

    private FastList<Client> clients = new();
    public Span<Client> Clients => clients.AsSpan();

    private int nextClientId = 0;
    private readonly Stack<int> clientIds = new();

    private readonly X509Certificate2 certificate;

    public ServerWorld(int port) : this(port, CreateTemporaryCertificate()) { }

    public ServerWorld(int port, X509Certificate2 certificate)
    {
        this.certificate = certificate;

        listener = TcpListener.Create(port);
        listener.Start();
        EndPoint = (IPEndPoint)listener.LocalEndpoint;
    }

    public void Dispose()
    {
        Exit();
        certificate.Dispose();
    }

    public override void AddStorage<T>(StorageOptionsAttribute? options = default)
    {
        var newstorage = new ServerStorage<T>(IdToStorage.Count, options);
        TypeNameToStorage.Add(typeof(T).ToString(), newstorage);
        IdToStorage.Add(newstorage);
    }

    public new ServerStorage<T> GetStorage<T>() where T : struct, IEntity<T>
    {
        return Unsafe.As<ServerStorage<T>>(base.GetStorage<T>());
    }

    public bool Tick()
    {
        foreach (var storage in IdToStorage.AsSpan()[1..])
        {
            storage.FinalizeFrame();
        }

        if (exit)
        {
            listener.Stop();
            return false;
        }

        while (listener.Pending())
        {
            var tcpClient = listener.AcceptTcpClient();
            var stream = new SslStream(tcpClient.GetStream());
            var task = stream.AuthenticateAsServerAsync(certificate);
            clientsPendingSslAuth.Add((tcpClient, stream, task));
        }

        var i = clientsPendingSslAuth.Count;
        while (i > 0)
        {
            i--;
            var (tcpClient, stream, task) = clientsPendingSslAuth[i];
            if (!task.IsCompleted)
            {
                continue;
            }
            if (task.IsCompletedSuccessfully)
            {
                if (!clientIds.TryPop(out var id))
                {
                    id = nextClientId;
                    nextClientId++;
                }
                clients.Add(new(this, tcpClient, stream, id));
            }
            clientsPendingSslAuth.RemoveAt(i);
        }

        i = clients.Count;
        while (i > 0)
        {
            i--;
            var client = clients[i];
            try
            {
                const int messageReadMaximum = 10;
                var messagesRead = 0;
                while (messagesRead < messageReadMaximum && client.CanRead())
                {
                    var id = client.ReadHeader(out var type, out var index, out var generation);
                    if (id <= 0 || id >= IdToStorage.Count)
                    {
                        throw new IOException();
                    }
                    IdToStorage[id].HandleMessage(client, type, index, generation);

                    messagesRead++;
                }
            }
            catch (IOException)
            {
                DeleteClient(client);
            }
        }

        i = clients.Count;
        while (i > 0)
        {
            i--;
            var client = clients[i];
            var delete = !client.Connected;
            if (!delete)
            {
                try
                {
                    client.Flush();
                }
                catch (IOException)
                {
                    delete = true;
                }
            }

            if (delete)
            {
                DeleteClient(client);
            }
        }

        return true;
    }

    private void DeleteClient(Client client)
    {
        clientIds.Push(client.Id);
        client.Disconnect();
        _ = clients.Remove(client);
    }

    public void Serialize<T>(T serializer) where T : ISerializer<T>
    {
        serializer.ObjectBegin();
        serializer.ObjectNext("HandleIdentifiers").ObjectBegin();
        foreach (var pair in TypeNameToStorage)
        {
            if (!pair.Value.Impermanent)
            {
                serializer.ObjectNext(pair.Key).Serialize(pair.Value.Id);
            }
        }
        serializer.ObjectEnd();
        serializer.ObjectNext("Entities").ObjectBegin();
        foreach (var pair in TypeNameToStorage)
        {
            if (!pair.Value.Impermanent)
            {
                pair.Value.Serialize(serializer.ObjectNext(pair.Key));
            }
        }
        serializer.ObjectEnd();
        serializer.ObjectEnd();
    }

    public void Deserialize<T>(T deserializer) where T : IDeserializer<T>
    {
        var idToStorageSaved = IdToStorage;

        IdToStorage = new();

        deserializer.ObjectBegin();
        while (deserializer.ObjectTryNext(out var key))
        {
            if (key == "HandleIdentifiers")
            {
                deserializer.ObjectBegin();
                while (deserializer.ObjectTryNext(out key))
                {
                    var storage = TypeNameToStorage[key.ToString()];
                    Unsafe.SkipInit(out int id);
                    deserializer.Deserialize(ref id);
                    IdToStorage.EnsureSize(id + 1);
                    IdToStorage[id] = storage;
                }
            }
            else if (key == "Entities")
            {
                deserializer.ObjectBegin();
                while (deserializer.ObjectTryNext(out key))
                {
                    TypeNameToStorage[key.ToString()!].Deserialize(deserializer);
                }
                foreach (var pair in TypeNameToStorage)
                {
                    pair.Value.LateDeserialize();
                }
            }
            else
            {
                throw new IOException();
            }
        }

        IdToStorage = idToStorageSaved;
    }

    private static X509Certificate2 CreateTemporaryCertificate()
    {
        var rsa = RSA.Create();
        var certtmp = new CertificateRequest($"cn={certificateName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now + TimeSpan.FromDays(1));
        // The certificate must be reimported as Windows does not allow ephemeral keys
        // https://stackoverflow.com/questions/75890480
        return new X509Certificate2(certtmp.Export(X509ContentType.Pkcs12));
    }
}

public sealed class ClientWorld : World
{
    private readonly Client server;

    private ClientWorld(TcpClient server, SslStream stream)
    {
        this.server = new Client(this, server, stream, 0);
        EndPoint = (IPEndPoint)server.Client.LocalEndPoint!;
    }

    public static async Task<ClientWorld> StartAsync(string host, int port) => await StartAsync(host, port, certificateName);

    public static async Task<ClientWorld> StartAsync(string host, int port, string serverName)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var stream = new SslStream(client.GetStream(), false, ValidateCertificate);
        await stream.AuthenticateAsClientAsync(serverName);
        return new ClientWorld(client, stream);

        //TODO
        static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) => true;
    }

    public override void AddStorage<T>(StorageOptionsAttribute? options = default)
    {
        Storage newstorage = new ClientStorage<T>(IdToStorage.Count, options, server);
        TypeNameToStorage.Add(typeof(T).ToString(), newstorage);
        IdToStorage.Add(newstorage);
    }

    public new ClientStorage<T> GetStorage<T>() where T : struct, IEntity<T>
    {
        return Unsafe.As<ClientStorage<T>>(base.GetStorage<T>());
    }

    public bool Tick()
    {
        foreach (var storage in IdToStorage.AsSpan()[1..])
        {
            storage.FinalizeFrame();
        }

        if (exit)
        {
            server.Disconnect();
            return false;
        }

        try
        {
            server.Flush();
        }
        catch (IOException)
        {
            return false;
        }

        while (server.CanRead())
        {
            IdToStorage[server.ReadHeader(out var type, out var index, out var generation)].HandleMessage(server, type, index, generation);
        }

        return true;
    }
}
