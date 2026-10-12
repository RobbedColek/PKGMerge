namespace PKGMerge.Core;

public static class Plural
{
    public static string Pieces(int count) => count == 1 ? "1 piece" : $"{count} pieces";
}
