namespace KiTTYManager.Core;

public static class TreeSelectionPolicy
{
    public static bool NextBranchClick(bool? current) => current == false;

    public static bool? Aggregate(IEnumerable<bool?> children)
    {
        var values = children.ToArray();
        if (values.Length == 0) return false;
        if (values.All(value => value == true)) return true;
        if (values.All(value => value == false)) return false;
        return null;
    }
}
