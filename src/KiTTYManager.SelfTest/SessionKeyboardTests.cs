using KiTTYManager.App;
using KiTTYManager.Core;

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

        var csPath = ResolveSourceFile(Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml.cs"));
        var csText = File.ReadAllText(csPath);

        // Method body extraction and exact handler assertions
        var findSessionBody = ExtractMethodBody(csText, "FindSession_Executed");
        Equal(true, ContainsUncommentedCode(findSessionBody, "SearchBox.Focus();"));
        Equal(true, ContainsUncommentedCode(findSessionBody, "SearchBox.SelectAll();"));
        Equal(true, ContainsUncommentedCode(findSessionBody, "e.Handled = true;"));

        var searchBoxBody = ExtractMethodBody(csText, "SearchBox_PreviewKeyDown");
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
        var sessionsGrid = Activator.CreateInstance(dataGridType)!;

        windowType.GetField("SearchBox", flags)?.SetValue(window, searchBox);
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
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}
