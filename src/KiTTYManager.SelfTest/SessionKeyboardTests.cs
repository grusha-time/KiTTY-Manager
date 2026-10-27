using KiTTYManager.App;
using KiTTYManager.Core;
using System.Xml.Linq;

internal sealed partial class SelfTestRunner
{
    private static void SessionOpenDispatcherTests()
    {
        // 1. Regular server -> calls openRoutedConsoleAsync
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-A" };
            var config = new ManagerConfig();
            var jumphostCalled = false;
            var consoleCalled = false;
            ManagedServer? invokedServer = null;

            SessionOpenDispatcher.OpenAsync(
                server,
                config,
                s => { jumphostCalled = true; return Task.CompletedTask; },
                s => { consoleCalled = true; invokedServer = s; return Task.CompletedTask; })
                .GetAwaiter().GetResult();

            Equal(false, jumphostCalled);
            Equal(true, consoleCalled);
            Equal(server.Id, invokedServer?.Id);
        }

        // 2. Enabled matching jumphost -> calls startJumphostAsync
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Name = "JH-Server" };
            var config = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Enabled = true, StartupServerId = server.Id, Name = "JH" }]
            };
            var jumphostCalled = false;
            var consoleCalled = false;
            ManagedServer? invokedServer = null;

            SessionOpenDispatcher.OpenAsync(
                server,
                config,
                s => { jumphostCalled = true; invokedServer = s; return Task.CompletedTask; },
                s => { consoleCalled = true; return Task.CompletedTask; })
                .GetAwaiter().GetResult();

            Equal(true, jumphostCalled);
            Equal(false, consoleCalled);
            Equal(server.Id, invokedServer?.Id);
        }

        // 3. Disabled jumphost with matching server -> calls openRoutedConsoleAsync
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Disabled-JH" };
            var config = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Enabled = false, StartupServerId = server.Id, Name = "JH-Disabled" }]
            };
            var jumphostCalled = false;
            var consoleCalled = false;
            ManagedServer? invokedServer = null;

            SessionOpenDispatcher.OpenAsync(
                server,
                config,
                s => { jumphostCalled = true; return Task.CompletedTask; },
                s => { consoleCalled = true; invokedServer = s; return Task.CompletedTask; })
                .GetAwaiter().GetResult();

            Equal(false, jumphostCalled);
            Equal(true, consoleCalled);
            Equal(server.Id, invokedServer?.Id);
        }

        // 4. Enabled jumphost with different server -> calls openRoutedConsoleAsync
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-B" };
            var otherServerId = Guid.NewGuid();
            var config = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Enabled = true, StartupServerId = otherServerId, Name = "JH-Other" }]
            };
            var jumphostCalled = false;
            var consoleCalled = false;
            ManagedServer? invokedServer = null;

            SessionOpenDispatcher.OpenAsync(
                server,
                config,
                s => { jumphostCalled = true; return Task.CompletedTask; },
                s => { consoleCalled = true; invokedServer = s; return Task.CompletedTask; })
                .GetAwaiter().GetResult();

            Equal(false, jumphostCalled);
            Equal(true, consoleCalled);
            Equal(server.Id, invokedServer?.Id);
        }

        // 5. Null arguments throw ArgumentNullException
        {
            var dummyServer = new ManagedServer { Id = Guid.NewGuid() };
            var dummyConfig = new ManagerConfig();
            Func<ManagedServer, Task> dummyFunc = _ => Task.CompletedTask;

            AssertThrows<ArgumentNullException>(() =>
                SessionOpenDispatcher.OpenAsync(null!, dummyConfig, dummyFunc, dummyFunc));

            AssertThrows<ArgumentNullException>(() =>
                SessionOpenDispatcher.OpenAsync(dummyServer, null!, dummyFunc, dummyFunc));

            AssertThrows<ArgumentNullException>(() =>
                SessionOpenDispatcher.OpenAsync(dummyServer, dummyConfig, null!, dummyFunc));

            AssertThrows<ArgumentNullException>(() =>
                SessionOpenDispatcher.OpenAsync(dummyServer, dummyConfig, dummyFunc, null!));
        }
    }

    private static void SessionKeyboardPolicyAndBindingTests()
    {
        // 1. SessionKeyboardPolicy.EvaluateSearchKeyDown
        // - Down with items -> Handled=true, ShouldNavigate=true, TargetIndex=0
        var s1 = SessionKeyboardPolicy.EvaluateSearchKeyDown("Down", false, 5);
        Equal(true, s1.Handled);
        Equal(true, s1.ShouldNavigate);
        Equal(0, s1.TargetIndex);

        // - Down with 0 items -> Handled=true, ShouldNavigate=false, TargetIndex=-1
        var s2 = SessionKeyboardPolicy.EvaluateSearchKeyDown("Down", false, 0);
        Equal(true, s2.Handled);
        Equal(false, s2.ShouldNavigate);
        Equal(-1, s2.TargetIndex);

        // - Down with modifiers (Ctrl/Alt/Shift) -> Handled=false
        var s3 = SessionKeyboardPolicy.EvaluateSearchKeyDown("Down", true, 5);
        Equal(false, s3.Handled);
        Equal(false, s3.ShouldNavigate);

        // - Other keys -> Handled=false
        var s4 = SessionKeyboardPolicy.EvaluateSearchKeyDown("Up", false, 5);
        Equal(false, s4.Handled);
        Equal(false, s4.ShouldNavigate);

        var s5 = SessionKeyboardPolicy.EvaluateSearchKeyDown("Enter", false, 5);
        Equal(false, s5.Handled);

        // 1b. SessionKeyboardPolicy.ShouldClearSearchOnKeyDown
        Equal(true, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Escape", false));
        Equal(true, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Esc", false));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Escape", true));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Esc", true));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Down", false));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Enter", false));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("A", false));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("F", true));

        // 2. SessionKeyboardPolicy.EvaluateGridKeyDown
        // - Enter with server and not repeat -> Handled=true, ShouldOpen=true
        var g1 = SessionKeyboardPolicy.EvaluateGridKeyDown("Enter", false, false, true);
        Equal(true, g1.Handled);
        Equal(true, g1.ShouldOpen);

        // Also test Return (synonym of Enter in WPF)
        var g1b = SessionKeyboardPolicy.EvaluateGridKeyDown("Return", false, false, true);
        Equal(true, g1b.Handled);
        Equal(true, g1b.ShouldOpen);

        // - Enter without server -> Handled=true, ShouldOpen=false
        var g2 = SessionKeyboardPolicy.EvaluateGridKeyDown("Enter", false, false, false);
        Equal(true, g2.Handled);
        Equal(false, g2.ShouldOpen);

        // - Enter with repeat -> Handled=true, ShouldOpen=false
        var g3 = SessionKeyboardPolicy.EvaluateGridKeyDown("Enter", false, true, true);
        Equal(true, g3.Handled);
        Equal(false, g3.ShouldOpen);

        // - Enter with modifiers -> Handled=false, ShouldOpen=false
        var g4 = SessionKeyboardPolicy.EvaluateGridKeyDown("Enter", true, false, true);
        Equal(false, g4.Handled);
        Equal(false, g4.ShouldOpen);

        // - Other keys -> Handled=false
        var g5 = SessionKeyboardPolicy.EvaluateGridKeyDown("Space", false, false, true);
        Equal(false, g5.Handled);
        Equal(false, g5.ShouldOpen);

        // 3. SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged
        Equal(true, SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged(true, true));
        Equal(false, SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged(false, true));
        Equal(false, SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged(true, false));
        Equal(false, SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged(false, false));

        // 4. XAML and Code-Behind Regression Verification
        var xamlPath = ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml"));
        var xamlText = File.ReadAllText(xamlPath);

        Equal(true, xamlText.Contains("Command=\"ApplicationCommands.Find\"") &&
                    xamlText.Contains("Executed=\"FindSession_Executed\""));
        Equal(true, xamlText.Contains("Key=\"F\"") &&
                    xamlText.Contains("Modifiers=\"Control\"") &&
                    xamlText.Contains("Command=\"ApplicationCommands.Find\""));
        Equal(true, xamlText.Contains("x:Name=\"SearchBox\"") &&
                    xamlText.Contains("PreviewKeyDown=\"SearchBox_PreviewKeyDown\""));
        Equal(true, xamlText.Contains("x:Name=\"SessionsGrid\"") &&
                    xamlText.Contains("PreviewKeyDown=\"SessionsGrid_PreviewKeyDown\"") &&
                    xamlText.Contains("SelectionChanged=\"SessionsGrid_SelectionChanged\""));

        Equal(true, xamlText.Contains("x:Name=\"GroupSearchClearButton\""));
        Equal(true, xamlText.Contains("x:Name=\"SearchClearButton\""));
        Equal(true, xamlText.Contains("PreviewKeyDown=\"GroupSearchBox_PreviewKeyDown\""));
        Equal(true, xamlText.Contains("Click=\"GroupSearchClear_Click\""));
        Equal(true, xamlText.Contains("Click=\"SearchClear_Click\""));
        Equal(true, xamlText.Contains("Focusable=\"False\""));
        Equal(true, xamlText.Contains("IsTabStop=\"False\""));

        var doc = XDocument.Parse(xamlText);
        var ns = doc.Root!.Name.Namespace;
        var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var allTextBoxes = doc.Descendants(ns + "TextBox").ToList();
        var groupSearchBoxEl = allTextBoxes.FirstOrDefault(e => (string?)e.Attribute(xNs + "Name") == "GroupSearchBox")
            ?? throw new InvalidOperationException("GroupSearchBox not found in XAML");
        var searchBoxEl = allTextBoxes.FirstOrDefault(e => (string?)e.Attribute(xNs + "Name") == "SearchBox")
            ?? throw new InvalidOperationException("SearchBox not found in XAML");

        Equal("10,8,38,8", (string?)groupSearchBoxEl.Attribute("Padding"));
        Equal("10,8,38,8", (string?)searchBoxEl.Attribute("Padding"));
        Equal("GroupSearchBox_PreviewKeyDown", (string?)groupSearchBoxEl.Attribute("PreviewKeyDown"));

        var allButtons = doc.Descendants(ns + "Button").ToList();
        var groupClearBtnEl = allButtons.FirstOrDefault(e => (string?)e.Attribute(xNs + "Name") == "GroupSearchClearButton")
            ?? throw new InvalidOperationException("GroupSearchClearButton not found in XAML");
        var searchClearBtnEl = allButtons.FirstOrDefault(e => (string?)e.Attribute(xNs + "Name") == "SearchClearButton")
            ?? throw new InvalidOperationException("SearchClearButton not found in XAML");

        Equal("GroupSearchClear_Click", (string?)groupClearBtnEl.Attribute("Click"));
        Equal("SearchClear_Click", (string?)searchClearBtnEl.Attribute("Click"));

        var groupBtnTriggers = groupClearBtnEl.Descendants(ns + "DataTrigger").ToList();
        var groupClearTrigger = groupBtnTriggers.FirstOrDefault(dt =>
            ((string?)dt.Attribute("Value")) == "0" &&
            ((string?)dt.Attribute("Binding"))?.Contains("ElementName=GroupSearchBox") == true &&
            ((string?)dt.Attribute("Binding"))?.Contains("Text.Length") == true);
        Equal(true, groupClearTrigger is not null);
        var groupSetter = groupClearTrigger!.Elements(ns + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "Visibility");
        Equal("Collapsed", (string?)groupSetter?.Attribute("Value"));

        var searchBtnTriggers = searchClearBtnEl.Descendants(ns + "DataTrigger").ToList();
        var searchClearTrigger = searchBtnTriggers.FirstOrDefault(dt =>
            ((string?)dt.Attribute("Value")) == "0" &&
            ((string?)dt.Attribute("Binding"))?.Contains("ElementName=SearchBox") == true &&
            ((string?)dt.Attribute("Binding"))?.Contains("Text.Length") == true);
        Equal(true, searchClearTrigger is not null);
        var searchSetter = searchClearTrigger!.Elements(ns + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "Visibility");
        Equal("Collapsed", (string?)searchSetter?.Attribute("Value"));

        var searchClearStyle = doc.Descendants(ns + "Style").FirstOrDefault(s => (string?)s.Attribute(xNs + "Key") == "SearchClearButtonStyle");
        Equal(true, searchClearStyle is not null);
        var focusableSetter = searchClearStyle!.Elements(ns + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "Focusable");
        Equal("False", (string?)focusableSetter?.Attribute("Value"));
        var tabStopSetter = searchClearStyle!.Elements(ns + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "IsTabStop");
        Equal("False", (string?)tabStopSetter?.Attribute("Value"));

        var searchClearPath = searchClearStyle!.Descendants(ns + "Path").FirstOrDefault();
        Equal(true, searchClearPath is not null);
        Equal("14", (string?)searchClearPath!.Attribute("Width"));
        Equal("14", (string?)searchClearPath!.Attribute("Height"));

        var csPath = ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml.cs"));
        var csText = File.ReadAllText(csPath);

        // Method body extraction and exact handler assertions
        var findSessionBody = ExtractMethodBody(csText, "FindSession_Executed");
        Equal(true, ContainsUncommentedCode(findSessionBody, "SearchBox.Focus();"));
        Equal(true, ContainsUncommentedCode(findSessionBody, "SearchBox.SelectAll();"));
        Equal(true, ContainsUncommentedCode(findSessionBody, "e.Handled = true;"));

        var searchBoxBody = ExtractMethodBody(csText, "SearchBox_PreviewKeyDown");
        Equal(true, ContainsUncommentedCode(searchBoxBody, "SessionKeyboardPolicy.ShouldClearSearchOnKeyDown"));
        Equal(true, ContainsUncommentedCode(searchBoxBody, "SearchBox.Clear();"));
        Equal(true, ContainsUncommentedCode(searchBoxBody, "SearchBox.Focus();"));
        Equal(true, ContainsUncommentedCode(searchBoxBody, "e.Handled = true;"));
        var clearIdx = searchBoxBody.IndexOf("ShouldClearSearchOnKeyDown", StringComparison.Ordinal);
        var evalIdx = searchBoxBody.IndexOf("EvaluateSearchKeyDown", StringComparison.Ordinal);
        Equal(true, clearIdx >= 0 && evalIdx > clearIdx);

        var groupClearBody = ExtractMethodBody(csText, "GroupSearchClear_Click");
        Equal(true, ContainsUncommentedCode(groupClearBody, "GroupSearchBox.Clear();"));
        Equal(true, ContainsUncommentedCode(groupClearBody, "GroupSearchBox.Focus();"));

        var searchClearBody = ExtractMethodBody(csText, "SearchClear_Click");
        Equal(true, ContainsUncommentedCode(searchClearBody, "SearchBox.Clear();"));
        Equal(true, ContainsUncommentedCode(searchClearBody, "SearchBox.Focus();"));

        var groupKeyDownBody = ExtractMethodBody(csText, "GroupSearchBox_PreviewKeyDown");
        Equal(true, ContainsUncommentedCode(groupKeyDownBody, "SessionKeyboardPolicy.ShouldClearSearchOnKeyDown"));
        Equal(true, ContainsUncommentedCode(groupKeyDownBody, "GroupSearchBox.Clear();"));
        Equal(true, ContainsUncommentedCode(groupKeyDownBody, "GroupSearchBox.Focus();"));
        Equal(true, ContainsUncommentedCode(groupKeyDownBody, "e.Handled = true;"));

        Equal(true, ContainsUncommentedCode(searchBoxBody, "SessionKeyboardPolicy.EvaluateSearchKeyDown"));
        Equal(true, ContainsUncommentedCode(searchBoxBody, "e.Handled = true;"));

        var gridBody = ExtractMethodBody(csText, "SessionsGrid_PreviewKeyDown");
        Equal(true, ContainsUncommentedCode(gridBody, "SessionKeyboardPolicy.EvaluateGridKeyDown"));
        Equal(true, ContainsUncommentedCode(gridBody, "e.Handled = true;"));

        var selectionChangedBody = ExtractMethodBody(csText, "SessionsGrid_SelectionChanged");
        Equal(true, ContainsUncommentedCode(selectionChangedBody, "SessionKeyboardPolicy.ShouldSyncDetailsOnSelectionChanged"));

        Equal(false, ContainsUncommentedCode("/*\n e.Handled = true;\n */", "e.Handled = true;"));
        Equal(false, ContainsUncommentedCode("// e.Handled = true;\n", "e.Handled = true;"));

        // 5. Windows STA check
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "KiTTYManager.dll"),
                ResolveAppDll()
            };
            var appDll = candidates.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("KiTTYManager.dll не найдена для запуска Windows STA теста.");

            Exception? threadEx = null;
            var thread = new Thread(() =>
            {
                try
                {
                    RunWindowsWpfKeyboardTests(appDll);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (threadEx is not null) throw threadEx;
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        if (methodIndex < 0)
            throw new InvalidOperationException($"Method {methodName} not found in source.");

        var openBrace = source.IndexOf('{', methodIndex);
        if (openBrace < 0)
            throw new InvalidOperationException($"Opening brace for {methodName} not found.");

        var depth = 1;
        var i = openBrace + 1;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) break;
            }
        }

        if (depth != 0)
            throw new InvalidOperationException($"Matching closing brace for {methodName} not found.");

        return source.Substring(openBrace, i - openBrace + 1);
    }

    private static string StripComments(string code) =>
        System.Text.RegularExpressions.Regex.Replace(code, @"/\*[\s\S]*?\*/|//.*", "");

    private static bool ContainsUncommentedCode(string methodBody, string expectedCode) =>
        StripComments(methodBody).Contains(expectedCode);

    private static string ResolveAppDll()
    {
        var repoRoot = ResolveRepoRoot();
        var paths = new[]
        {
            Path.Combine(repoRoot, "src", "KiTTYManager.App", "bin", "Release", "net8.0-windows", "KiTTYManager.dll"),
            Path.Combine(repoRoot, "src", "KiTTYManager.App", "bin", "Debug", "net8.0-windows", "KiTTYManager.dll")
        };
        return paths.FirstOrDefault(File.Exists) ?? paths[0];
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "KiTTYManager.sln")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ResolveSourceFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
        if (File.Exists(cwdCandidate)) return cwdCandidate;

        throw new FileNotFoundException($"Cannot locate source file: {relativePath}");
    }

    private static void RunWindowsWpfKeyboardTests(string appDll)
    {
        var asm = System.Reflection.Assembly.LoadFrom(appDll);
        var windowType = asm.GetType("KiTTYManager.App.MainWindow")
            ?? throw new InvalidOperationException("KiTTYManager.App.MainWindow not found in assembly.");

        var presentationCore = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PresentationCore")
            ?? System.Reflection.Assembly.Load("PresentationCore");
        var presentationFramework = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PresentationFramework")
            ?? System.Reflection.Assembly.Load("PresentationFramework");

        var keyEventArgsType = presentationCore.GetType("System.Windows.Input.KeyEventArgs")!;
        var executedRoutedEventArgsType = presentationCore.GetType("System.Windows.Input.ExecutedRoutedEventArgs")!;
        var routedEventArgsType = presentationCore.GetType("System.Windows.RoutedEventArgs")!;
        var handledProp = routedEventArgsType.GetProperty("Handled")!;
        var routedEventProp = routedEventArgsType.GetProperty("RoutedEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var keyProp = keyEventArgsType.GetProperty("Key", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var keyType = keyProp.PropertyType;

        var keyboardType = presentationCore.GetType("System.Windows.Input.Keyboard")!;
        var previewKeyDownEventField = keyboardType.GetField("PreviewKeyDownEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var previewKeyDownEvent = previewKeyDownEventField.GetValue(null)!;

        var textBoxType = presentationFramework.GetType("System.Windows.Controls.TextBox")!;
        var dataGridType = presentationFramework.GetType("System.Windows.Controls.DataGrid")!;

        var window = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(windowType);

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var searchBox = Activator.CreateInstance(textBoxType)!;
        var groupSearchBox = Activator.CreateInstance(textBoxType)!;
        var sessionsGrid = Activator.CreateInstance(dataGridType)!;

        windowType.GetField("SearchBox", flags)?.SetValue(window, searchBox);
        windowType.GetField("GroupSearchBox", flags)?.SetValue(window, groupSearchBox);
        windowType.GetField("SessionsGrid", flags)?.SetValue(window, sessionsGrid);

        // 1. Verify FindSession_Executed
        var findMethod = windowType.GetMethod("FindSession_Executed", flags)
            ?? throw new InvalidOperationException("FindSession_Executed not found");
        var commandManagerType = presentationCore.GetType("System.Windows.Input.CommandManager")!;
        var executedEventField = commandManagerType.GetField("ExecutedEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var executedEvent = executedEventField.GetValue(null)!;
        var appCommandsType = presentationCore.GetType("System.Windows.Input.ApplicationCommands")!;
        var findProp = appCommandsType.GetProperty("Find", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var findCommand = findProp.GetValue(null)!;
        var execCtor = executedRoutedEventArgsType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("ExecutedRoutedEventArgs constructor with 2 parameters not found.");
        var execArgs = execCtor.Invoke(new object[] { findCommand, searchBox });
        routedEventProp.SetValue(execArgs, executedEvent);
        findMethod.Invoke(window, new object[] { searchBox, execArgs });
        var findHandled = (bool)handledProp.GetValue(execArgs)!;
        Equal(true, findHandled);

        // KeyEventArgs creation using standard public constructor
        var keyboardDeviceProp = keyboardType.GetProperty("PrimaryDevice", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var primaryDevice = keyboardDeviceProp.GetValue(null);

        var hwndSourceType = presentationCore.GetType("System.Windows.Interop.HwndSource")!;
        var presentationSourceType = presentationCore.GetType("System.Windows.PresentationSource")!;
        var keyboardDeviceType = presentationCore.GetType("System.Windows.Input.KeyboardDevice")!;

        var hwndSourceCtor = hwndSourceType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(IntPtr) });
        var source = hwndSourceCtor?.Invoke(new object[] { 0, 0, 0, 0, 0, "Test", IntPtr.Zero });

        if (source is not null)
        {
            try
            {
                var rootProp = hwndSourceType.GetProperty("RootVisual", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var stackPanelType = presentationFramework.GetType("System.Windows.Controls.StackPanel");
                if (rootProp is not null && stackPanelType is not null)
                {
                    var rootPanel = Activator.CreateInstance(stackPanelType);
                    var childrenProp = stackPanelType.GetProperty("Children", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var children = childrenProp?.GetValue(rootPanel);
                    var addMethod = children?.GetType().GetMethod("Add", new[] { presentationCore.GetType("System.Windows.UIElement")! });
                    addMethod?.Invoke(children, new[] { groupSearchBox });
                    addMethod?.Invoke(children, new[] { searchBox });
                    rootProp.SetValue(source, rootPanel);
                }
            }
            catch
            {
                // Visual root setup fallback
            }
        }

        try
        {
            var keyEventArgsCtor = keyEventArgsType.GetConstructor(new[] { keyboardDeviceType, presentationSourceType, typeof(int), keyType })
                ?? keyEventArgsType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .FirstOrDefault(c => c.GetParameters().Length == 4)
                ?? throw new InvalidOperationException("KeyEventArgs constructor with 4 parameters not found.");

            object CreateKeyArgs(string keyName)
            {
                var keyValue = Enum.Parse(keyType, keyName);
                var args = keyEventArgsCtor.Invoke(new[] { primaryDevice, source, 0, keyValue });
                routedEventProp.SetValue(args, previewKeyDownEvent);
                return args;
            }

            // 2. Verify SearchBox_PreviewKeyDown sets e.Handled = true for Down
            var searchKeyDownMethod = windowType.GetMethod("SearchBox_PreviewKeyDown", flags)
                ?? throw new InvalidOperationException("SearchBox_PreviewKeyDown not found");
            var downArgs = CreateKeyArgs("Down");
            Equal(Enum.Parse(keyType, "Down"), keyProp.GetValue(downArgs));
            Equal(true, routedEventProp.GetValue(downArgs) is not null);
            searchKeyDownMethod.Invoke(window, new object[] { searchBox, downArgs });
            var downHandled = (bool)handledProp.GetValue(downArgs)!;
            Equal(true, downHandled);

            // 3. Verify SessionsGrid_PreviewKeyDown sets e.Handled = true for Enter
            var gridKeyDownMethod = windowType.GetMethod("SessionsGrid_PreviewKeyDown", flags)
                ?? throw new InvalidOperationException("SessionsGrid_PreviewKeyDown not found");
            var enterArgs = CreateKeyArgs("Enter");
            Equal(Enum.Parse(keyType, "Enter"), keyProp.GetValue(enterArgs));
            Equal(true, routedEventProp.GetValue(enterArgs) is not null);
            gridKeyDownMethod.Invoke(window, new object[] { sessionsGrid, enterArgs });
            var enterHandled = (bool)handledProp.GetValue(enterArgs)!;
            Equal(true, enterHandled);

            // 4. Verify SearchBox and GroupSearchBox Esc clears text and sets e.Handled = true
            var groupKeyDownMethod = windowType.GetMethod("GroupSearchBox_PreviewKeyDown", flags)
                ?? throw new InvalidOperationException("GroupSearchBox_PreviewKeyDown not found");
            var groupClearMethod = windowType.GetMethod("GroupSearchClear_Click", flags)
                ?? throw new InvalidOperationException("GroupSearchClear_Click not found");
            var searchClearMethod = windowType.GetMethod("SearchClear_Click", flags)
                ?? throw new InvalidOperationException("SearchClear_Click not found");

            var textProp = textBoxType.GetProperty("Text", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;

            // GroupSearchBox Esc
            textProp.SetValue(groupSearchBox, "group esc query");
            var groupEscArgs = CreateKeyArgs("Escape");
            groupKeyDownMethod.Invoke(window, new object[] { groupSearchBox, groupEscArgs });
            Equal(true, (bool)handledProp.GetValue(groupEscArgs)!);
            Equal("", (string)textProp.GetValue(groupSearchBox)!);

            // SearchBox Esc
            textProp.SetValue(searchBox, "session esc query");
            var searchEscArgs = CreateKeyArgs("Escape");
            searchKeyDownMethod.Invoke(window, new object[] { searchBox, searchEscArgs });
            Equal(true, (bool)handledProp.GetValue(searchEscArgs)!);
            Equal("", (string)textProp.GetValue(searchBox)!);

            // GroupSearchClear_Click
            textProp.SetValue(groupSearchBox, "group click query");
            var buttonType = presentationFramework.GetType("System.Windows.Controls.Button")!;
            var groupClearBtn = Activator.CreateInstance(buttonType)!;
            var routedArgsCtor = routedEventArgsType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 0)
                ?? throw new InvalidOperationException("RoutedEventArgs parameterless constructor not found.");
            var clickArgs = routedArgsCtor.Invoke(Array.Empty<object>());
            groupClearMethod.Invoke(window, new object[] { groupClearBtn, clickArgs });
            Equal("", (string)textProp.GetValue(groupSearchBox)!);

            // SearchClear_Click
            textProp.SetValue(searchBox, "session click query");
            var searchClearBtn = Activator.CreateInstance(buttonType)!;
            searchClearMethod.Invoke(window, new object[] { searchClearBtn, clickArgs });
            Equal("", (string)textProp.GetValue(searchBox)!);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}
