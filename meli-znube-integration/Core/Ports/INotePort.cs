namespace meli_znube_integration.Core.Ports;

public interface INotePort
{
    Task<bool> PublishNoteAsync(string orderId, string noteContent, CancellationToken cancellationToken = default);
}
