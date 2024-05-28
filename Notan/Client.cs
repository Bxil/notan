using Notan.Serialization.Binary;
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Notan;

public sealed class Client
{
    private readonly TcpClient tcpClient;
    private readonly MemoryStream outgoing;
    private readonly MemoryStream incoming;
    private long IncomingAvailable => incoming.Length - incoming.Position;
    private readonly SslStream stream;
    private readonly BinaryWriter writer;
    private readonly BinaryReader reader;

    private readonly CancellationTokenSource readCts = new();
    private readonly ReadTask readTask;

    private readonly BinarySerializer serializer;
    private readonly BinaryDeserializer deserializer;

    private static readonly UTF8Encoding encoding = new(false);

    public int Id { get; }
    public bool Authenticated { get; set; } = false;
    public bool Connected => tcpClient.Connected;
    public DateTimeOffset LastCommunicated { get; private set; }
    public DateTimeOffset LoginTime { get; }
    public IPEndPoint IPEndPoint { get; }

    internal Client(World world, TcpClient tcpClient, SslStream stream, int id)
    {
        this.tcpClient = tcpClient;
        IPEndPoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint!;
        Id = id;

        outgoing = new MemoryStream();
        incoming = new MemoryStream();

        LastCommunicated = DateTimeOffset.Now;
        LoginTime = LastCommunicated;

        this.stream = stream;
        tcpClient.NoDelay = true;

        writer = new BinaryWriter(outgoing, encoding, true);
        reader = new BinaryReader(incoming, encoding, true);

        serializer = new(outgoing);
        deserializer = new(world, incoming);

        lengthPrefix = 0;

        readTask = new ReadTask(stream, readCts.Token);
    }

    public void Disconnect()
    {
        readCts.Cancel();
        tcpClient.Close();
    }

    internal void Flush()
    {
        if (outgoing.Position > 0)
        {
            outgoing.WriteTo(stream);
            outgoing.SetLength(0);
            LastCommunicated = DateTimeOffset.Now;
        }
    }

    internal void Send<T>(int storageid, MessageType type, int index, int generation, ref T entity) where T : struct, IEntity<T>
    {
        //Leave space for the length prefix
        var prefixPosition = (int)outgoing.Position;
        outgoing.Position += sizeof(int);

        writer.Write(storageid);
        writer.Write((byte)type);
        writer.Write(index);
        writer.Write(generation);

        switch (type)
        {
            case MessageType.Create:
            case MessageType.Update:
                entity.Serialize(serializer);
                break;
            case MessageType.Destroy:
                break;
        }

        var endPosition = (int)outgoing.Position;
        outgoing.Position = prefixPosition;
        writer.Write(endPosition - prefixPosition - sizeof(int));
        outgoing.Position = endPosition;
    }

    private int lengthPrefix;

    // After this function returns true an immediate read must follow.
    internal bool CanRead()
    {
        if (CanReadInner())
        {
            return true;
        }

        // If we can't read maybe we need to fetch data from the socket first.
        readTask.CopyTo(incoming);

        return CanReadInner();
    }

    private bool CanReadInner()
    {
        if (lengthPrefix == 0) //We are yet to read the prefix,
        {
            if (IncomingAvailable < sizeof(int)) //but it is unavailable.
            {
                return false;
            }

            lengthPrefix = reader.ReadInt32();
        }
        return IncomingAvailable >= lengthPrefix;
    }

    internal int ReadHeader(out MessageType type, out int index, out int generation)
    {
        LastCommunicated = DateTimeOffset.Now;

        var storageid = reader.ReadInt32();
        type = (MessageType)reader.ReadByte();
        index = reader.ReadInt32();
        generation = reader.ReadInt32();

        lengthPrefix = 0;
        return storageid;
    }

    internal void ReadIntoEntity<T>(ref T entity) where T : struct, IEntity<T>
    {
        entity.Deserialize(deserializer);
    }

    private sealed class ReadTask
    {
        private readonly SslStream stream;
        private readonly MemoryStream memory = new();

        public ReadTask(SslStream stream, CancellationToken cancellationToken)
        {
            this.stream = stream;
            _ = ReadAsync(cancellationToken);
        }

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                lock (memory)
                {
                    var old = memory.Position;
                    memory.Position = memory.Length;
                    memory.Write(buffer.AsSpan(0, read));
                    memory.Position = old;
                }
            }
        }

        public void CopyTo(MemoryStream incoming)
        {
            var buffer = incoming.GetBuffer();

            // Override the read content in the beginning with the unread content.
            var amount = (int)(incoming.Length - incoming.Position);
            buffer.AsSpan((int)incoming.Position, amount).CopyTo(buffer.AsSpan(0, amount));
            incoming.Position = amount;
            incoming.SetLength(amount);

            lock (memory)
            {
                memory.CopyTo(incoming);
                incoming.Position = 0;
                memory.Position = 0;
                memory.SetLength(0);
            }
        }
    }
}
