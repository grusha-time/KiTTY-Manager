using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void WebFieldValueResolverTests()
    {
        var web = new WebInterface
        {
            Name = "Panel",
            Url = "https://10.0.0.1:8443",
            ResolverAddress = "10.0.0.53",
            Username = "admin",
            Password = "secret-password"
        };

        // All 5 fields resolve accurately
        Equal("Panel", WebFieldValueResolver.Resolve(web, nameof(WebInterface.Name)));
        Equal("https://10.0.0.1:8443", WebFieldValueResolver.Resolve(web, nameof(WebInterface.Url)));
        Equal("10.0.0.53", WebFieldValueResolver.Resolve(web, nameof(WebInterface.ResolverAddress)));
        Equal("admin", WebFieldValueResolver.Resolve(web, nameof(WebInterface.Username)));
        Equal("secret-password", WebFieldValueResolver.Resolve(web, nameof(WebInterface.Password)));

        // Unknown or invalid property names return null (no fallback to Name for copying)
        Equal(null, WebFieldValueResolver.Resolve(web, "UnknownProperty"));
        Equal(null, WebFieldValueResolver.Resolve(web, ""));
        Equal(null, WebFieldValueResolver.Resolve(web, null));
        Equal(null, WebFieldValueResolver.Resolve(null, nameof(WebInterface.Name)));

        // Sort resolver maintains fallback to Name
        Equal("Panel", WebFieldValueResolver.ResolveForSort(web, nameof(WebInterface.Name)));
        Equal("https://10.0.0.1:8443", WebFieldValueResolver.ResolveForSort(web, nameof(WebInterface.Url)));
        Equal("Panel", WebFieldValueResolver.ResolveForSort(web, "UnknownProperty"));
        Equal("Panel", WebFieldValueResolver.ResolveForSort(web, null));

        // Empty field values
        var emptyWeb = new WebInterface();
        Equal("", WebFieldValueResolver.Resolve(emptyWeb, nameof(WebInterface.Password)));
        Equal("", WebFieldValueResolver.Resolve(emptyWeb, nameof(WebInterface.Username)));

        // SecretBoxEditing tests: Absent selection
        Equal(false, SecretBoxEditing.CanCopy(0));
        Equal(false, SecretBoxEditing.CanCopy(-1));
        Equal(false, SecretBoxEditing.CanCut(isReadOnly: false, selectionLength: 0));
        Equal(false, SecretBoxEditing.CanCut(isReadOnly: false, selectionLength: -1));
        Equal("", SecretBoxEditing.GetSelectedText("secret-password", 0, 0));
        Equal("secret-password", SecretBoxEditing.RemoveSelectedText("secret-password", 0, 0));

        // SecretBoxEditing tests: Partial selection
        Equal(true, SecretBoxEditing.CanCopy(4));
        Equal(true, SecretBoxEditing.CanCut(isReadOnly: false, selectionLength: 4));
        Equal(false, SecretBoxEditing.CanCut(isReadOnly: true, selectionLength: 4));
        Equal("cret", SecretBoxEditing.GetSelectedText("secret-password", 2, 4));
        Equal("se-password", SecretBoxEditing.RemoveSelectedText("secret-password", 2, 4));

        // SecretBoxEditing tests: Full selection
        Equal(true, SecretBoxEditing.CanCopy(15));
        Equal(true, SecretBoxEditing.CanCut(isReadOnly: false, selectionLength: 15));
        Equal("secret-password", SecretBoxEditing.GetSelectedText("secret-password", 0, 15));
        Equal("", SecretBoxEditing.RemoveSelectedText("secret-password", 0, 15));

        // SecretBoxEditing tests: Clamping and boundary checks
        Equal("ord", SecretBoxEditing.GetSelectedText("secret-password", 12, 100));
        Equal("secret-passw", SecretBoxEditing.RemoveSelectedText("secret-password", 12, 100));
        Equal("", SecretBoxEditing.GetSelectedText("secret-password", 100, 5));
        Equal("secret-password", SecretBoxEditing.RemoveSelectedText("secret-password", 100, 5));
        Equal("", SecretBoxEditing.GetSelectedText("", 0, 5));
        Equal("", SecretBoxEditing.RemoveSelectedText("", 0, 5));

        // SecretBoxEditing tests: ExtractSelection with simulated WPF PasswordBox internal structure
        Equal((0, 0), SecretBoxEditing.ExtractSelection(null));
        Equal((0, 0), SecretBoxEditing.ExtractSelection(new MockWpfPasswordSelection(0, 0, isEmpty: true)));
        Equal((5, 0), SecretBoxEditing.ExtractSelection(new MockWpfPasswordSelection(5, 5, isEmpty: true)));

        // Partial selection: characters 2..6
        var partialMock = new MockWpfPasswordSelection(2, 6, isEmpty: false);
        var partialRange = SecretBoxEditing.ExtractSelection(partialMock);
        Equal(2, partialRange.Start);
        Equal(4, partialRange.Length);
        Equal(true, SecretBoxEditing.CanCopy(partialRange.Length));
        Equal(true, SecretBoxEditing.CanCut(isReadOnly: false, partialRange.Length));
        Equal(false, SecretBoxEditing.CanCut(isReadOnly: true, partialRange.Length));
        Equal("cret", SecretBoxEditing.GetSelectedText("secret-password", partialRange.Start, partialRange.Length));
        Equal("se-password", SecretBoxEditing.RemoveSelectedText("secret-password", partialRange.Start, partialRange.Length));

        // Full selection: characters 0..15
        var fullMock = new MockWpfPasswordSelection(0, 15, isEmpty: false);
        var fullRange = SecretBoxEditing.ExtractSelection(fullMock);
        Equal(0, fullRange.Start);
        Equal(15, fullRange.Length);
        Equal(true, SecretBoxEditing.CanCopy(fullRange.Length));
        Equal(true, SecretBoxEditing.CanCut(isReadOnly: false, fullRange.Length));
        Equal("secret-password", SecretBoxEditing.GetSelectedText("secret-password", fullRange.Start, fullRange.Length));
        Equal("", SecretBoxEditing.RemoveSelectedText("secret-password", fullRange.Start, fullRange.Length));

        // Inverted/reverse selection (dragged right-to-left)
        var revMock = new MockWpfPasswordSelection(6, 2, isEmpty: false);
        var revRange = SecretBoxEditing.ExtractSelection(revMock);
        Equal(2, revRange.Start);
        Equal(4, revRange.Length);
        Equal("cret", SecretBoxEditing.GetSelectedText("secret-password", revRange.Start, revRange.Length));

        // Cut-then-type test reproducing WPF PasswordBox caret behavior:
        // 1. Initial password is "secret-password"
        var box = new MockWpfPasswordBox("secret-password");
        // User selects "cret" (offset 2, length 4)
        var selectMethod = typeof(MockWpfPasswordBox).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        selectMethod.Invoke(box, new object[] { 2, 4 });

        // Extract selection and verify
        var cutSel = SecretBoxEditing.ExtractSelection(box.Selection);
        Equal(2, cutSel.Start);
        Equal(4, cutSel.Length);
        var cutText = SecretBoxEditing.GetSelectedText(box.Password, cutSel.Start, cutSel.Length);
        Equal("cret", cutText);

        // Perform Cut: remove selected text and update Password
        box.Password = SecretBoxEditing.RemoveSelectedText(box.Password, cutSel.Start, cutSel.Length);
        Equal("se-password", box.Password);

        // Verify that without caret restore, WPF setter reset caret to 0
        var resetSel = SecretBoxEditing.ExtractSelection(box.Selection);
        Equal(0, resetSel.Start);
        Equal(0, resetSel.Length);

        // Apply caret restoration via SecretBoxEditing.SetSelection
        var restored = SecretBoxEditing.SetSelection(box, cutSel.Start, 0);
        Equal(true, restored);
        var restoredSel = SecretBoxEditing.ExtractSelection(box.Selection);
        Equal(2, restoredSel.Start);
        Equal(0, restoredSel.Length);

        // User types "X": character is inserted at restored caret (offset 2), yielding "seX-password"
        box.Type("X");
        Equal("seX-password", box.Password);
    }

    private interface IMockTextPointer
    {
        int CharOffset { get; }
    }

    private sealed class MockPasswordTextPointer : IMockTextPointer
    {
        private readonly int _offset;
        public int Offset => _offset;
        int IMockTextPointer.CharOffset => _offset;
        public MockPasswordTextPointer(int offset) => _offset = offset;
    }

    private interface ITextRange
    {
        object Start { get; }
        object End { get; }
        bool IsEmpty { get; }
    }

    private sealed class MockWpfPasswordSelection : ITextRange
    {
        private readonly MockPasswordTextPointer _start;
        private readonly MockPasswordTextPointer _end;
        private readonly bool _isEmpty;

        public MockWpfPasswordSelection(int start, int end, bool isEmpty)
        {
            _start = new MockPasswordTextPointer(start);
            _end = new MockPasswordTextPointer(end);
            _isEmpty = isEmpty;
        }

        // Simulates WPF TextRange public property casting to TextPointer, which throws in PasswordBox
        public object Start => throw new InvalidCastException("Cannot cast PasswordTextPointer to TextPointer");
        public object End => throw new InvalidCastException("Cannot cast PasswordTextPointer to TextPointer");

        object ITextRange.Start => _start;
        object ITextRange.End => _end;
        bool ITextRange.IsEmpty => _isEmpty;
    }

    private sealed class MockWpfPasswordBox
    {
        private string _password = "";
        private MockWpfPasswordSelection _selection;

        public MockWpfPasswordBox(string initialPassword)
        {
            _password = initialPassword ?? "";
            _selection = new MockWpfPasswordSelection(0, 0, isEmpty: true);
        }

        public string Password
        {
            get => _password;
            set
            {
                _password = value ?? "";
                // WPF PasswordBox.Password setter calls ResetSelection() which sets selection to (0, 0)
                Select(0, 0);
            }
        }

        public object Selection => _selection;

        // Private method in WPF PasswordBox, invoked by reflection in SecretBoxEditing.SetSelection
        private void Select(int start, int length)
        {
            _selection = new MockWpfPasswordSelection(start, start + length, isEmpty: length == 0);
        }

        public void Type(string text)
        {
            var sel = SecretBoxEditing.ExtractSelection(_selection);
            _password = _password.Remove(sel.Start, sel.Length).Insert(sel.Start, text);
            Select(sel.Start + text.Length, 0);
        }
    }
}
