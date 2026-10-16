namespace Comven.Application.Models;

public sealed record StatementFilter(
    string CustomerExternalId,
    DateOnly? From = null,
    DateOnly? To = null);