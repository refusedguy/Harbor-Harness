namespace Harbor.App.Cli.Commands;

public interface ICommand
{
    public string Name { get; }
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default);
}
