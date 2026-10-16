namespace Comven.Application.Models;

public sealed record InvoiceFilter(
    string? CustomerExternalId,
    DateOnly? From,
    DateOnly? To);