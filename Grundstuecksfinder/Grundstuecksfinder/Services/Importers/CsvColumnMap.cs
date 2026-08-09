namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Describes where each Property field lives in a delimited text source, so a new region
/// with a similar CSV layout only needs to supply one of these — no parsing code.
/// </summary>
public sealed record CsvColumnMap(
    char Delimiter,
    int MinColumnCount,
    int StrIndex,
    int HnrIndex,
    int HnrZusIndex,
    int PlzIndex,
    int OrtIndex,
    int GemeindeIndex,
    int FlaecheAmtlIndex,
    string NullToken = "NULL");
