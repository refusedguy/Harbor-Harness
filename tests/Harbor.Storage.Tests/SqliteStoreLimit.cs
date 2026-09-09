using TUnit.Core.Interfaces;

namespace Harbor.Storage.Tests;

public record SqliteStoreLimit : IParallelLimit
{
    public int Limit => 2;
}
