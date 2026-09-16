using System.Reflection;

namespace KiTTYManager.Core;

public static class SecretBoxEditing
{
    public static bool CanCopy(int selectionLength) => selectionLength > 0;

    public static bool CanCut(bool isReadOnly, int selectionLength) => !isReadOnly && selectionLength > 0;

    public static string GetSelectedText(string value, int selectionStart, int selectionLength)
    {
        if (string.IsNullOrEmpty(value) || selectionLength <= 0 || selectionStart < 0 || selectionStart >= value.Length)
            return "";

        var length = Math.Min(selectionLength, value.Length - selectionStart);
        return value.Substring(selectionStart, length);
    }

    public static string RemoveSelectedText(string value, int selectionStart, int selectionLength)
    {
        if (string.IsNullOrEmpty(value) || selectionLength <= 0 || selectionStart < 0 || selectionStart >= value.Length)
            return value ?? "";

        var length = Math.Min(selectionLength, value.Length - selectionStart);
        return value.Remove(selectionStart, length);
    }

    public static (int Start, int Length) ExtractSelection(object? selection)
    {
        if (selection == null) return (0, 0);

        try
        {
            var rangeType = selection.GetType().GetInterfaces()
                .FirstOrDefault(i => i.Name == "ITextRange" || i.FullName == "System.Windows.Documents.ITextRange")
                ?? selection.GetType();

            var startProp = rangeType.GetProperty("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var endProp = rangeType.GetProperty("End", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var startPtr = startProp?.GetValue(selection);
            var endPtr = endProp?.GetValue(selection);

            if (startPtr == null || endPtr == null) return (0, 0);

            var isEmptyProp = rangeType.GetProperty("IsEmpty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? selection.GetType().GetProperty("IsEmpty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool isEmpty = isEmptyProp != null && (bool)isEmptyProp.GetValue(selection)! == true;

            int? startOffset = GetPointerOffset(startPtr);
            int? endOffset = GetPointerOffset(endPtr);

            if (startOffset.HasValue && endOffset.HasValue)
            {
                int start = Math.Min(startOffset.Value, endOffset.Value);
                int length = isEmpty ? 0 : Math.Abs(endOffset.Value - startOffset.Value);
                return (Math.Max(0, start), Math.Max(0, length));
            }
        }
        catch
        {
            // Suppress reflection/cast errors and default to no selection
        }

        return (0, 0);
    }

    public static bool SetSelection(object? passwordBox, int start, int length = 0)
    {
        if (passwordBox == null) return false;
        try
        {
            var selectMethod = passwordBox.GetType().GetMethod("Select", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(int) }, null);
            if (selectMethod != null)
            {
                selectMethod.Invoke(passwordBox, new object[] { Math.Max(0, start), Math.Max(0, length) });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static int? GetPointerOffset(object pointer)
    {
        var type = pointer.GetType();

        // 1. Check Offset property (PasswordTextPointer.Offset)
        var offsetProp = type.GetProperty("Offset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (offsetProp != null && offsetProp.PropertyType == typeof(int))
        {
            return (int)offsetProp.GetValue(pointer)!;
        }

        // 2. Check CharOffset property (ITextPointer.CharOffset)
        var iTextPointerType = type.GetInterfaces()
            .FirstOrDefault(i => i.Name == "ITextPointer" || i.FullName == "System.Windows.Documents.ITextPointer")
            ?? type;
        var charOffsetProp = iTextPointerType.GetProperty("CharOffset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type.GetProperty("CharOffset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (charOffsetProp != null && charOffsetProp.PropertyType == typeof(int))
        {
            return (int)charOffsetProp.GetValue(pointer)!;
        }

        // 3. Check _offset field (PasswordTextPointer._offset)
        var offsetField = type.GetField("_offset", BindingFlags.Instance | BindingFlags.NonPublic);
        if (offsetField != null && offsetField.FieldType == typeof(int))
        {
            return (int)offsetField.GetValue(pointer)!;
        }

        // 4. Fallback for standard TextPointer: DocumentStart.GetOffsetToPosition(pointer)
        var docStartProp = type.GetProperty("DocumentStart", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var docStart = docStartProp?.GetValue(pointer);
        if (docStart != null)
        {
            var getOffsetMethod = type.GetMethod("GetOffsetToPosition", new[] { type })
                ?? iTextPointerType.GetMethod("GetOffsetToPosition");
            if (getOffsetMethod != null)
            {
                return (int)getOffsetMethod.Invoke(docStart, new[] { pointer })!;
            }
        }

        return null;
    }
}
