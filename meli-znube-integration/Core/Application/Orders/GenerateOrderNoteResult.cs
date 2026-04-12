namespace meli_znube_integration.Core.Application.Orders;

/// <summary>
/// Outcome of <see cref="GenerateOrderNoteUseCase.ExecuteAsync"/> for infrastructure orchestration (e.g. post-note buyer message).
/// </summary>
public sealed record GenerateOrderNoteResult(bool NotePublished, string? NoteText);
