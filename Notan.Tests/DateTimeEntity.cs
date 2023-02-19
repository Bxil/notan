﻿using Notan.Serialization;
using System;
using System.Runtime.CompilerServices;

namespace Notan.Tests;

[GenerateSerialization]
public partial struct DateTimeEntity : IEntity<DateTimeEntity>
{
    [Serialize("Timestamp")]
    public DateTime DateTime;
}

public static class DateTimeSerialization
{
    public static void Serialize<T>(this T serializer, in DateTime dateTime) where T : ISerializer<T>
    {
        serializer.Serialize(dateTime.Ticks);
    }

    public static void Deserialize<T>(this T deserializer, ref DateTime dateTime) where T : IDeserializer<T>
    {
        Unsafe.SkipInit(out long ticks);
        deserializer.Deserialize(ref ticks);
        dateTime = new DateTime(ticks);
    }
}