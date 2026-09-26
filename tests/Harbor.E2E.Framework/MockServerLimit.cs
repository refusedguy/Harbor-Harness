using TUnit.Core.Interfaces;

namespace Harbor.E2E.Framework;

public record MockServerLimit : IParallelLimit
{
    public int Limit => 4;
}
