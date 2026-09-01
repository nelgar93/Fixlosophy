namespace Fixlosophy.Services;

/// What will happen to one row of the file. Every row lands in exactly one of these,
/// and the preview shows the counts before anything is written.
public enum ImportOutcome
{
    /// No account with this email — a customer will be created.
    Create,

    /// The account exists and the file fills at least one field that is currently blank.
    Update,

    /// The account exists and there is nothing to add.
    Unchanged,

    /// The row can't be used. <see cref="ImportRow.Problem"/> says why.
    Invalid,

    /// An earlier row in the same file already used this email. The first one wins.
    DuplicateInFile
}

/// <param name="LineNumber">1-based line in the file, header included, so a reported
/// problem can be found by scrolling to that line in a spreadsheet.</param>
/// <param name="Fills">For <see cref="ImportOutcome.Update"/>: which blank fields the
/// row would fill, e.g. "phone". Empty otherwise.</param>
public sealed record ImportRow(
    int LineNumber,
    string Email,
    string FullName,
    string Phone,
    ImportOutcome Outcome,
    string? Problem = null,
    IReadOnlyList<string>? Fills = null)
{
    public IReadOnlyList<string> Fills { get; init; } = Fills ?? [];
}

/// <param name="UnknownHeaders">Columns in the file that the import ignores. Surfaced so
/// a mis-named header is obvious rather than silently dropping a column.</param>
public sealed record ImportPreview(
    IReadOnlyList<ImportRow> Rows,
    IReadOnlyList<string> UnknownHeaders,
    string? FatalError = null)
{
    public int CountOf(ImportOutcome outcome) => Rows.Count(r => r.Outcome == outcome);

    /// Rows that will actually be written.
    public IEnumerable<ImportRow> Actionable =>
        Rows.Where(r => r.Outcome is ImportOutcome.Create or ImportOutcome.Update);

    public bool HasWork => Actionable.Any();
}

/// <param name="BookingsAdopted">Guest bookings attached to newly created customers.</param>
/// <param name="ClaimEmailsSent">Claim emails that actually went out.</param>
/// <param name="EmailFailures">Addresses the claim email could not be sent to. The
/// import itself still succeeded for these — the account exists, it just wasn't told.</param>
public sealed record ImportResult(
    int Created,
    int Updated,
    int BookingsAdopted,
    int ClaimEmailsSent,
    IReadOnlyList<string> EmailFailures,
    string? Error = null);
