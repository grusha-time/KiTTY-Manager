namespace KiTTYManager.Core;

public static class SessionNotePolicy
{
    public const int PreviewLimit = 400;

    public static string Normalize(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "" : note;

    public static bool HasNote(string? note) => !string.IsNullOrWhiteSpace(note);

    public static string Preview(string? note)
    {
        var normalized = Normalize(note);
        if (normalized.Length <= PreviewLimit) return normalized;
        var length = PreviewLimit;
        if (char.IsHighSurrogate(normalized[length - 1])) length--;
        return normalized[..length] + "…";
    }
}
