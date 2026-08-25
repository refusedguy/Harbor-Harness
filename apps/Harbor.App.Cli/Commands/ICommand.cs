namespace Harbor.App.Cli.Commands;

public interface ICommand
{
    string Name { get; }
    Task<int> ExecuteAsync(string[] args, CancellationToken ct = default);
}
