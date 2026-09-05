namespace Harbor.E2E.Framework;

public record MockServerLimit : IParallelLimit
{
    public int Limit => 4;
}
