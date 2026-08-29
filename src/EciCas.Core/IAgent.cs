namespace EciCas.Core;

public interface IAgent
{
    string Name { get; }
    IReadOnlyCollection<string> Subscriptions { get; }
    Task HandleAsync(Envelope envelope, CancellationToken cancellationToken);
}
