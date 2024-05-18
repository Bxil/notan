using Notan.Serialization;

namespace Notan.Tests;

[GenerateSerialization]
partial struct WeakHandleEntity : IEntity<WeakHandleEntity>
{
    [Serialize]
    private Handle Handle;
}
