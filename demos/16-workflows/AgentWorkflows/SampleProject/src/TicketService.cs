namespace Acme.Tickets;

public sealed class TicketService(ITicketRepository repository)
{
    public async Task<Ticket> OpenAsync(string title, string requestedBy, CancellationToken cancellationToken)
    {
        // TODO: walidacja długości tytułu i reguł dotyczących wulgaryzmów.
        var ticket = new Ticket(Guid.NewGuid(), title, requestedBy, DateTimeOffset.UtcNow, TicketStatus.Open);
        await repository.SaveAsync(ticket, cancellationToken);
        return ticket;
    }
}

public interface ITicketRepository
{
    Task SaveAsync(Ticket ticket, CancellationToken cancellationToken);
}

public sealed record Ticket(Guid Id, string Title, string RequestedBy, DateTimeOffset CreatedAt, TicketStatus Status);

public enum TicketStatus
{
    Open = 0,
    Closed = 1,
}
