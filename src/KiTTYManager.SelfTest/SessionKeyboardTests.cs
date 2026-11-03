using System.Diagnostics;
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

    private static void NonBlockingSessionOpeningTests()
    {
        var csPath = ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml.cs"));
        var csText = File.ReadAllText(csPath);

        // 1. Structural checks for OpenRoutedConsoleAsync
        var openConsoleBody = ExtractMethodBody(csText, "async Task OpenRoutedConsoleAsync");
        Equal(false, ContainsUncommentedCode(openConsoleBody, "BusyAsync("));
        Equal(false, ContainsUncommentedCode(openConsoleBody, "HeaderPanel.IsEnabled = false"));
        Equal(false, ContainsUncommentedCode(openConsoleBody, "WorkspacePanel.IsEnabled = false"));
        Equal(false, ContainsUncommentedCode(openConsoleBody, "operationCancellation ="));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "pendingSessionOpenings.Add(openingKey)"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "pendingSessionOpenings.Remove(openingKey)"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "ConfigTransfer.CloneSnapshot"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "new SshConnectionService"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "await Task.Run"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "hasSourceSessionFile"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "OpenOriginalSessionAsync"));
        Equal(true, ContainsUncommentedCode(openConsoleBody, "cancellationToken.ThrowIfCancellationRequested()"));

        var openOrigSessionBody = ExtractMethodBody(csText, "async Task OpenOriginalSessionAsync(");
        Equal(true, ContainsUncommentedCode(openOrigSessionBody, "await Task.Run"));
        Equal(true, ContainsUncommentedCode(openOrigSessionBody, "cancellationToken.ThrowIfCancellationRequested()"));
        Equal(true, ContainsUncommentedCode(openOrigSessionBody, "Process.Start(startInfo)"));

        // 2. Structural checks for StartManagedJumphostAsync
        var startJhBody = ExtractMethodBody(csText, "async Task StartManagedJumphostAsync");
        Equal(false, ContainsUncommentedCode(startJhBody, "BusyAsync("));
        Equal(false, ContainsUncommentedCode(startJhBody, "HeaderPanel.IsEnabled = false"));
        Equal(false, ContainsUncommentedCode(startJhBody, "WorkspacePanel.IsEnabled = false"));
        Equal(false, ContainsUncommentedCode(startJhBody, "operationCancellation ="));
        Equal(true, ContainsUncommentedCode(startJhBody, "pendingSessionOpenings.Add(openingKey)"));
        Equal(true, ContainsUncommentedCode(startJhBody, "pendingSessionOpenings.Remove(openingKey)"));
        Equal(true, ContainsUncommentedCode(startJhBody, "ConfigTransfer.CloneSnapshot"));
        Equal(true, ContainsUncommentedCode(startJhBody, "runConfig: snapshotConfig"));

        // 2b. Structural checks for Connect and EnsureManagedJumphostAsync configuration passing
        var connectBody = ExtractMethodBody(csText, "async Task<ActiveRoute> Connect(");
        Equal(true, ContainsUncommentedCode(connectBody, "var activeConfig = routeConfig ?? ConfigTransfer.CloneSnapshot(config);"));
        Equal(true, ContainsUncommentedCode(connectBody, "var activeSsh = sshService ?? ssh;"));
        Equal(true, ContainsUncommentedCode(connectBody, "runConfig: activeConfig"));
        Equal(true, ContainsUncommentedCode(connectBody, "liveTarget.PreferredProxyId = targetServer.PreferredProxyId;"));
        Equal(true, ContainsUncommentedCode(connectBody, "liveProxy.LastConnectLatencyMs = result.Candidate.Proxy.LastConnectLatencyMs;"));
        Equal(true, ContainsUncommentedCode(connectBody, "liveLink.ProxyStatistics"));
        Equal(true, ContainsUncommentedCode(connectBody, "s.ProxyId == successfulProxyId"));
        Equal(true, ContainsUncommentedCode(connectBody, "IsProxyReadyAndPreparedAsync"));

        var isProxyReadyBody = ExtractMethodBody(csText, "async Task<bool> IsProxyReadyAndPreparedAsync(");
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "jumphostStartupGates.GetOrAdd"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "await startupGate.WaitAsync"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "accessScriptGates.GetOrAdd"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "await accessGate.WaitAsync"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "accessGate.Release();"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "startupGate.Release();"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "jumphostProcesses.TryGetAliveManaged"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "await IsSocks5ReadyAsync"));
        var socksProbeIdx = isProxyReadyBody.IndexOf("IsSocks5ReadyAsync", StringComparison.Ordinal);
        var accessReleaseIdx = isProxyReadyBody.IndexOf("accessGate.Release()", StringComparison.Ordinal);
        var startupReleaseIdx = isProxyReadyBody.IndexOf("startupGate.Release()", StringComparison.Ordinal);
        Equal(true, socksProbeIdx > 0);
        Equal(true, accessReleaseIdx > socksProbeIdx);
        Equal(true, startupReleaseIdx > accessReleaseIdx);

        // 2c. Structural checks for gate port reuse, access script snapshot isolation, and runtime sync
        var ensureJhBody = ExtractMethodBody(csText, "async Task<bool> EnsureManagedJumphostAsync");
        Equal(true, ContainsUncommentedCode(ensureJhBody, "await IsProxyReadyAndPreparedAsync"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "jumphostProcesses.TryGetAliveManaged(proxy, out var existingManaged)"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "!IsLiveConfigProxy(proxy)"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "!IsLiveConfigProxy(proxy)"));
        Equal(true, ContainsUncommentedCode(connectBody, "!IsLiveConfigProxy(candidate.Proxy)"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "if (hasIncompatibleEndpointConflict)"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "!hasIncompatibleEndpointConflict &&"));
        Equal(true, ContainsUncommentedCode(isProxyReadyBody, "ProxyEndpointComparer.HostsEquivalent(registered.Host, proxy.Host)"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "ProxyEndpointComparer.HostsEquivalent(registered.Host, proxy.Host)"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "probeHost = existingManaged.Host;"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "probePort = existingManaged.Port;"));
        Equal(true, ContainsUncommentedCode(ensureJhBody, "liveProxyForFailure?.LastStartupFailureUtc is not null"));

        var startJhInternalBody = ExtractMethodBody(csText, "async Task<bool> StartManagedJumphostInternalAsync(");
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "await Task.Run"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "cancellationToken.ThrowIfCancellationRequested()"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "var initialHost = proxy.Host;"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "var initialStartupServerId = proxy.StartupServerId;"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "var initialAutoSelect = proxy.UseAutomaticPort;"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "var expectedPort = proxy.Port;"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "syncPort: true"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "originalPort: expectedPort"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "originalHost: initialHost"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "originalStartupServerId: initialStartupServerId"));
        Equal(true, ContainsUncommentedCode(startJhInternalBody, "originalAutoSelect: initialAutoSelect"));

        var syncRuntimeBody = ExtractMethodBody(csText, "void SyncProxyRuntimeState(");
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "syncPort"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "liveProxy.Port == originalPort"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "originalHost is null || string.Equals(liveProxy.Host, originalHost"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "originalStartupServerId is null || liveProxy.StartupServerId == originalStartupServerId"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "originalAutoSelect is null || liveProxy.UseAutomaticPort == originalAutoSelect.Value"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "matchesStartupIdentity"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "activeConfig.FindServer(snapProxy.StartupServerId.Value)"));
        Equal(true, ContainsUncommentedCode(syncRuntimeBody, "liveServer.Host"));

        var accessScriptBody = ExtractMethodBody(csText, "async Task<bool> RunAccessScriptInExistingConsoleAsync(");
        Equal(true, ContainsUncommentedCode(accessScriptBody, "sharedLive.LastAccessScriptAttemptUtc != requestedAfter"));
        Equal(true, ContainsUncommentedCode(accessScriptBody, "runConfig ?? config"));
        Equal(true, ContainsUncommentedCode(accessScriptBody, "BuildPostLogin(executionProxy"));

        var accessScriptCoreBody = ExtractMethodBody(csText, "async Task<bool> RunAccessScriptAndConfirmCoreAsync(");
        Equal(true, ContainsUncommentedCode(accessScriptCoreBody, "await Task.Run"));
        Equal(true, ContainsUncommentedCode(accessScriptCoreBody, "AccessScriptRunnerPlan.Create"));
        Equal(true, ContainsUncommentedCode(accessScriptCoreBody, "cancellationToken.ThrowIfCancellationRequested()"));
        Equal(true, ContainsUncommentedCode(accessScriptCoreBody, "Process.Start(p.StartInfo)"));

        var savedLinksBody = ExtractMethodBody(csText, "void ShowSavedLinks_Click(");
        Equal(false, ContainsUncommentedCode(savedLinksBody, "ShowDialog()"));
        Equal(true, ContainsUncommentedCode(savedLinksBody, "dialog.Show();"));
        Equal(true, ContainsUncommentedCode(savedLinksBody, "savedLinksWindows[source.Id] = dialog;"));

        var checkSavedLinksBody = ExtractMethodBody(csText, "async Task<IReadOnlyList<ConnectivityResult>> CheckSavedLinksAsync(");
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "ConfigTransfer.CloneSnapshot(config)"));
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "runConfig: snapshotConfig"));
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "new SshConnectionService"));
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "snapshotConfig.AllServers()"));
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "EndpointPreferences"));
        Equal(true, ContainsUncommentedCode(checkSavedLinksBody, "RoutePreferencePolicy.ShouldReplacePreferred"));

        var jhIdentityPath = ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "JumphostProcessIdentity.cs"));
        var jhIdentityText = File.ReadAllText(jhIdentityPath);
        var stopAsyncBody = ExtractMethodBody(jhIdentityText, "async Task<bool> TryStopAliveManagedAsync(");
        Equal(true, ContainsUncommentedCode(stopAsyncBody, "cancellationToken.ThrowIfCancellationRequested()"));
        Equal(true, ContainsUncommentedCode(stopAsyncBody, "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)"));

        var canReuseBody = ExtractMethodBody(jhIdentityText, "bool CanReuseManaged(");
        Equal(true, ContainsUncommentedCode(canReuseBody, "identity.ProxyId != proxy.Id"));
        Equal(true, ContainsUncommentedCode(canReuseBody, "identity.StartupServerId != proxy.StartupServerId"));
        Equal(true, ContainsUncommentedCode(canReuseBody, "ProxyEndpointComparer.HostsEquivalent(identity.Host, proxy.Host)"));
        Equal(true, ContainsUncommentedCode(canReuseBody, "identity.UseAutomaticPort"));

        var tryGetAliveManagedBody = ExtractMethodBody(jhIdentityText, "bool TryGetAliveManaged(");
        Equal(true, ContainsUncommentedCode(tryGetAliveManagedBody, "CanReuseManaged(proxy, identity)"));

        var betterProbeBody = ExtractMethodBody(csText, "void ScheduleBetterRouteProbe(");
        Equal(true, ContainsUncommentedCode(betterProbeBody, "SyncBackgroundProbeHops"));
        Equal(true, ContainsUncommentedCode(betterProbeBody, "initialFingerprints"));

        var directProbeBody = ExtractMethodBody(csText, "void ScheduleDirectRouteProbe(");
        Equal(true, ContainsUncommentedCode(directProbeBody, "SyncBackgroundProbeHops"));
        Equal(true, ContainsUncommentedCode(directProbeBody, "initialFingerprints"));

        var syncBackgroundBody = ExtractMethodBody(csText, "void SyncBackgroundProbeHops(");
        Equal(true, ContainsUncommentedCode(syncBackgroundBody, "probeConfig.Links"));
        Equal(true, ContainsUncommentedCode(syncBackgroundBody, "if (liveLink is null) continue;"));
        Equal(false, ContainsUncommentedCode(syncBackgroundBody, "config.Links.Add(liveLink)"));

        var importBody = ExtractMethodBody(csText, "void ImportConfig_Click(");
        var rollbackBody = ExtractMethodBody(csText, "void RollbackLastImport(");
        var cleanupBody = ExtractMethodBody(csText, "void Cleanup(");
        Equal(true, ContainsUncommentedCode(importBody, "CloseActiveSavedLinksDialogs();"));
        Equal(true, ContainsUncommentedCode(rollbackBody, "CloseActiveSavedLinksDialogs();"));
        Equal(true, ContainsUncommentedCode(cleanupBody, "CloseActiveSavedLinksDialogs();"));

        var importCloseIdx = importBody.IndexOf("CloseActiveSavedLinksDialogs();", StringComparison.Ordinal);
        var importMergedIdx = importBody.IndexOf("config = merged;", StringComparison.Ordinal);
        Equal(true, importCloseIdx > 0 && importCloseIdx < importMergedIdx);

        var rollbackCloseIdx = rollbackBody.IndexOf("CloseActiveSavedLinksDialogs();", StringComparison.Ordinal);
        var rollbackLoadIdx = rollbackBody.IndexOf("config = ConfigStore.Load(", StringComparison.Ordinal);
        Equal(true, rollbackCloseIdx > 0 && rollbackCloseIdx < rollbackLoadIdx);

        // 3. Deduplication of pendingSessionOpenings
        var pending = new HashSet<(Guid ServerId, Guid? ForcedViaServerId)>();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        Equal(true, pending.Add((s1, null)));
        Equal(false, pending.Add((s1, null))); // Collapses duplicate
        Equal(true, pending.Add((s1, s2)));   // Different forced route proceeds
        Equal(true, pending.Add((s2, null))); // Different target proceeds
        Equal(true, pending.Remove((s1, null)));
        Equal(true, pending.Add((s1, null))); // Can re-add after completion

        // 4. Modeless dialog structure and API
        var dialogCode = File.ReadAllText(ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "ThemedMessageDialog.xaml.cs")));
        Equal(true, dialogCode.Contains("ShowModelessAsync("));
        Equal(true, dialogCode.Contains("try { DialogResult = true; } catch (InvalidOperationException) { Close(); }"));

        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "KiTTYManager.dll"),
                ResolveAppDll()
            };
            var appDll = candidates.FirstOrDefault(File.Exists);
            if (appDll is not null)
            {
                var asm = System.Reflection.Assembly.LoadFrom(appDll);
                var dialogType = asm.GetType("KiTTYManager.App.ThemedMessageDialog");
                var mainWindowType = asm.GetType("KiTTYManager.App.MainWindow");
                if (mainWindowType is not null)
                {
                    var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(mainWindowType);
                    var startupGatesField = mainWindowType.GetField("jumphostStartupGates",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var accessGatesField = mainWindowType.GetField("accessScriptGates",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var processesField = mainWindowType.GetField("jumphostProcesses",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var readyMethod = mainWindowType.GetMethod("IsProxyReadyAndPreparedAsync",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (startupGatesField is not null && accessGatesField is not null && readyMethod is not null)
                    {
                        var startupDict = new System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim>();
                        var accessDict = new System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim>();
                        startupGatesField.SetValue(instance, startupDict);
                        accessGatesField.SetValue(instance, accessDict);
                        if (processesField is not null)
                            processesField.SetValue(instance, Activator.CreateInstance(processesField.FieldType, nonPublic: true)!);
                        var configField = mainWindowType.GetField("config",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (configField is not null)
                            configField.SetValue(instance, new ManagerConfig { BaseProxies = [] });

                        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        listener.Start();
                        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                        var clientConnected = new TaskCompletionSource<bool>();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var client = await listener.AcceptTcpClientAsync();
                                clientConnected.TrySetResult(true);
                                await Task.Delay(10000);
                            }
                            catch { }
                        });

                        var testProxy = new BaseProxy { Id = Guid.NewGuid(), Host = "127.0.0.1", Port = port };
                        using var cts = new CancellationTokenSource();
                        var probeTask = (Task<bool>)readyMethod.Invoke(instance, [testProxy, TimeSpan.FromSeconds(5), cts.Token])!;

                        // Wait until SOCKS connect is actively established and waiting
                        clientConnected.Task.Wait(TimeSpan.FromSeconds(2));
                        var sGate = startupDict.GetOrAdd(testProxy.Id, _ => new SemaphoreSlim(1, 1));
                        var aGate = accessDict.GetOrAdd(testProxy.Id, _ => new SemaphoreSlim(1, 1));
                        Equal(0, sGate.CurrentCount);
                        Equal(0, aGate.CurrentCount);

                        cts.Cancel();
                        try { probeTask.GetAwaiter().GetResult(); } catch (Exception) { }

                        Equal(1, sGate.CurrentCount);
                        Equal(1, aGate.CurrentCount);

                        // Regression test: Connect without routeConfig (shared callers like WinSCP, batch tasks, tunnels)
                        var connectMethod = mainWindowType.GetMethod("Connect",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (connectMethod is not null)
                        {
                            var procRegistry = processesField?.GetValue(instance);
                            var rememberMethod = procRegistry?.GetType().GetMethod("Remember");
                            var jhProxyId = Guid.NewGuid();
                            var testServer = new ManagedServer { Id = Guid.NewGuid(), Name = "Server1", Host = "10.0.0.1", Port = 22, PreferredProxyId = jhProxyId };
                            var managedRunningProxy = new BaseProxy { Id = jhProxyId, Host = "127.0.0.1", Port = 54321, StartupServerId = testServer.Id, UseAutomaticPort = true };
                            rememberMethod?.Invoke(procRegistry, [managedRunningProxy, 0 /* Entry */, Process.GetCurrentProcess(), "ManagedJH"]);

                            var liveJhProxy = new BaseProxy { Id = jhProxyId, Name = "JH", Host = "127.0.0.1", Port = 0, Enabled = true, StartupServerId = testServer.Id, UseAutomaticPort = true };
                            var liveCfg = new ManagerConfig
                            {
                                BaseProxies = [liveJhProxy],
                                UngroupedServers = [testServer]
                            };
                            configField?.SetValue(instance, liveCfg);

                            var sshField = mainWindowType.GetField("ssh",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            var configuredSsh = new SshConnectionService
                            {
                                HostKeyVerifier = (s, fp) => true,
                                HostKeyMismatchVerifier = (s, oldFp, newFp) => true,
                                Timeout = TimeSpan.FromSeconds(42),
                                EndpointProbeTimeout = TimeSpan.FromSeconds(7)
                            };
                            sshField?.SetValue(instance, configuredSsh);

                            using var connectCts = new CancellationTokenSource();
                            string? reportedEndpoint = null;
                            int? reportedTimeoutSeconds = null;
                            Action<SshConnectionProgress> progressCb = p =>
                            {
                                if (p.TimeoutSeconds > 0)
                                    reportedTimeoutSeconds = p.TimeoutSeconds;
                                if (p.EndpointDisplay is not null)
                                {
                                    reportedEndpoint = p.EndpointDisplay;
                                    connectCts.Cancel();
                                }
                            };

                            var connectTask = (Task)connectMethod.Invoke(instance, [
                                testServer, connectCts.Token, false, null, progressCb, null /* routeConfig: null */,
                                null /* sshService: null */, false, false, null
                            ])!;

                            try { connectTask.GetAwaiter().GetResult(); } catch (Exception) { }

                            Equal(true, reportedEndpoint?.Contains(":54321") == true);
                            Equal(42, reportedTimeoutSeconds);
                            Equal(true, configuredSsh.HostKeyVerifier is not null);
                            Equal(true, configuredSsh.HostKeyMismatchVerifier is not null);
                            Equal("127.0.0.1", liveJhProxy.Host);
                            Equal(0, liveJhProxy.Port);

                            if (procRegistry is JumphostProcessRegistry jreg)
                            {
                                var id = new ManagedJumphostProcess(jhProxyId, JumphostConsoleKind.Entry, Process.GetCurrentProcess().Id, DateTime.UtcNow, "127.0.0.1", 54321, "title", testServer.Id, true);
                                var sameServerLocalhost = new BaseProxy { Id = jhProxyId, Host = "localhost", Port = 0, StartupServerId = testServer.Id, UseAutomaticPort = true };
                                Equal(true, jreg.CanReuseManaged(sameServerLocalhost, id));

                                var diffHostProxy = new BaseProxy { Id = jhProxyId, Host = "192.168.1.100", Port = 0, StartupServerId = testServer.Id, UseAutomaticPort = true };
                                Equal(false, jreg.CanReuseManaged(diffHostProxy, id));

                                var diffServerProxy = new BaseProxy { Id = jhProxyId, Host = "127.0.0.1", Port = 0, StartupServerId = Guid.NewGuid(), UseAutomaticPort = true };
                                Equal(false, jreg.CanReuseManaged(diffServerProxy, id));
                            }

                            var isProxyReadyMethod = mainWindowType.GetMethod("IsProxyReadyAndPreparedAsync",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (isProxyReadyMethod is not null)
                            {
                                using var socksListenerCts = new CancellationTokenSource();
                                static (System.Net.Sockets.TcpListener Listener, int Port) StartSocksListener(CancellationToken ct)
                                {
                                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                                    listener.Start();
                                    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                                    _ = Task.Run(async () =>
                                    {
                                        while (!ct.IsCancellationRequested)
                                        {
                                            try
                                            {
                                                var client = await listener.AcceptTcpClientAsync(ct);
                                                _ = Task.Run(async () =>
                                                {
                                                    using (client)
                                                    {
                                                        var stream = client.GetStream();
                                                        var buf = new byte[3];
                                                        await stream.ReadExactlyAsync(buf, ct);
                                                        if (buf[0] == 0x05)
                                                            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);
                                                    }
                                                }, ct);
                                            }
                                            catch { break; }
                                        }
                                    }, ct);
                                    return (listener, port);
                                }

                                var (listenerA, socksPortA) = StartSocksListener(socksListenerCts.Token);
                                var (listenerExt, socksPortExt) = StartSocksListener(socksListenerCts.Token);

                                try
                                {
                                    var serverA = new ManagedServer { Id = Guid.NewGuid(), Name = "ServerA", Host = "10.0.0.1" };
                                    var serverB = new ManagedServer { Id = Guid.NewGuid(), Name = "ServerB", Host = "10.0.0.2" };
                                    var proxyA = new BaseProxy { Id = Guid.NewGuid(), Host = "127.0.0.1", Port = socksPortA, StartupServerId = serverA.Id, UseAutomaticPort = true };
                                    rememberMethod?.Invoke(procRegistry, [proxyA, 0 /* Entry */, Process.GetCurrentProcess(), "ManagedJH_A"]);

                                    // Same serverA with localhost alias: recognized as reusable and ready
                                    var proxySameA = new BaseProxy { Id = proxyA.Id, Host = "localhost", Port = socksPortA, StartupServerId = serverA.Id, UseAutomaticPort = true };
                                    var readyTaskSame = (Task<bool>)isProxyReadyMethod.Invoke(instance, [proxySameA, TimeSpan.FromSeconds(2), CancellationToken.None])!;
                                    Equal(true, readyTaskSame.GetAwaiter().GetResult());

                                    // Different serverB with localhost alias on same port: detected as conflict -> rejected (false)
                                    var proxyDiffB = new BaseProxy { Id = proxyA.Id, Host = "localhost", Port = socksPortA, StartupServerId = serverB.Id, UseAutomaticPort = true };
                                    var readyTaskDiff = (Task<bool>)isProxyReadyMethod.Invoke(instance, [proxyDiffB, TimeSpan.FromMilliseconds(50), CancellationToken.None])!;
                                    Equal(false, readyTaskDiff.GetAwaiter().GetResult());

                                    // Replacement external SOCKS proxy at a different port -> confirmed ready (true)
                                    var proxyExt = new BaseProxy { Id = proxyA.Id, Host = "localhost", Port = socksPortExt, StartupServerId = null, UseAutomaticPort = false };
                                    var readyTaskExt = (Task<bool>)isProxyReadyMethod.Invoke(instance, [proxyExt, TimeSpan.FromSeconds(2), CancellationToken.None])!;
                                    Equal(true, readyTaskExt.GetAwaiter().GetResult());

                                    // Unregistered working SOCKS endpoint without registry entry -> confirmed ready (true)
                                    var proxyUnreg = new BaseProxy { Id = Guid.NewGuid(), Host = "127.0.0.1", Port = socksPortExt, StartupServerId = null, UseAutomaticPort = false };
                                    var readyTaskUnreg = (Task<bool>)isProxyReadyMethod.Invoke(instance, [proxyUnreg, TimeSpan.FromSeconds(2), CancellationToken.None])!;
                                    Equal(true, readyTaskUnreg.GetAwaiter().GetResult());
                                }
                                finally
                                {
                                    socksListenerCts.Cancel();
                                    listenerA.Stop();
                                    listenerExt.Stop();
                                }
                            }
                        }
                    }
                }
            }
        }

        // 5. JumphostProcessRegistry async stop
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 >nul") { UseShellExecute = false }
                : new ProcessStartInfo("sleep", "30") { UseShellExecute = false };
            using var proc = Process.Start(startInfo)!;
            var pid = proc.Id;
            var proxy = new BaseProxy { Host = "127.0.0.1", Port = 5678 };
            var registry = new JumphostProcessRegistry();
            registry.Remember(proxy, JumphostConsoleKind.Entry, proc, "async-stop-test");
            Equal(true, registry.TryStopAliveManagedAsync(proxy).GetAwaiter().GetResult());
            try
            {
                using var remaining = Process.GetProcessById(pid);
                remaining.WaitForExit(5000);
                Equal(true, remaining.HasExited);
            }
            catch (ArgumentException) { }
            Equal(false, registry.TryStopAliveManagedAsync(proxy).GetAwaiter().GetResult());
        }

        // 6. Snapshot isolation, fallback sync, and learned route data writeback
        {
            var writebackProxyId = Guid.NewGuid();
            var writebackHopId = Guid.NewGuid();
            var writebackTargetId = Guid.NewGuid();
            var live = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Id = writebackProxyId, Name = "JH1", Host = "10.0.0.2", Port = 1080 }],
                UngroupedServers =
                [
                    new ManagedServer { Id = writebackHopId, Name = "Hop1", Host = "10.0.0.10", Port = 22 },
                    new ManagedServer { Id = writebackTargetId, Name = "Target", Host = "10.0.0.1", Port = 22 }
                ],
                Links = [new ServerLink { FromServerId = writebackHopId, ToServerId = writebackTargetId }]
            };
            var snapshot = ConfigTransfer.CloneSnapshot(live);
            var snapshotTarget = snapshot.FindServer(writebackTargetId)!;

            // Mutating live does not affect snapshot
            live.UngroupedServers[1].Host = "10.0.0.99";
            Equal("10.0.0.1", snapshotTarget.Host);

            // Fallback timestamp synchronization
            var now = DateTimeOffset.UtcNow;
            snapshotTarget.LastOriginalSessionFallbackUtc = now;
            var liveTarget = live.FindServer(snapshotTarget.Id);
            if (liveTarget is not null && string.Equals(liveTarget.Host, "10.0.0.99", StringComparison.OrdinalIgnoreCase) && liveTarget.Port == snapshotTarget.Port)
            {
                liveTarget.LastOriginalSessionFallbackUtc = snapshotTarget.LastOriginalSessionFallbackUtc;
            }
            Equal(now, liveTarget?.LastOriginalSessionFallbackUtc);

            // PreferredProxyId synchronization
            snapshotTarget.PreferredProxyId = writebackProxyId;
            if (liveTarget is not null)
            {
                liveTarget.PreferredProxyId = snapshotTarget.PreferredProxyId;
            }
            Equal(writebackProxyId, liveTarget?.PreferredProxyId);

            // Proxy statistics synchronization
            var snapProxy = snapshot.BaseProxies[0];
            snapProxy.LastSuccessUtc = now;
            snapProxy.LastConnectLatencyMs = 42.5;
            var liveProxy = live.BaseProxies.FirstOrDefault(p => p.Id == snapProxy.Id);
            if (liveProxy is not null && (liveProxy.LastSuccessUtc is null || snapProxy.LastSuccessUtc >= liveProxy.LastSuccessUtc))
            {
                liveProxy.LastSuccessUtc = snapProxy.LastSuccessUtc;
                liveProxy.LastConnectLatencyMs = snapProxy.LastConnectLatencyMs;
            }
            Equal(now, liveProxy?.LastSuccessUtc);
            Equal(42.5, liveProxy?.LastConnectLatencyMs);

            // Link statistics synchronization
            var snapLink = snapshot.Links[0];
            snapLink.LastSuccessUtc = now;
            snapLink.LastSuccessfulProxyId = writebackProxyId;
            snapLink.LastLatencyMs = 12.3;
            snapLink.LastStrategy = "ssh";
            snapLink.ProxyStatistics.Add(new LinkProxyStatistic
            {
                ProxyId = writebackProxyId,
                LastSuccessUtc = now,
                LatencyMs = 12.3,
                Strategy = "ssh"
            });

            var liveLink = live.Links.FirstOrDefault(l => l.FromServerId == writebackHopId && l.ToServerId == writebackTargetId);
            if (snapLink is not null && liveLink is not null)
            {
                if (liveLink.LastSuccessUtc is null || snapLink.LastSuccessUtc >= liveLink.LastSuccessUtc)
                {
                    liveLink.LastSuccessUtc = snapLink.LastSuccessUtc;
                    liveLink.LastSuccessfulProxyId = snapLink.LastSuccessfulProxyId;
                    liveLink.LastLatencyMs = snapLink.LastLatencyMs;
                    liveLink.LastStrategy = snapLink.LastStrategy;
                }
                foreach (var stat in snapLink.ProxyStatistics)
                {
                    var existing = liveLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == stat.ProxyId);
                    if (existing is null)
                    {
                        liveLink.ProxyStatistics.Add(new LinkProxyStatistic
                        {
                            ProxyId = stat.ProxyId,
                            LastSuccessUtc = stat.LastSuccessUtc,
                            LatencyMs = stat.LatencyMs,
                            Strategy = stat.Strategy
                        });
                    }
                    else if (stat.LastSuccessUtc >= existing.LastSuccessUtc)
                    {
                        existing.LastSuccessUtc = stat.LastSuccessUtc;
                        existing.LatencyMs = stat.LatencyMs;
                        existing.Strategy = stat.Strategy;
                    }
                }
            }
            Equal(now, liveLink?.LastSuccessUtc);
            Equal(writebackProxyId, liveLink?.LastSuccessfulProxyId);
            Equal(1, liveLink?.ProxyStatistics.Count);
            Equal(12.3, liveLink?.ProxyStatistics[0].LatencyMs);
            Equal("ssh", LinkStatisticsPolicy.ForProxy(liveLink!, writebackProxyId)?.Strategy);

            // Second route completion for same ProxyId with different strategy ("remote-command") and newer timestamp
            var snapLink2 = new ServerLink
            {
                FromServerId = writebackHopId,
                ToServerId = writebackTargetId,
                ProxyStatistics =
                [
                    new LinkProxyStatistic
                    {
                        ProxyId = writebackProxyId,
                        LastSuccessUtc = now.AddMinutes(2),
                        LatencyMs = 8.5,
                        Strategy = "remote-command"
                    }
                ]
            };
            var snapStat2 = snapLink2.ProxyStatistics.FirstOrDefault(s => s.ProxyId == writebackProxyId);
            if (snapStat2 is not null)
            {
                var existing = liveLink!.ProxyStatistics.FirstOrDefault(s => s.ProxyId == writebackProxyId);
                if (existing is null)
                {
                    liveLink.ProxyStatistics.Add(new LinkProxyStatistic
                    {
                        ProxyId = snapStat2.ProxyId,
                        LastSuccessUtc = snapStat2.LastSuccessUtc,
                        LatencyMs = snapStat2.LatencyMs,
                        Strategy = snapStat2.Strategy
                    });
                }
                else if (snapStat2.LastSuccessUtc >= existing.LastSuccessUtc)
                {
                    existing.LastSuccessUtc = snapStat2.LastSuccessUtc;
                    existing.LatencyMs = snapStat2.LatencyMs;
                    existing.Strategy = snapStat2.Strategy;
                }
            }
            Equal(1, liveLink?.ProxyStatistics.Count);
            Equal("remote-command", LinkStatisticsPolicy.ForProxy(liveLink!, writebackProxyId)?.Strategy);
            Equal(8.5, LinkStatisticsPolicy.ForProxy(liveLink!, writebackProxyId)?.LatencyMs);

            // Finding 2 test: CompleteRoute only syncs statistics for the successfully connected candidate proxy
            var proxyXId = Guid.NewGuid();
            var proxyYId = Guid.NewGuid();
            var invalidatedLiveLink = new ServerLink
            {
                FromServerId = writebackHopId,
                ToServerId = writebackTargetId,
                ProxyStatistics = [] // Y previously failed on this hop and its stats were invalidated/cleared
            };

            var snapLinkWithBoth = new ServerLink
            {
                FromServerId = writebackHopId,
                ToServerId = writebackTargetId,
                ProxyStatistics =
                [
                    new LinkProxyStatistic
                    {
                        ProxyId = proxyYId,
                        LastSuccessUtc = now.AddMinutes(-10),
                        LatencyMs = 50.0,
                        Strategy = "direct-tcpip"
                    },
                    new LinkProxyStatistic
                    {
                        ProxyId = proxyXId,
                        LastSuccessUtc = now.AddMinutes(3),
                        LatencyMs = 15.0,
                        Strategy = "remote-command"
                    }
                ]
            };

            // Route succeeded via candidate with Proxy X (not Y)
            var successfulCandidateProxyId = proxyXId;
            var successfulSnapStat = snapLinkWithBoth.ProxyStatistics.FirstOrDefault(s => s.ProxyId == successfulCandidateProxyId);
            if (successfulSnapStat is not null)
            {
                var existing = invalidatedLiveLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == successfulCandidateProxyId);
                if (existing is null)
                {
                    invalidatedLiveLink.ProxyStatistics.Add(new LinkProxyStatistic
                    {
                        ProxyId = successfulSnapStat.ProxyId,
                        LastSuccessUtc = successfulSnapStat.LastSuccessUtc,
                        LatencyMs = successfulSnapStat.LatencyMs,
                        Strategy = successfulSnapStat.Strategy
                    });
                }
                else if (successfulSnapStat.LastSuccessUtc >= existing.LastSuccessUtc)
                {
                    existing.LastSuccessUtc = successfulSnapStat.LastSuccessUtc;
                    existing.LatencyMs = successfulSnapStat.LatencyMs;
                    existing.Strategy = successfulSnapStat.Strategy;
                }
            }

            // Assert that Y remains invalidated (null) and not re-introduced from snapshot
            Equal(null, LinkStatisticsPolicy.ForProxy(invalidatedLiveLink, proxyYId));
            // Assert that X returns fresh success stats
            Equal("remote-command", LinkStatisticsPolicy.ForProxy(invalidatedLiveLink, proxyXId)?.Strategy);
            Equal(15.0, LinkStatisticsPolicy.ForProxy(invalidatedLiveLink, proxyXId)?.LatencyMs);
            Equal(1, invalidatedLiveLink.ProxyStatistics.Count);

            // Finding 2b test: Direct multi-hop connection (WithoutProxy = true, Proxy.Id = Guid.Empty) succeeds
            var directMultiHopLiveLink = new ServerLink
            {
                FromServerId = writebackHopId,
                ToServerId = writebackTargetId,
                ProxyStatistics =
                [
                    new LinkProxyStatistic
                    {
                        ProxyId = Guid.Empty,
                        LastSuccessUtc = now.AddMinutes(-10),
                        LatencyMs = 100.0,
                        Strategy = "direct-tcpip"
                    },
                    new LinkProxyStatistic
                    {
                        ProxyId = proxyYId,
                        LastSuccessUtc = now.AddMinutes(-5),
                        LatencyMs = 50.0,
                        Strategy = "ssh"
                    }
                ]
            };

            var directMultiHopSnapLink = new ServerLink
            {
                FromServerId = writebackHopId,
                ToServerId = writebackTargetId,
                ProxyStatistics =
                [
                    new LinkProxyStatistic
                    {
                        ProxyId = Guid.Empty,
                        LastSuccessUtc = now.AddMinutes(5),
                        LatencyMs = 42.0,
                        Strategy = "direct-tcpip"
                    },
                    new LinkProxyStatistic
                    {
                        ProxyId = proxyYId,
                        LastSuccessUtc = now.AddMinutes(1),
                        LatencyMs = 20.0,
                        Strategy = "ssh"
                    }
                ]
            };

            // CompleteRoute sync logic for direct multi-hop route (successful candidate proxy is Guid.Empty)
            var directSuccessfulProxyId = Guid.Empty;
            var directSnapStat = directMultiHopSnapLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == directSuccessfulProxyId);
            if (directSnapStat is not null)
            {
                var existing = directMultiHopLiveLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == directSuccessfulProxyId);
                if (existing is null)
                {
                    directMultiHopLiveLink.ProxyStatistics.Add(new LinkProxyStatistic
                    {
                        ProxyId = directSnapStat.ProxyId,
                        LastSuccessUtc = directSnapStat.LastSuccessUtc,
                        LatencyMs = directSnapStat.LatencyMs,
                        Strategy = directSnapStat.Strategy
                    });
                }
                else if (directSnapStat.LastSuccessUtc >= existing.LastSuccessUtc)
                {
                    existing.LastSuccessUtc = directSnapStat.LastSuccessUtc;
                    existing.LatencyMs = directSnapStat.LatencyMs;
                    existing.Strategy = directSnapStat.Strategy;
                }
            }

            // Verify Guid.Empty entry updated with fresh timestamp and latency
            Equal(42.0, LinkStatisticsPolicy.ForProxy(directMultiHopLiveLink, Guid.Empty)?.LatencyMs);
            Equal(now.AddMinutes(5), LinkStatisticsPolicy.ForProxy(directMultiHopLiveLink, Guid.Empty)?.LastSuccessUtc);
            // Verify proxy Y's statistics remain untouched
            Equal(50.0, LinkStatisticsPolicy.ForProxy(directMultiHopLiveLink, proxyYId)?.LatencyMs);
            Equal(now.AddMinutes(-5), LinkStatisticsPolicy.ForProxy(directMultiHopLiveLink, proxyYId)?.LastSuccessUtc);

            // Finding 2 & 3 regression tests: CheckSavedLinksAsync modeless check does not overwrite edited live server,
            // does not roll back concurrently confirmed host key, and does not downgrade short preferred route with longer chain
            var checkSourceId = Guid.NewGuid();
            var checkTargetId = Guid.NewGuid();
            var checkLiveConfig = new ManagerConfig
            {
                UngroupedServers =
                [
                    new ManagedServer { Id = checkSourceId, Name = "Source", Host = "10.0.0.1", Port = 22, HostKeyFingerprint = "oldSourceFp" },
                    new ManagedServer { Id = checkTargetId, Name = "Target", Host = "10.0.0.2", Port = 22, HostKeyFingerprint = "oldTargetFp" }
                ],
                Links = []
            };

            var checkSnapConfig = ConfigTransfer.CloneSnapshot(checkLiveConfig);
            var origSourceHost = checkSnapConfig.FindServer(checkSourceId)!.Host;
            var origSourcePort = checkSnapConfig.FindServer(checkSourceId)!.Port;
            var origSourceFingerprint = checkSnapConfig.FindServer(checkSourceId)!.HostKeyFingerprint ?? "";
            var snapTargetEndpoints = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>
            {
                [checkTargetId] = (checkSnapConfig.FindServer(checkTargetId)!.Host,
                                   checkSnapConfig.FindServer(checkTargetId)!.Port,
                                   checkSnapConfig.FindServer(checkTargetId)!.HostKeyFingerprint ?? "")
            };

            // While check is running on snapshot, user edits live config (changes target host from 10.0.0.2 to 192.168.1.200)
            checkLiveConfig.UngroupedServers[1].Host = "192.168.1.200";

            // Snapshot check completes and updates snapshot servers
            checkSnapConfig.FindServer(checkTargetId)!.HostKeyFingerprint = "newConfirmedFpForOldHost";
            var mockCheckResult = new ConnectivityResult(checkTargetId, true, "OK", TimeSpan.FromMilliseconds(25), "ssh", SourceId: checkSourceId);

            // Apply results to live config using CheckSavedLinksAsync logic
            var liveSourceServer = checkLiveConfig.FindServer(checkSourceId);
            var liveTargetServer = checkLiveConfig.FindServer(mockCheckResult.TargetId);
            if (liveSourceServer is not null && liveTargetServer is not null)
            {
                var sourceEndpointMatches = string.Equals(liveSourceServer.Host, origSourceHost, StringComparison.OrdinalIgnoreCase) &&
                                            liveSourceServer.Port == origSourcePort;
                var targetEndpointMatches = snapTargetEndpoints.TryGetValue(mockCheckResult.TargetId, out var ep) &&
                                            string.Equals(liveTargetServer.Host, ep.Host, StringComparison.OrdinalIgnoreCase) &&
                                            liveTargetServer.Port == ep.Port;

                if (sourceEndpointMatches && targetEndpointMatches)
                {
                    var snapDestServer = checkSnapConfig.FindServer(mockCheckResult.TargetId);
                    if (!string.IsNullOrEmpty(snapDestServer?.HostKeyFingerprint) &&
                        !string.Equals(snapDestServer.HostKeyFingerprint, ep.Fingerprint, StringComparison.Ordinal) &&
                        string.Equals(liveTargetServer.HostKeyFingerprint ?? "", ep.Fingerprint, StringComparison.Ordinal))
                    {
                        liveTargetServer.HostKeyFingerprint = snapDestServer.HostKeyFingerprint;
                    }
                    ServerLinkPairPolicy.RememberDirectedSuccess(checkLiveConfig, mockCheckResult, DateTimeOffset.UtcNow);
                }
            }

            // Assert that liveTargetServer retains its original host key, not the confirmed key for the old host
            Equal("oldTargetFp", checkLiveConfig.FindServer(checkTargetId)?.HostKeyFingerprint);
            // Assert that no link was saved in live config for the edited server
            Equal(0, checkLiveConfig.Links.Count);

            // Finding 2b regression: Parallel connection confirmed fresh host key B, snapshot check must NOT roll back to A
            var targetBId = Guid.NewGuid();
            var liveTargetB = new ManagedServer { Id = targetBId, Name = "TargetB", Host = "10.0.0.3", Port = 22, HostKeyFingerprint = "fp_A" };
            var snapTargetB = new ManagedServer { Id = targetBId, Name = "TargetB", Host = "10.0.0.3", Port = 22, HostKeyFingerprint = "fp_A" };
            var snapTargetBEndpoint = (Host: "10.0.0.3", Port: 22, Fingerprint: "fp_A");

            // Parallel connection confirmed fp_B on liveTargetB
            liveTargetB.HostKeyFingerprint = "fp_B";

            // Snapshot check finishes (still has fp_A)
            if (!string.IsNullOrEmpty(snapTargetB.HostKeyFingerprint) &&
                !string.Equals(snapTargetB.HostKeyFingerprint, snapTargetBEndpoint.Fingerprint, StringComparison.Ordinal) &&
                string.Equals(liveTargetB.HostKeyFingerprint ?? "", snapTargetBEndpoint.Fingerprint, StringComparison.Ordinal))
            {
                liveTargetB.HostKeyFingerprint = snapTargetB.HostKeyFingerprint;
            }
            // Assert that fresh fp_B was NOT rolled back to fp_A
            Equal("fp_B", liveTargetB.HostKeyFingerprint);

            // Finding 3 regression: Live destination has short direct route (1 hop), snapshot check finishes via 3 hops
            var directRoute = new CachedRoute { ServerIds = [targetBId] };
            liveTargetB.PreferredRoute = directRoute;
            var threeHopRoute = new CachedRoute { ServerIds = [Guid.NewGuid(), Guid.NewGuid(), targetBId] };
            snapTargetB.PreferredRoute = threeHopRoute;

            if (snapTargetB.PreferredRoute is not null &&
                RoutePreferencePolicy.ShouldReplacePreferred(liveTargetB.PreferredRoute, snapTargetB.PreferredRoute.ServerIds))
            {
                liveTargetB.PreferredRoute = snapTargetB.PreferredRoute;
            }
            // Assert that liveTargetB kept direct 1-hop route instead of being overwritten with 3-hop chain
            Equal(1, liveTargetB.PreferredRoute?.ServerIds.Count);
            Equal(true, ReferenceEquals(directRoute, liveTargetB.PreferredRoute));
        }

        // 7. JumpHost dynamic port reuse, user-edited port protection, and access script snapshot isolation
        {
            // A. JumpHost dynamic port reuse on gate acquisition
            var proxyId = Guid.NewGuid();
            var liveConf = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Id = proxyId, Name = "JH-Dynamic", Host = "127.0.0.1", Port = 54321, UseAutomaticPort = true }]
            };
            var snapConf = ConfigTransfer.CloneSnapshot(liveConf);
            var snapProxy = snapConf.BaseProxies[0];
            snapProxy.Port = 0; // snapshot had 0 before dynamic allocation

            var registry = new JumphostProcessRegistry();
            using var proc = Process.GetCurrentProcess();
            registry.Remember(liveConf.BaseProxies[0], JumphostConsoleKind.Entry, proc, "live-entry");

            if (registry.TryGetAliveManaged(snapProxy, out var existingManaged))
            {
                snapProxy.Port = existingManaged.Port;
                snapProxy.Host = existingManaged.Host;
            }
            Equal(54321, snapProxy.Port); // Reused assigned port from alive process

            // Even if live proxy port is edited while process is alive, reuse exact running port
            liveConf.BaseProxies[0].Port = 99999;
            if (registry.TryGetAliveManaged(snapProxy, out var existingProcess))
            {
                snapProxy.Port = existingProcess.Port;
                snapProxy.Host = existingProcess.Host;
            }
            Equal(54321, snapProxy.Port);

            // B. SyncProxyRuntimeState preserves user-edited port, host, startup server, auto-select, and fresher statistics
            var liveProxy = new BaseProxy { Id = proxyId, Port = 2222, LastSuccessUtc = DateTimeOffset.UtcNow, LastConnectLatencyMs = 15.0 };
            var snapshotProxy = new BaseProxy { Id = proxyId, Port = 54321, LastSuccessUtc = liveProxy.LastSuccessUtc.Value.AddMinutes(-5), LastConnectLatencyMs = 99.0 };

            void ApplySyncProxyRuntime(BaseProxy snap, BaseProxy live, bool syncPort,
                int origPort, string? origHost, Guid? origStartupServerId, bool? origAutoSelect,
                string? origServerHost = null, int origServerPort = 0,
                string? origServerUsername = null, string? origServerPassword = null,
                string? origServerKeyPath = null, string? origServerKeyPassphrase = null,
                ManagedServer? liveServer = null)
            {
                if (syncPort &&
                    live.Port == origPort &&
                    (origHost is null || string.Equals(live.Host, origHost, StringComparison.OrdinalIgnoreCase)) &&
                    (origStartupServerId is null || live.StartupServerId == origStartupServerId) &&
                    (origAutoSelect is null || live.UseAutomaticPort == origAutoSelect.Value))
                {
                    live.Port = snap.Port;
                }
                if (snap.LastSuccessUtc is not null && (live.LastSuccessUtc is null || snap.LastSuccessUtc >= live.LastSuccessUtc))
                {
                    live.LastSuccessUtc = snap.LastSuccessUtc;
                    live.LastConnectLatencyMs = snap.LastConnectLatencyMs;
                }
                var matchesStartupIdentity =
                    (origStartupServerId is null || live.StartupServerId == origStartupServerId) &&
                    live.StartupServerId == snap.StartupServerId;

                if (matchesStartupIdentity && origServerHost is not null && live.StartupServerId is not null)
                {
                    if (liveServer is null ||
                        !string.Equals(liveServer.Host, origServerHost, StringComparison.OrdinalIgnoreCase) ||
                        liveServer.Port != origServerPort ||
                        !string.Equals(liveServer.Username, origServerUsername ?? "", StringComparison.Ordinal) ||
                        !string.Equals(liveServer.Password, origServerPassword ?? "", StringComparison.Ordinal) ||
                        !string.Equals(liveServer.PrivateKeyPath, origServerKeyPath ?? "", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(liveServer.PrivateKeyPassphrase, origServerKeyPassphrase ?? "", StringComparison.Ordinal))
                    {
                        matchesStartupIdentity = false;
                    }
                }

                if (matchesStartupIdentity)
                {
                    if (snap.LastStartupLatencyMs is not null)
                    {
                        live.LastStartupLatencyMs = snap.LastStartupLatencyMs;
                    }
                    if (snap.LastStartupFailureUtc is null || snap.LastStartupFailureUtc >= (live.LastStartupFailureUtc ?? DateTimeOffset.MinValue))
                    {
                        live.LastStartupFailureUtc = snap.LastStartupFailureUtc;
                    }
                }
            }

            // User edited port while launch was in flight (originalPort was 1080)
            ApplySyncProxyRuntime(snapshotProxy, liveProxy, syncPort: true, origPort: 1080, origHost: null, origStartupServerId: null, origAutoSelect: null);
            Equal(2222, liveProxy.Port); // User-edited port 2222 preserved

            // Auto-allocated port (originalPort == 0) propagates to live config when unedited
            var startupServerId = Guid.NewGuid();
            var uneditedLive = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0, StartupServerId = startupServerId, UseAutomaticPort = true };
            var uneditedSnap = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 54321, StartupServerId = startupServerId, UseAutomaticPort = true };
            ApplySyncProxyRuntime(uneditedSnap, uneditedLive, syncPort: true, origPort: 0, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(54321, uneditedLive.Port);

            // User edited host while port was 0 -> dynamic port must NOT overwrite live proxy
            var editedHostLive = new BaseProxy { Id = proxyId, Host = "192.168.1.50", Port = 0, StartupServerId = startupServerId, UseAutomaticPort = true };
            ApplySyncProxyRuntime(uneditedSnap, editedHostLive, syncPort: true, origPort: 0, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(0, editedHostLive.Port);

            // User edited startup server id while port was 0 -> dynamic port must NOT overwrite live proxy
            var editedServerLive = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0, StartupServerId = Guid.NewGuid(), UseAutomaticPort = true };
            ApplySyncProxyRuntime(uneditedSnap, editedServerLive, syncPort: true, origPort: 0, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(0, editedServerLive.Port);

            // User disabled automatic port while port was 0 -> dynamic port must NOT overwrite live proxy
            var editedAutoPortLive = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0, StartupServerId = startupServerId, UseAutomaticPort = false };
            ApplySyncProxyRuntime(uneditedSnap, editedAutoPortLive, syncPort: true, origPort: 0, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(0, editedAutoPortLive.Port);

            // User edited port from 0 to 2222 during auto-startup is preserved
            var editedFromZeroLive = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 2222, StartupServerId = startupServerId, UseAutomaticPort = true };
            ApplySyncProxyRuntime(uneditedSnap, editedFromZeroLive, syncPort: true, origPort: 0, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(2222, editedFromZeroLive.Port); // Manual edit from 0 to 2222 is NOT overwritten

            // Retry attempt port capture:
            // Attempt 1: expectedPort is snapshot proxy.Port (0, P0). Launch selects 54321 (P1). Live config becomes 54321.
            var snapAttemptProxy = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0, StartupServerId = startupServerId, UseAutomaticPort = true };
            var retryLiveProxy = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0, StartupServerId = startupServerId, UseAutomaticPort = true };
            var expectedPortAttempt1 = snapAttemptProxy.Port; // 0
            snapAttemptProxy.Port = 54321; // P1
            ApplySyncProxyRuntime(snapAttemptProxy, retryLiveProxy, syncPort: true, origPort: expectedPortAttempt1, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(54321, retryLiveProxy.Port); // Live becomes P1 (54321)

            // Attempt 2 without user edits: expectedPort is snapshot proxy.Port (54321, P1). Launch selects 54322 (P2).
            var expectedPortAttempt2 = snapAttemptProxy.Port; // 54321
            snapAttemptProxy.Port = 54322; // P2
            ApplySyncProxyRuntime(snapAttemptProxy, retryLiveProxy, syncPort: true, origPort: expectedPortAttempt2, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(54322, retryLiveProxy.Port); // Live becomes P2 (54322)

            // Finding 1 test: User edits liveProxy.Port = 3333 before attempt 2.
            // Attempt 2 runs with expectedPort = proxy.Port (54321 from snapshot proxy).
            var liveWithUserPortEdit = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 54321, StartupServerId = startupServerId, UseAutomaticPort = true };
            liveWithUserPortEdit.Port = 3333; // User edits port to 3333 before attempt 2
            var snapAttempt2 = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 54321, StartupServerId = startupServerId, UseAutomaticPort = true };
            var expectedPortBeforeAttempt2 = snapAttempt2.Port; // 54321 (from snapshot proxy.Port)
            snapAttempt2.Port = 54322; // Attempt 2 allocates 54322 (P2)
            ApplySyncProxyRuntime(snapAttempt2, liveWithUserPortEdit, syncPort: true, origPort: expectedPortBeforeAttempt2, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(3333, liveWithUserPortEdit.Port); // P2 rejected, user's edit 3333 preserved!

            // User edits liveProxy.Host between attempts: P2 does NOT overwrite user's edit
            var retryLiveWithUserEdit = new BaseProxy { Id = proxyId, Host = "192.168.1.99", Port = 54321, StartupServerId = startupServerId, UseAutomaticPort = true };
            ApplySyncProxyRuntime(snapAttempt2, retryLiveWithUserEdit, syncPort: true, origPort: expectedPortBeforeAttempt2, origHost: "127.0.0.1", origStartupServerId: startupServerId, origAutoSelect: true);
            Equal(54321, retryLiveWithUserEdit.Port); // Dynamic port P2 rejected due to Host edit

            // Finding 3 regression test: Failure cooldown from old JumpHost must not block corrected settings
            var serverS1 = Guid.NewGuid();
            var serverS2 = Guid.NewGuid();
            var reconfiguredLiveProxy = new BaseProxy
            {
                Id = proxyId,
                StartupServerId = serverS2, // User changed to new server S2
                LastStartupFailureUtc = null
            };
            var failedSnapProxy = new BaseProxy
            {
                Id = proxyId,
                StartupServerId = serverS1, // Failed launch was on old server S1
                LastStartupFailureUtc = DateTimeOffset.UtcNow,
                LastStartupLatencyMs = 999.0
            };
            ApplySyncProxyRuntime(failedSnapProxy, reconfiguredLiveProxy, syncPort: false, origPort: 0, origHost: null, origStartupServerId: serverS1, origAutoSelect: null);
            Equal(null, reconfiguredLiveProxy.LastStartupFailureUtc); // Cooldown NOT assigned to reconfigured proxy
            Equal(null, reconfiguredLiveProxy.LastStartupLatencyMs);

            // If StartupServerId was unedited, cooldown IS recorded
            var matchingLiveProxy = new BaseProxy
            {
                Id = proxyId,
                StartupServerId = serverS1,
                LastStartupFailureUtc = null
            };
            ApplySyncProxyRuntime(failedSnapProxy, matchingLiveProxy, syncPort: false, origPort: 0, origHost: null, origStartupServerId: serverS1, origAutoSelect: null);
            Equal(failedSnapProxy.LastStartupFailureUtc, matchingLiveProxy.LastStartupFailureUtc);
            Equal(999.0, matchingLiveProxy.LastStartupLatencyMs);

            // Finding 4 regression test: Failure cooldown must not be assigned if user corrected server host or password
            var liveServerWithTypo = new ManagedServer
            {
                Id = serverS1,
                Host = "10.0.0.1",
                Port = 22,
                Username = "user",
                Password = "initialPassword",
                PrivateKeyPath = "",
                PrivateKeyPassphrase = ""
            };
            var liveProxyForTypo = new BaseProxy
            {
                Id = proxyId,
                StartupServerId = serverS1,
                LastStartupFailureUtc = null
            };
            // User corrects host from 10.0.0.1 to 10.0.0.2 during failed attempt
            liveServerWithTypo.Host = "10.0.0.2";
            ApplySyncProxyRuntime(failedSnapProxy, liveProxyForTypo, syncPort: false, origPort: 0, origHost: null,
                origStartupServerId: serverS1, origAutoSelect: null,
                origServerHost: "10.0.0.1", origServerPort: 22, origServerUsername: "user",
                origServerPassword: "initialPassword", origServerKeyPath: "", origServerKeyPassphrase: "",
                liveServer: liveServerWithTypo);
            Equal(null, liveProxyForTypo.LastStartupFailureUtc); // Cooldown NOT assigned to server with corrected Host!

            // User corrects password from initialPassword to correctedPassword
            liveServerWithTypo.Host = "10.0.0.1"; // Host restored
            liveServerWithTypo.Password = "correctedPassword";
            ApplySyncProxyRuntime(failedSnapProxy, liveProxyForTypo, syncPort: false, origPort: 0, origHost: null,
                origStartupServerId: serverS1, origAutoSelect: null,
                origServerHost: "10.0.0.1", origServerPort: 22, origServerUsername: "user",
                origServerPassword: "initialPassword", origServerKeyPath: "", origServerKeyPassphrase: "",
                liveServer: liveServerWithTypo);
            Equal(null, liveProxyForTypo.LastStartupFailureUtc); // Cooldown NOT assigned to server with corrected Password!

            // Stale stats do not overwrite fresher stats
            if (snapshotProxy.LastSuccessUtc is not null && (liveProxy.LastSuccessUtc is null || snapshotProxy.LastSuccessUtc >= liveProxy.LastSuccessUtc))
            {
                liveProxy.LastSuccessUtc = snapshotProxy.LastSuccessUtc;
                liveProxy.LastConnectLatencyMs = snapshotProxy.LastConnectLatencyMs;
            }
            Equal(15.0, liveProxy.LastConnectLatencyMs); // Fresher latency preserved

            // C. Access script execution preserves snapshot commands
            var server = new ManagedServer { Id = Guid.NewGuid(), Name = "S1" };
            var snapProxyWithScript = new BaseProxy { Id = proxyId, StartupServerId = server.Id, PostLoginCommand = "snapshot original command" };
            var steps = JumphostStartupPlan.BuildPostLogin(snapProxyWithScript, server, true);
            Equal("snapshot original command", steps[0].Response);

            // D. Concurrent failure cooldown inside gate
            var concurrentLiveProxy = new BaseProxy { Id = proxyId, LastStartupFailureUtc = DateTimeOffset.UtcNow };
            var concurrentSnapProxy = new BaseProxy { Id = proxyId, LastStartupFailureUtc = null };
            if (concurrentLiveProxy.LastStartupFailureUtc is not null &&
                (concurrentSnapProxy.LastStartupFailureUtc is null || concurrentLiveProxy.LastStartupFailureUtc > concurrentSnapProxy.LastStartupFailureUtc))
            {
                concurrentSnapProxy.LastStartupFailureUtc = concurrentLiveProxy.LastStartupFailureUtc;
            }
            Equal(concurrentLiveProxy.LastStartupFailureUtc, concurrentSnapProxy.LastStartupFailureUtc);

            // E. Real gate nesting behavior: both startupGate and accessGate must be held throughout SOCKS probe
            var startupGate = new SemaphoreSlim(1, 1);
            var accessGate = new SemaphoreSlim(1, 1);
            var probeRunning = false;
            var probeCompleted = false;

            async Task<bool> SimulatedIsProxyReadyAndPreparedAsync(CancellationToken ct)
            {
                await startupGate.WaitAsync(ct);
                try
                {
                    await accessGate.WaitAsync(ct);
                    try
                    {
                        probeRunning = true;
                        await Task.Delay(150, ct); // simulate IsSocks5ReadyAsync network probe
                        return true;
                    }
                    finally
                    {
                        probeRunning = false;
                        accessGate.Release();
                    }
                }
                finally
                {
                    startupGate.Release();
                }
            }

            // 1. While probe is in flight, BOTH gates must be held (CurrentCount == 0)
            using var probeCts = new CancellationTokenSource();
            var runTask = SimulatedIsProxyReadyAndPreparedAsync(probeCts.Token);
            Thread.Sleep(50);
            Equal(true, probeRunning);
            Equal(0, startupGate.CurrentCount);
            Equal(0, accessGate.CurrentCount);
            runTask.GetAwaiter().GetResult();
            Equal(1, startupGate.CurrentCount);
            Equal(1, accessGate.CurrentCount);

            // 2. While an access restore script holds accessGate, readiness check waits and cannot complete probe
            accessGate.Wait();
            var blockedTask = Task.Run(async () =>
            {
                await SimulatedIsProxyReadyAndPreparedAsync(CancellationToken.None);
                probeCompleted = true;
            });
            Thread.Sleep(50);
            Equal(false, probeCompleted); // Blocked because accessGate is held
            accessGate.Release();
            blockedTask.GetAwaiter().GetResult();
            Equal(true, probeCompleted);

            // 3. Preparation-to-connection regression check:
            // Two snapshots captured before port allocation (proxy.Port == 0).
            // Opening 1 starts JumpHost on dynamic port P (54321).
            // User edits live proxy to endpoint B ("192.168.1.100", port 9999).
            // Opening 2 tests readiness/preparation:
            // - snapshot 2 proxy endpoint is updated to dynamic port P (54321) so SSH connects through P.
            // - live proxy endpoint remains B ("192.168.1.100", 9999) without mutation.
            // - EnsureManagedJumphostAsync reuses the running process.
            var dynamicPort = 54321;
            var runningManagedEndpoint = (Host: "127.0.0.1", Port: dynamicPort);

            var liveConfigProxy = new BaseProxy { Id = proxyId, Host = "192.168.1.100", Port = 9999 };
            var activeConfigForTest = new ManagerConfig { BaseProxies = [liveConfigProxy] };
            bool TestIsLiveConfigProxy(BaseProxy p) =>
                activeConfigForTest.BaseProxies.Any(live => ReferenceEquals(live, p));

            var snapshot1Proxy = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0 };
            var snapshot2Proxy = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 0 };

            // Opening 1 starts managed JumpHost on dynamic port P
            snapshot1Proxy.Port = dynamicPort;

            // Opening 2 tests readiness/preparation for snapshot 2
            var probeHost = snapshot2Proxy.Host;
            var probePort = snapshot2Proxy.Port;
            if (runningManagedEndpoint != default)
            {
                probeHost = runningManagedEndpoint.Host;
                probePort = runningManagedEndpoint.Port;
            }
            Equal("127.0.0.1", probeHost);
            Equal(dynamicPort, probePort);

            var readinessSucceeded = true;
            if (readinessSucceeded && runningManagedEndpoint != default && !TestIsLiveConfigProxy(snapshot2Proxy))
            {
                snapshot2Proxy.Host = runningManagedEndpoint.Host;
                snapshot2Proxy.Port = runningManagedEndpoint.Port;
            }
            Equal("127.0.0.1", snapshot2Proxy.Host);
            Equal(dynamicPort, snapshot2Proxy.Port); // Updated to running port so SSH succeeds

            // Probing live proxy preserves user's manual configuration without mutation
            var liveProbeHost = liveConfigProxy.Host;
            var liveProbePort = liveConfigProxy.Port;
            if (runningManagedEndpoint != default)
            {
                liveProbeHost = runningManagedEndpoint.Host;
                liveProbePort = runningManagedEndpoint.Port;
            }
            if (readinessSucceeded && runningManagedEndpoint != default && !TestIsLiveConfigProxy(liveConfigProxy))
            {
                liveConfigProxy.Host = runningManagedEndpoint.Host;
                liveConfigProxy.Port = runningManagedEndpoint.Port;
            }
            Equal("192.168.1.100", liveConfigProxy.Host); // Preserved
            Equal(9999, liveConfigProxy.Port);            // Preserved

            // EnsureManagedJumphostAsync reuses running process without starting a new one
            var reuseAttemptProxy = snapshot2Proxy;
            var reusedManaged = false;
            var ensureHost = reuseAttemptProxy.Host;
            var ensurePort = reuseAttemptProxy.Port;
            if (runningManagedEndpoint != default)
            {
                ensureHost = runningManagedEndpoint.Host;
                ensurePort = runningManagedEndpoint.Port;
            }
            if (ensureHost == runningManagedEndpoint.Host && ensurePort == runningManagedEndpoint.Port)
            {
                if (!TestIsLiveConfigProxy(reuseAttemptProxy))
                {
                    reuseAttemptProxy.Host = ensureHost;
                    reuseAttemptProxy.Port = ensurePort;
                }
                reusedManaged = true;
            }
            Equal(true, reusedManaged);
            Equal(dynamicPort, reuseAttemptProxy.Port);
            Equal(9999, liveConfigProxy.Port);

            // 4. Live-config Connect call creates operation snapshot:
            var liveCallerConfig = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Id = proxyId, Host = "192.168.1.100", Port = 9999, Enabled = true }],
                UngroupedServers = [new ManagedServer { Id = Guid.NewGuid(), Name = "S1", Host = "10.0.0.1", Port = 22, PreferredProxyId = proxyId }]
            };
            ManagerConfig? nullRouteConfig = null;
            var connectActiveConfig = nullRouteConfig ?? ConfigTransfer.CloneSnapshot(liveCallerConfig);
            Equal(false, ReferenceEquals(connectActiveConfig, liveCallerConfig));
            Equal(false, ReferenceEquals(connectActiveConfig.BaseProxies[0], liveCallerConfig.BaseProxies[0]));
            if (runningManagedEndpoint != default && !liveCallerConfig.BaseProxies.Any(live => ReferenceEquals(live, connectActiveConfig.BaseProxies[0])))
            {
                connectActiveConfig.BaseProxies[0].Host = runningManagedEndpoint.Host;
                connectActiveConfig.BaseProxies[0].Port = runningManagedEndpoint.Port;
            }
            Equal(54321, connectActiveConfig.BaseProxies[0].Port);
            Equal(9999, liveCallerConfig.BaseProxies[0].Port);

            // 5. Connect without injected service preserves configured MainWindow SshConnectionService
            var windowConfiguredSsh = new SshConnectionService
            {
                HostKeyVerifier = (s, fp) => true,
                HostKeyMismatchVerifier = (s, oldFp, newFp) => true,
                Timeout = TimeSpan.FromSeconds(42),
                EndpointProbeTimeout = TimeSpan.FromSeconds(7)
            };
            SshConnectionService? injectedSsh = null;
            var activeSshFallback = injectedSsh ?? windowConfiguredSsh;
            Equal(true, ReferenceEquals(windowConfiguredSsh, activeSshFallback));
            Equal(true, activeSshFallback.HostKeyVerifier is not null);
            Equal(true, activeSshFallback.HostKeyMismatchVerifier is not null);
            Equal(TimeSpan.FromSeconds(42), activeSshFallback.Timeout);
            Equal(TimeSpan.FromSeconds(7), activeSshFallback.EndpointProbeTimeout);

            // F. Deduplication of repeated access script runs via shared live proxy
            var sharedLiveProxy = new BaseProxy { Id = proxyId, LastAccessScriptAttemptUtc = DateTimeOffset.UtcNow, LastAccessScriptResult = "Verified" };
            var reqAfter = sharedLiveProxy.LastAccessScriptAttemptUtc.Value.AddSeconds(-10);
            Equal(true, sharedLiveProxy.LastAccessScriptAttemptUtc != reqAfter);
            Equal("Verified", sharedLiveProxy.LastAccessScriptResult);

            // G. Fresh live proxy re-query after awaits
            var currentConfig = new ManagerConfig
            {
                BaseProxies = [new BaseProxy { Id = proxyId, LastAccessScriptResult = "Initial" }]
            };
            currentConfig.BaseProxies = [new BaseProxy { Id = proxyId, LastAccessScriptResult = "Reloaded" }];
            var freshLive = currentConfig.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
            Equal("Reloaded", freshLive?.LastAccessScriptResult);

            // H. SyncProxyRuntimeState identity check guards cooldown against server edits
            var srvId = Guid.NewGuid();
            var origServer = new ManagedServer { Id = srvId, Host = "10.0.0.1", Port = 22, Password = "secret" };
            var snapConfig = new ManagerConfig { UngroupedServers = [origServer] };
            var liveServer = new ManagedServer { Id = srvId, Host = "10.0.0.1", Port = 22, Password = "secret" };
            var syncLiveProxy = new BaseProxy { Id = proxyId, StartupServerId = srvId };
            var liveConfig = new ManagerConfig { UngroupedServers = [liveServer], BaseProxies = [syncLiveProxy] };
            var syncSnapProxy = new BaseProxy { Id = proxyId, StartupServerId = srvId, LastStartupFailureUtc = DateTimeOffset.UtcNow };

            void SimulateSyncProxyRuntimeState(BaseProxy snap, ManagerConfig? activeCfg)
            {
                var live = liveConfig.BaseProxies.FirstOrDefault(p => p.Id == snap.Id);
                if (live is null) return;
                var matches = live.StartupServerId == snap.StartupServerId;
                if (matches && snap.StartupServerId is not null && activeCfg is not null && !ReferenceEquals(activeCfg, liveConfig) && live.StartupServerId is not null)
                {
                    var sServer = activeCfg.FindServer(snap.StartupServerId.Value);
                    var lServer = liveConfig.FindServer(live.StartupServerId.Value);
                    if (sServer is not null && (lServer is null ||
                        !string.Equals(lServer.Host, sServer.Host, StringComparison.OrdinalIgnoreCase) ||
                        lServer.Port != sServer.Port ||
                        !string.Equals(lServer.Username, sServer.Username, StringComparison.Ordinal) ||
                        !string.Equals(lServer.Password, sServer.Password, StringComparison.Ordinal) ||
                        !string.Equals(lServer.PrivateKeyPath, sServer.PrivateKeyPath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(lServer.PrivateKeyPassphrase, sServer.PrivateKeyPassphrase, StringComparison.Ordinal)))
                    {
                        matches = false;
                    }
                }
                if (matches)
                {
                    live.LastStartupFailureUtc = snap.LastStartupFailureUtc;
                }
            }

            // Without edit, failure cooldown is propagated
            SimulateSyncProxyRuntimeState(syncSnapProxy, snapConfig);
            Equal(syncSnapProxy.LastStartupFailureUtc, syncLiveProxy.LastStartupFailureUtc);

            // When user edits password in live configuration, cooldown is rejected
            syncLiveProxy.LastStartupFailureUtc = null;
            liveServer.Password = "new-password";
            SimulateSyncProxyRuntimeState(syncSnapProxy, snapConfig);
            Equal(null, syncLiveProxy.LastStartupFailureUtc);

            // When user edits host in live configuration, cooldown is rejected
            liveServer.Password = "secret";
            liveServer.Host = "10.0.0.2";
            SimulateSyncProxyRuntimeState(syncSnapProxy, snapConfig);
            Equal(null, syncLiveProxy.LastStartupFailureUtc);

            // I. CheckSavedLinksAsync transfers intermediate hop host key and EndpointPreferences across all servers
            var hopHId = Guid.NewGuid();
            var destTId = Guid.NewGuid();
            var snapH = new ManagedServer { Id = hopHId, Name = "HopH", Host = "10.0.1.1", Port = 22, HostKeyFingerprint = "" };
            var snapT = new ManagedServer { Id = destTId, Name = "DestT", Host = "10.0.1.2", Port = 22, HostKeyFingerprint = "" };
            var linksSnapConfig = new ManagerConfig { UngroupedServers = [snapH, snapT] };

            var initFingerprints = linksSnapConfig.AllServers()
                .ToDictionary(s => s.Id, s => (Host: s.Host, Port: s.Port, Fingerprint: s.HostKeyFingerprint ?? ""));

            var liveH = new ManagedServer { Id = hopHId, Name = "HopH", Host = "10.0.1.1", Port = 22, HostKeyFingerprint = "" };
            var liveT = new ManagedServer { Id = destTId, Name = "DestT", Host = "10.0.1.2", Port = 22, HostKeyFingerprint = "" };
            var linksLiveConfig = new ManagerConfig { UngroupedServers = [liveH, liveT] };

            snapH.HostKeyFingerprint = "SHA256:H_CONFIRMED";
            snapH.HostKeyAlgorithm = "ssh-ed25519";
            snapH.HostKeyBits = 256;
            var prefTime = DateTimeOffset.UtcNow;
            snapH.EndpointPreferences.Add(new EndpointPreference
            {
                ProxyId = proxyId,
                Endpoint = new ServerEndpoint("10.0.1.1", 22),
                LastSuccessUtc = prefTime
            });
            snapT.HostKeyFingerprint = "SHA256:T_CONFIRMED";

            foreach (var hop in linksSnapConfig.AllServers())
            {
                var liveHop = linksLiveConfig.FindServer(hop.Id);
                if (liveHop is null) continue;
                if (!initFingerprints.TryGetValue(hop.Id, out var initial)) continue;
                if (!string.Equals(liveHop.Host, initial.Host, StringComparison.OrdinalIgnoreCase) || liveHop.Port != initial.Port) continue;
                if (!string.IsNullOrEmpty(hop.HostKeyFingerprint) &&
                    !string.Equals(hop.HostKeyFingerprint, initial.Fingerprint, StringComparison.Ordinal) &&
                    string.Equals(liveHop.HostKeyFingerprint ?? "", initial.Fingerprint, StringComparison.Ordinal))
                {
                    liveHop.HostKeyFingerprint = hop.HostKeyFingerprint;
                    liveHop.HostKeyAlgorithm = hop.HostKeyAlgorithm;
                    liveHop.HostKeyBits = hop.HostKeyBits;
                }
                foreach (var snapshotPref in hop.EndpointPreferences)
                {
                    var livePref = liveHop.EndpointPreferences.FirstOrDefault(item =>
                        item.ProxyId == snapshotPref.ProxyId &&
                        item.PreviousServerId == snapshotPref.PreviousServerId);
                    if (livePref is null || livePref.LastSuccessUtc <= snapshotPref.LastSuccessUtc)
                    {
                        var hopContext = snapshotPref.ProxyId is not null
                            ? EndpointContext.Direct(snapshotPref.ProxyId.Value)
                            : (snapshotPref.PreviousServerId is not null ? EndpointContext.Via(snapshotPref.PreviousServerId.Value) : (EndpointContext?)null);
                        ServerEndpointPolicy.Remember(liveHop, snapshotPref.Endpoint, hopContext);
                        if (snapshotPref.LastSuccessUtc != default)
                        {
                            var updatedPref = liveHop.EndpointPreferences.FirstOrDefault(item =>
                                item.ProxyId == snapshotPref.ProxyId &&
                                item.PreviousServerId == snapshotPref.PreviousServerId);
                            if (updatedPref is not null)
                                updatedPref.LastSuccessUtc = snapshotPref.LastSuccessUtc;
                        }
                    }
                }
            }

            Equal("SHA256:H_CONFIRMED", liveH.HostKeyFingerprint);
            Equal("ssh-ed25519", liveH.HostKeyAlgorithm);
            Equal(256, liveH.HostKeyBits);
            Equal(1, liveH.EndpointPreferences.Count);
            Equal(proxyId, liveH.EndpointPreferences[0].ProxyId);
            Equal(22, liveH.EndpointPreferences[0].Endpoint.Port);
            Equal("SHA256:T_CONFIRMED", liveT.HostKeyFingerprint);

            // J. Process stop cancellation throws OperationCanceledException instead of returning false
            using var cancelledCts = new CancellationTokenSource();
            cancelledCts.Cancel();
            var stopCancelledCaught = false;
            try
            {
                cancelledCts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                stopCancelledCaught = true;
            }
            Equal(true, stopCancelledCaught);

            // K. CanReuseManaged test:
            // JumpHost with UseAutomaticPort = true on dynamic port 54321 reuses port with unchanged config.
            // When StartupServerId, Host, or UseAutomaticPort change, CanReuseManaged returns false.
            var autoProxy = new BaseProxy
            {
                Id = proxyId,
                Host = "127.0.0.1",
                Port = 54321,
                StartupServerId = srvId,
                UseAutomaticPort = true
            };
            bool SimulateCanReuseManaged(BaseProxy p, Guid identityProxyId, Guid? identityServerId, string identityHost, int identityPort, bool identityAutoPort)
            {
                if (identityProxyId != p.Id) return false;
                if (identityServerId != p.StartupServerId) return false;
                if (!string.Equals(identityHost, p.Host, StringComparison.OrdinalIgnoreCase)) return false;
                if (p.UseAutomaticPort)
                    return identityAutoPort;
                return identityPort == p.Port;
            }

            // Same config reuses running process
            Equal(true, SimulateCanReuseManaged(autoProxy, proxyId, srvId, "127.0.0.1", 54321, true));

            // Different StartupServerId rejects old process
            var diffServerId = Guid.NewGuid();
            var proxyDiffServer = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 54321, StartupServerId = diffServerId, UseAutomaticPort = true };
            Equal(false, SimulateCanReuseManaged(proxyDiffServer, proxyId, srvId, "127.0.0.1", 54321, true));

            // Different Host rejects old process
            var proxyDiffHost = new BaseProxy { Id = proxyId, Host = "192.168.1.1", Port = 54321, StartupServerId = srvId, UseAutomaticPort = true };
            Equal(false, SimulateCanReuseManaged(proxyDiffHost, proxyId, srvId, "127.0.0.1", 54321, true));

            // Disabling UseAutomaticPort with different port rejects old process
            var proxyDiffAuto = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 12345, StartupServerId = srvId, UseAutomaticPort = false };
            Equal(false, SimulateCanReuseManaged(proxyDiffAuto, proxyId, srvId, "127.0.0.1", 54321, true));

            // Fixed port mode matches only exact port
            var fixedProxy = new BaseProxy { Id = proxyId, Host = "127.0.0.1", Port = 2222, StartupServerId = srvId, UseAutomaticPort = false };
            Equal(true, SimulateCanReuseManaged(fixedProxy, proxyId, srvId, "127.0.0.1", 2222, false));
            Equal(false, SimulateCanReuseManaged(fixedProxy, proxyId, srvId, "127.0.0.1", 3333, false));

            var realRegistry = new JumphostProcessRegistry();
            var realId = new ManagedJumphostProcess(proxyId, JumphostConsoleKind.Entry, 1234, DateTime.UtcNow, "127.0.0.1", 54321, "title", srvId, true);
            Equal(true, realRegistry.CanReuseManaged(autoProxy, realId));
            var autoProxyLocalhost = new BaseProxy { Id = proxyId, Host = "localhost", Port = 54321, StartupServerId = srvId, UseAutomaticPort = true };
            Equal(true, realRegistry.CanReuseManaged(autoProxyLocalhost, realId));

            Equal(false, realRegistry.CanReuseManaged(proxyDiffServer, realId));
            var proxyDiffServerLocalhost = new BaseProxy { Id = proxyId, Host = "localhost", Port = 54321, StartupServerId = diffServerId, UseAutomaticPort = true };
            Equal(false, realRegistry.CanReuseManaged(proxyDiffServerLocalhost, realId));

            Equal(false, realRegistry.CanReuseManaged(proxyDiffHost, realId));
            Equal(false, realRegistry.CanReuseManaged(proxyDiffAuto, realId));

            var realFixedId = new ManagedJumphostProcess(proxyId, JumphostConsoleKind.Entry, 1234, DateTime.UtcNow, "127.0.0.1", 2222, "title", srvId, false);
            Equal(true, realRegistry.CanReuseManaged(fixedProxy, realFixedId));
            var fixedProxyLocalhost = new BaseProxy { Id = proxyId, Host = "localhost", Port = 2222, StartupServerId = srvId, UseAutomaticPort = false };
            Equal(true, realRegistry.CanReuseManaged(fixedProxyLocalhost, realFixedId));

            var realFixedIdMismatch = new ManagedJumphostProcess(proxyId, JumphostConsoleKind.Entry, 1234, DateTime.UtcNow, "127.0.0.1", 3333, "title", srvId, false);
            Equal(false, realRegistry.CanReuseManaged(fixedProxy, realFixedIdMismatch));

            // Test hasIncompatibleEndpointConflict across all 3 scenarios:
            // 1. JumpHost A on port 54321, reconfigured to external SOCKS B on different port (1080) with localhost: conflict is false
            var proxyExternal = new BaseProxy { Id = proxyId, Host = "localhost", Port = 1080, StartupServerId = null, UseAutomaticPort = false };
            var isConflict1 = realId.ProxyId == proxyExternal.Id &&
                              !realRegistry.CanReuseManaged(proxyExternal, realId) &&
                              ProxyEndpointComparer.HostsEquivalent(realId.Host, proxyExternal.Host) &&
                              realId.Port == proxyExternal.Port;
            Equal(false, isConflict1);

            // 2. Unregistered working SOCKS endpoint (no entry in registry): conflict is false
            var proxyUnregistered = new BaseProxy { Id = Guid.NewGuid(), Host = "localhost", Port = 1080, StartupServerId = null, UseAutomaticPort = false };
            var isConflict2 = realRegistry.TryGetAlive(proxyUnregistered, JumphostConsoleKind.Entry, out var reg2) &&
                              !realRegistry.TryGetAliveManaged(proxyUnregistered, out _) &&
                              ProxyEndpointComparer.HostsEquivalent(reg2.Host, proxyUnregistered.Host) &&
                              reg2.Port == proxyUnregistered.Port;
            Equal(false, isConflict2);

            // 3. Incompatible process A occupies same requested endpoint (127.0.0.1 vs localhost on same Port): conflict is true
            var isConflict3 = realId.ProxyId == proxyDiffServerLocalhost.Id &&
                              !realRegistry.CanReuseManaged(proxyDiffServerLocalhost, realId) &&
                              ProxyEndpointComparer.HostsEquivalent(realId.Host, proxyDiffServerLocalhost.Host) &&
                              realId.Port == proxyDiffServerLocalhost.Port;
            Equal(true, isConflict3);

            // L. Background route recovery transfers confirmed intermediate hop key and endpoint preferences
            var probeHopId = Guid.NewGuid();
            var probeTargetId = Guid.NewGuid();
            var probeHopServer = new ManagedServer { Id = probeHopId, Host = "10.0.2.1", Port = 22, HostKeyFingerprint = "" };
            var probeTargetServer = new ManagedServer { Id = probeTargetId, Host = "10.0.2.2", Port = 22, HostKeyFingerprint = "" };
            var probeActiveConfig = new ManagerConfig { UngroupedServers = [probeHopServer, probeTargetServer] };

            var probeInitFingerprints = probeActiveConfig.AllServers()
                .ToDictionary(s => s.Id, s => (Host: s.Host, Port: s.Port, Fingerprint: s.HostKeyFingerprint ?? ""));

            var probeLiveHop = new ManagedServer { Id = probeHopId, Host = "10.0.2.1", Port = 22, HostKeyFingerprint = "" };
            var probeLiveTarget = new ManagedServer { Id = probeTargetId, Host = "10.0.2.2", Port = 22, HostKeyFingerprint = "" };
            var probeLiveConfig = new ManagerConfig { UngroupedServers = [probeLiveHop, probeLiveTarget] };

            // Candidate intermediate hop confirms key and discovers preference
            probeHopServer.HostKeyFingerprint = "SHA256:PROBE_HOP_KEY";
            probeHopServer.HostKeyAlgorithm = "ssh-rsa";
            probeHopServer.HostKeyBits = 4096;
            probeHopServer.EndpointPreferences.Add(new EndpointPreference
            {
                ProxyId = proxyId,
                Endpoint = new ServerEndpoint("10.0.2.1", 22),
                LastSuccessUtc = DateTimeOffset.UtcNow
            });

            // Simulate SyncBackgroundProbeHops
            for (var i = 0; i < 1; i++)
            {
                var hop = probeHopServer;
                var liveHop = probeLiveConfig.FindServer(hop.Id);
                if (liveHop is null) continue;
                var hopContext = EndpointContext.Direct(proxyId);
                var snapshotPref = hop.EndpointPreferences.FirstOrDefault(item =>
                    item.ProxyId == hopContext.ProxyId &&
                    item.PreviousServerId == hopContext.PreviousServerId);
                if (snapshotPref is not null)
                {
                    ServerEndpointPolicy.Remember(liveHop, snapshotPref.Endpoint, hopContext);
                    var updatedPref = liveHop.EndpointPreferences.FirstOrDefault(item =>
                        item.ProxyId == hopContext.ProxyId &&
                        item.PreviousServerId == hopContext.PreviousServerId);
                    if (updatedPref is not null)
                        updatedPref.LastSuccessUtc = snapshotPref.LastSuccessUtc;
                }
                if (!string.IsNullOrEmpty(hop.HostKeyFingerprint) &&
                    probeInitFingerprints.TryGetValue(hop.Id, out var initial) &&
                    string.Equals(initial.Host, liveHop.Host, StringComparison.OrdinalIgnoreCase) &&
                    initial.Port == liveHop.Port &&
                    string.Equals(initial.Fingerprint, liveHop.HostKeyFingerprint ?? "", StringComparison.Ordinal))
                {
                    liveHop.HostKeyFingerprint = hop.HostKeyFingerprint;
                    liveHop.HostKeyAlgorithm = hop.HostKeyAlgorithm;
                    liveHop.HostKeyBits = hop.HostKeyBits;
                }
            }

            Equal("SHA256:PROBE_HOP_KEY", probeLiveHop.HostKeyFingerprint);
            Equal("ssh-rsa", probeLiveHop.HostKeyAlgorithm);
            Equal(4096, probeLiveHop.HostKeyBits);
            Equal(1, probeLiveHop.EndpointPreferences.Count);
            Equal(proxyId, probeLiveHop.EndpointPreferences[0].ProxyId);

            // Remark 2: SyncBackgroundProbeHops does not recreate deleted links
            var linkFromId = probeHopId;
            var linkToId = probeTargetId;
            var deletedToId = Guid.NewGuid();

            var snapLinkExisting = new ServerLink
            {
                FromServerId = linkFromId,
                ToServerId = linkToId,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastLatencyMs = 15.0,
                LastStrategy = "Direct",
                LastSuccessfulProxyId = proxyId,
                ProxyStatistics = [new LinkProxyStatistic { ProxyId = proxyId, LastSuccessUtc = DateTimeOffset.UtcNow, LatencyMs = 15.0, Strategy = "Direct" }]
            };
            var snapLinkDeleted = new ServerLink
            {
                FromServerId = linkFromId,
                ToServerId = deletedToId,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastLatencyMs = 25.0
            };
            probeActiveConfig.Links.Add(snapLinkExisting);
            probeActiveConfig.Links.Add(snapLinkDeleted);

            // In live config, snapLinkExisting exists with older timestamp, but snapLinkDeleted was deleted by user
            var liveLinkExisting = new ServerLink
            {
                FromServerId = linkFromId,
                ToServerId = linkToId,
                LastSuccessUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastLatencyMs = 50.0
            };
            probeLiveConfig.Links.Add(liveLinkExisting);

            // Run SyncBackgroundProbeHops link synchronization logic for both edges
            var candidateHopPairs = new[] { (From: linkFromId, To: linkToId), (From: linkFromId, To: deletedToId) };
            foreach (var (from, to) in candidateHopPairs)
            {
                var snapLink = probeActiveConfig.Links.FirstOrDefault(l => l.FromServerId == from && l.ToServerId == to);
                if (snapLink is null) continue;
                var liveLink = probeLiveConfig.Links.FirstOrDefault(l => l.FromServerId == from && l.ToServerId == to);
                if (liveLink is null) continue; // Deleted link is NOT re-created!

                if (liveLink.LastSuccessUtc is null || snapLink.LastSuccessUtc >= liveLink.LastSuccessUtc)
                {
                    liveLink.LastSuccessUtc = snapLink.LastSuccessUtc;
                    liveLink.LastSuccessfulProxyId = snapLink.LastSuccessfulProxyId;
                    liveLink.LastLatencyMs = snapLink.LastLatencyMs;
                    liveLink.LastStrategy = snapLink.LastStrategy;
                }
                var snapStat = snapLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == proxyId);
                if (snapStat is not null)
                {
                    var existing = liveLink.ProxyStatistics.FirstOrDefault(s => s.ProxyId == proxyId);
                    if (existing is null)
                    {
                        liveLink.ProxyStatistics.Add(new LinkProxyStatistic
                        {
                            ProxyId = snapStat.ProxyId,
                            LastSuccessUtc = snapStat.LastSuccessUtc,
                            LatencyMs = snapStat.LatencyMs,
                            Strategy = snapStat.Strategy
                        });
                    }
                    else if (snapStat.LastSuccessUtc >= existing.LastSuccessUtc)
                    {
                        existing.LastSuccessUtc = snapStat.LastSuccessUtc;
                        existing.LatencyMs = snapStat.LatencyMs;
                        existing.Strategy = snapStat.Strategy;
                    }
                }
            }

            // Deleted link must NOT be added to live config
            Equal(1, probeLiveConfig.Links.Count);
            Equal(false, probeLiveConfig.Links.Any(l => l.ToServerId == deletedToId));

            // Existing link must have updated metrics
            Equal(snapLinkExisting.LastSuccessUtc, liveLinkExisting.LastSuccessUtc);
            Equal(15.0, liveLinkExisting.LastLatencyMs);
            Equal(1, liveLinkExisting.ProxyStatistics.Count);
            Equal(15.0, liveLinkExisting.ProxyStatistics[0].LatencyMs);
        }
    }
}
