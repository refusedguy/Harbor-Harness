using TUnit.Core;

namespace Harbor.Storage.Tests;

public record SqliteStoreLimit : IParallelLimit
{
    public int Limit => 2;
}
