namespace FloatDesk.Core.Models;

public sealed record NoteSummary(
    Guid Id,
    string Title,
    long Revision,
    DateTimeOffset UpdatedUtc,
    bool Deleted);

public sealed record NoteDocument(
    Guid Id,
    string Title,
    string Body,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? DeletedUtc);
