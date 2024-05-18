using System;

#nullable enable

namespace Notan.Serialization;

[AttributeUsage(AttributeTargets.Struct)]
internal sealed class GenerateSerializationAttribute : Attribute {}

[AttributeUsage(AttributeTargets.Field)]
internal sealed class SerializeAttribute : Attribute
{
    public string? Name { get; }

    public SerializeAttribute(string? name = null)
    {
        Name = name;
    }
}
