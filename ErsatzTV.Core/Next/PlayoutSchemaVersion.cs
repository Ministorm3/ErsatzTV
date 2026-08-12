namespace ErsatzTV.Core.Next;

/// <summary>
///     The schema version a playout document declares.
/// </summary>
/// <remarks>
///     A worker reads this before it reads anything else, and refuses a document whose compatible
///     number is higher than the one it was built for. So the number is a demand, not a stamp: it
///     says which keys a reader has to understand to read this file correctly, and the honest value
///     is the lowest version that can express what the document actually contains.
///     Declaring less than the document needs is the failure worth avoiding. A worker built before
///     slate existed accepts a 0.0.2 document carrying a slate, ignores the key it does not know,
///     and airs the templated source it was told to stand down from, which is the one outcome no
///     one can see from the outside. Declaring the version the key came with makes that same worker
///     say so and stop.
///     Declaring more than the document needs costs the other direction: every file this engine
///     writes would demand a newer worker, including the files of channels that never asked for a
///     slate. So documents keep declaring 0.0.2 until a slate is actually in one.
/// </remarks>
public static class PlayoutSchemaVersion
{
    private const string Prefix = "https://ersatztv.org/playout/version/";

    /// <summary>
    ///     Everything this engine wrote before an item could name a slate.
    /// </summary>
    public const string WithoutSlate = Prefix + "0.0.2";

    /// <summary>
    ///     Adds the optional "slate" key on an item. Additive: a reader of this version reads every
    ///     document of the version below it.
    /// </summary>
    public const string WithSlate = Prefix + "0.0.3";

    public static string For(IEnumerable<PlayoutItem> items) =>
        items.Any(item => item.Slate is not null) ? WithSlate : WithoutSlate;
}
