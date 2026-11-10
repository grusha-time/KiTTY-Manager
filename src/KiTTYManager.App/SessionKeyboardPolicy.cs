namespace KiTTYManager.App;

public readonly record struct SearchKeyDownResult(bool Handled, bool ShouldNavigate, int TargetIndex);

public readonly record struct GridKeyDownResult(bool Handled, bool ShouldOpen);

public static class SessionKeyboardPolicy
{
    public static bool ShouldClearSearchOnKeyDown(string key, bool hasModifiers)
    {
        return (key is "Escape" or "Esc") && !hasModifiers;
    }

    public static bool ShouldFlushSearchDebounceOnKeyDown(string key, bool hasModifiers)
    {
        return (key is "Return" or "Enter" or "Down" or "Up") && !hasModifiers;
    }

    public static SearchKeyDownResult EvaluateSearchKeyDown(string key, bool hasModifiers, int itemCount)
    {
        if (key == "Down" && !hasModifiers)
        {
            if (itemCount > 0)
                return new(Handled: true, ShouldNavigate: true, TargetIndex: 0);
            return new(Handled: true, ShouldNavigate: false, TargetIndex: -1);
        }

        return new(Handled: false, ShouldNavigate: false, TargetIndex: -1);
    }

    public static GridKeyDownResult EvaluateGridKeyDown(string key, bool hasModifiers, bool isRepeat, bool hasSelectedServer)
    {
        if ((key is "Enter" or "Return") && !hasModifiers)
        {
            return new(Handled: true, ShouldOpen: !isRepeat && hasSelectedServer);
        }

        return new(Handled: false, ShouldOpen: false);
    }

    public static bool ShouldSyncDetailsOnSelectionChanged(bool isKeyboardFocusWithin, bool hasSelectedRow)
    {
        return isKeyboardFocusWithin && hasSelectedRow;
    }
}
