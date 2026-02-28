namespace meli_znube_integration.Models.Dtos;

public class OrderQueueMessage
{
    public string? Topic { get; set; }
    public string? Resource { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
