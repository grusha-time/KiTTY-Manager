using System.Text.Json;
using KiTTYManager.App;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void SessionNotePolicyAndPersistenceTests()
    {
        // 1. Policy normalization preserves all non-whitespace text exactly.
        Equal("", SessionNotePolicy.Normalize(null));
        Equal("", SessionNotePolicy.Normalize(""));
        Equal("", SessionNotePolicy.Normalize(" \t\r\n "));
        var multiline = "  first line\n\tsecond line\r\nthird  ";
        Equal(multiline, SessionNotePolicy.Normalize(multiline));
        Equal(false, SessionNotePolicy.HasNote(" \t\r\n "));
        Equal(true, SessionNotePolicy.HasNote("x"));

        // 2. Bounded preview: short text untouched, long text cut with ellipsis,
        // and a surrogate pair is never split.
        Equal("plain text", SessionNotePolicy.Preview("plain text"));
        var longNote = new string('a', SessionNotePolicy.PreviewLimit + 50);
        var preview = SessionNotePolicy.Preview(longNote);
        Equal(true, preview.Length <= SessionNotePolicy.PreviewLimit + 1);
        Equal(true, preview.EndsWith("…", StringComparison.Ordinal));
        var surrogateNote = new string('a', SessionNotePolicy.PreviewLimit - 1) + "😀tail";
        var surrogatePreview = SessionNotePolicy.Preview(surrogateNote);
        Equal(false, surrogatePreview.Any(char.IsSurrogate));
        Equal(true, surrogatePreview.EndsWith("…", StringComparison.Ordinal));

        // 3. Clone and save snapshot copy the new fields.
        var server = new ManagedServer { Name = "n", Host = "127.0.0.1", Note = "line 1\n  line 2" };
        var clone = server.Clone();
        Equal(server.Note, clone.Note);
        Equal(true, clone.HasNote);
        var config = new ManagerConfig { ShowSessionNotes = false };
        Equal(false, config.CloneForSave().ShowSessionNotes);
        config.ShowSessionNotes = true;
        Equal(true, config.CloneForSave().ShowSessionNotes);

        // 4. JSON round-trip; legacy JSON without the property keeps the C# default.
        var serialized = JsonSerializer.Serialize(server);
        Equal(server.Note, JsonSerializer.Deserialize<ManagedServer>(serialized)!.Note);
        Equal(true, JsonSerializer.Deserialize<ManagerConfig>("{}")!.ShowSessionNotes);
        var disabled = JsonSerializer.Deserialize<ManagerConfig>("{\"ShowSessionNotes\":false}")!;
        Equal(false, disabled.ShowSessionNotes);
        Equal(false, JsonSerializer.Deserialize<ManagerConfig>(
            JsonSerializer.Serialize(disabled))!.ShowSessionNotes);

        // 5. ConfigStore Save/Load preserves the note and the toggle; whitespace-only
        // notes normalize to empty on load.
        var directory = Path.Combine(Path.GetTempPath(), "kitty-selftest-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            var saved = Config(new ManagedServer { Name = "saved", Host = "127.0.0.1", Note = "  keep\r\n\tthis  " });
            saved.ShowSessionNotes = false;
            ConfigStore.Save(path, saved);
            var loaded = ConfigStore.Load(path);
            Equal("  keep\r\n\tthis  ", loaded.AllServers().Single().Note);
            Equal(false, loaded.ShowSessionNotes);

            loaded.AllServers().Single().Note = " \t\r\n ";
            ConfigStore.Save(path, loaded);
            Equal("", ConfigStore.Load(path).AllServers().Single().Note);
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        // 6. Rename and group moves keep the note attached to the server.
        var sourceGroup = new ServerGroup { Name = "source" };
        var targetGroup = new ServerGroup { Name = "target" };
        var moving = new ManagedServer { Name = "before", Host = "127.0.0.1", Note = "keep me" };
        var moveConfig = new ManagerConfig { Groups = [sourceGroup, targetGroup] };
        sourceGroup.Servers.Add(moving);
        moving.Name = "after";
        Equal(true, moveConfig.MoveServerToGroup(moving.Id, targetGroup.Id));
        var moved = moveConfig.FindServer(moving.Id)!;
        Equal("after", moved.Name);
        Equal("keep me", moved.Note);
        Equal(true, targetGroup.Servers.Contains(moved));
    }

    private static void SessionNoteImportExportTests()
    {
        // 1. Local empty + incoming note -> field is proposed and checked by default;
        // merge applies the note.
        var localEmpty = NoteConfig(Guid.NewGuid(), "", "local-empty");
        var incomingFilled = NoteConfig(localEmpty.AllServers().Single().Id, "incoming note", "incoming-filled");
        var plan = ImportWizardEngine.Analyze(localEmpty, incomingFilled);
        var noteField = plan.Fields.Single(field => field.PropertyName == "Note");
        Equal(true, noteField.UseIncoming);
        Equal("", noteField.CurrentValue);
        Equal("incoming note", noteField.IncomingValue);
        Equal("incoming note", ImportWizardEngine.Merge(localEmpty, incomingFilled, plan)
            .FindServer(localEmpty.AllServers().Single().Id)!.Note);

        // 2. Local note + incoming empty -> no field; keep-current and whole-session
        // replacement both preserve the local note.
        var localFilled = NoteConfig(Guid.NewGuid(), "local note", "local-filled");
        var serverId = localFilled.AllServers().Single().Id;
        var incomingEmpty = NoteConfig(serverId, "", "incoming-empty");
        var emptyPlan = ImportWizardEngine.Analyze(localFilled, incomingEmpty);
        Equal(0, emptyPlan.Fields.Count(field => field.PropertyName == "Note"));
        Equal("local note", ImportWizardEngine.Merge(localFilled, incomingEmpty, emptyPlan)
            .FindServer(serverId)!.Note);
        emptyPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        Equal("local note", ImportWizardEngine.Merge(localFilled, incomingEmpty, emptyPlan)
            .FindServer(serverId)!.Note);

        // 3. Both present and different -> field proposed unchecked; full cascade
        // leaves the conflict unchecked; whole replacement keeps local unless checked.
        var localBoth = NoteConfig(Guid.NewGuid(), "local both", "local-both");
        var bothId = localBoth.AllServers().Single().Id;
        var incomingBoth = NoteConfig(bothId, "incoming both", "incoming-both");
        var bothPlan = ImportWizardEngine.Analyze(localBoth, incomingBoth);
        var bothField = bothPlan.Fields.Single(field => field.PropertyName == "Note");
        Equal(false, bothField.UseIncoming);
        ImportWizardEngine.ApplyFullImportCascade(bothPlan, [bothId]);
        Equal(false, bothField.UseIncoming);
        bothPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        Equal("local both", ImportWizardEngine.Merge(localBoth, incomingBoth, bothPlan)
            .FindServer(bothId)!.Note);
        bothField.UseIncoming = true;
        Equal("incoming both", ImportWizardEngine.Merge(localBoth, incomingBoth, bothPlan)
            .FindServer(bothId)!.Note);

        // 4. Both empty -> no field and no note.
        var localBlank = NoteConfig(Guid.NewGuid(), " \r\n ", "local-blank");
        var blankId = localBlank.AllServers().Single().Id;
        var incomingBlank = NoteConfig(blankId, "\t", "incoming-blank");
        var blankPlan = ImportWizardEngine.Analyze(localBlank, incomingBlank);
        Equal(0, blankPlan.Fields.Count(field => field.PropertyName == "Note"));
        var blankMerged = ImportWizardEngine.Merge(localBlank, incomingBlank, blankPlan)
            .FindServer(blankId)!.Note;
        Equal(false, SessionNotePolicy.HasNote(blankMerged));
        Equal("", SessionNotePolicy.Normalize(blankMerged));

        // 5. Entry-point sessions still default an empty local note to incoming.
        var localEntry = NoteConfig(Guid.NewGuid(), "", "local-entry");
        var entryId = localEntry.AllServers().Single().Id;
        localEntry.BaseProxies.Add(new BaseProxy { Name = "JH", StartupServerId = entryId });
        var incomingEntry = NoteConfig(entryId, "entry note", "incoming-entry");
        var entryPlan = ImportWizardEngine.Analyze(localEntry, incomingEntry);
        Equal(true, entryPlan.Fields.Single(field => field.PropertyName == "Note").UseIncoming);

        // 6. Applying a selected Note field with empty incoming text never erases
        // the local note.
        var target = new ManagedServer { Name = "target", Note = "local kept" };
        ImportWizardEngine.ApplySelectedFields(target, new ManagedServer { Note = "  " },
            [new ImportFieldDecision { PropertyName = "Note", UseIncoming = true }]);
        Equal("local kept", target.Note);

        // 7. Export carries the note, resets the local UI toggle, and survives
        // ConfigStore export/import round-trip.
        var exportId = Guid.NewGuid();
        var exportSource = NoteConfig(exportId, "export note", "export");
        exportSource.ShowSessionNotes = false;
        var exportConfig = ConfigTransfer.CreateExport(exportSource, [exportId], includeEntryPoints: false);
        Equal("export note", exportConfig.FindServer(exportId)!.Note);
        Equal(true, exportConfig.ShowSessionNotes);
        var directory = Path.Combine(Path.GetTempPath(), "kitty-selftest-note-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "export.json");
            ConfigStore.Export(path, exportConfig);
            var imported = ConfigStore.Import(path);
            Equal("export note", imported.FindServer(exportId)!.Note);
            Equal(true, imported.ShowSessionNotes);
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        // 8. Legacy whole-server merge: explicit incoming keeps the local note when
        // the incoming one is empty and replaces it when non-empty.
        var legacyLocal = NoteConfig(Guid.NewGuid(), "legacy local", "legacy-local");
        var legacyId = legacyLocal.AllServers().Single().Id;
        var legacyIncoming = NoteConfig(legacyId, "", "legacy-incoming");
        var decisions = new Dictionary<(TransferConflictKind Kind, Guid IncomingId), bool>
        {
            [(TransferConflictKind.Server, legacyId)] = true
        };
        var legacyMerged = ConfigTransfer.Merge(legacyLocal, legacyIncoming, decisions);
        Equal("legacy local", legacyMerged.FindServer(legacyId)!.Note);
        Equal("legacy local", legacyLocal.FindServer(legacyId)!.Note);
        legacyIncoming.FindServer(legacyId)!.Note = "legacy incoming";
        var replaced = ConfigTransfer.Merge(legacyLocal, legacyIncoming, decisions);
        Equal("legacy incoming", replaced.FindServer(legacyId)!.Note);
        Equal("legacy local", legacyLocal.FindServer(legacyId)!.Note);
        Equal("legacy incoming", legacyIncoming.FindServer(legacyId)!.Note);

        // 9. Two incoming sessions matched to one local server with different
        // selected notes must be rejected before either note is silently lost.
        var conflictLocal = NoteConfig(Guid.NewGuid(), "", "conflict-local");
        var conflictId = conflictLocal.AllServers().Single().Id;
        var conflictIncoming = new ManagerConfig
        {
            UngroupedServers =
            [
                new ManagedServer
                {
                    Name = "alias-one", Host = "127.0.0.1", Port = 22, Username = "test", Note = "FIRST NOTE"
                },
                new ManagedServer
                {
                    Name = "alias-two", Host = "127.0.0.1", Port = 22, Username = "test", Note = "SECOND NOTE"
                }
            ]
        };
        var conflictPlan = ImportWizardEngine.Analyze(conflictLocal, conflictIncoming);
        Equal(2, conflictPlan.Sessions.Count(session => session.CurrentId == conflictId));
        Equal(2, conflictPlan.Fields.Count(field => field.PropertyName == "Note" && field.UseIncoming));
        ExpectInvalidData(() => ImportWizardEngine.ValidateNoteChoices(conflictPlan));
        ExpectInvalidData(() => ImportWizardEngine.Merge(conflictLocal, conflictIncoming, conflictPlan));
        conflictPlan.Fields.First(field => field.PropertyName == "Note").UseIncoming = false;
        ImportWizardEngine.ValidateNoteChoices(conflictPlan);
        Equal("SECOND NOTE", ImportWizardEngine.Merge(conflictLocal, conflictIncoming, conflictPlan)
            .FindServer(conflictId)!.Note);

        // The same conflict is rejected for whole-session replacement.
        var fullConflictPlan = ImportWizardEngine.Analyze(conflictLocal, conflictIncoming);
        foreach (var session in fullConflictPlan.Sessions)
            session.Decision = ImportDecision.UseIncoming;
        ImportWizardEngine.ApplyFullImportCascade(fullConflictPlan,
            fullConflictPlan.Sessions.Select(session => session.IncomingId));
        ExpectInvalidData(() => ImportWizardEngine.Merge(conflictLocal, conflictIncoming, fullConflictPlan));
        fullConflictPlan.Fields.First(field => field.PropertyName == "Note").UseIncoming = false;
        ImportWizardEngine.ValidateNoteChoices(fullConflictPlan);
        Equal("SECOND NOTE", ImportWizardEngine.Merge(conflictLocal, conflictIncoming, fullConflictPlan)
            .FindServer(conflictId)!.Note);
    }

    private static void SessionNoteUiWiringTests()
    {
        var xamlText = File.ReadAllText(ResolveSourceFile(
            Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml")));
        var doc = System.Xml.Linq.XDocument.Parse(xamlText);
        var ns = doc.Root!.Name.Namespace;
        var xNs = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        // Context menu item stays available independent of icon visibility.
        var noteMenuItem = doc.Descendants(ns + "MenuItem")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Name") == "SessionNoteMenuItem");
        Equal(true, noteMenuItem is not null);
        Equal("Заметка о сервере", (string?)noteMenuItem!.Attribute("Header"));
        Equal("SessionNote_Click", (string?)noteMenuItem.Attribute("Click"));

        // Group column is a template column with ellipsis and an independent note button.
        var groupColumn = doc.Descendants(ns + "DataGridTemplateColumn")
            .FirstOrDefault(element => (string?)element.Attribute("Header") == "Группа");
        Equal(true, groupColumn is not null);
        Equal("Group", (string?)groupColumn!.Attribute("SortMemberPath"));
        Equal(true, ((string?)groupColumn.Attribute("ClipboardContentBinding"))?.Contains("Group") == true);
        var groupText = groupColumn.Descendants(ns + "TextBlock")
            .FirstOrDefault(element => ((string?)element.Attribute("Text"))?.Contains("Group") == true);
        Equal(true, groupText is not null);
        Equal("CharacterEllipsis", (string?)groupText!.Attribute("TextTrimming"));
        var noteButton = groupColumn.Descendants(ns + "Button")
            .FirstOrDefault(element => (string?)element.Attribute("Click") == "SessionNoteButton_Click");
        Equal(true, noteButton is not null);
        Equal(true, ((string?)noteButton!.Attribute("Visibility"))?.Contains("NoteIconVisibility") == true);
        Equal(true, ((string?)noteButton.Attribute("ToolTip"))?.Contains("NotePreview") == true);
        Equal("Открыть заметку о сервере",
            (string?)noteButton.Attribute("AutomationProperties.Name"));

        // Session and server text share the group column's vertical centering.
        var sessionColumn = doc.Descendants(ns + "DataGridTextColumn")
            .FirstOrDefault(element => (string?)element.Attribute("Header") == "Сессия");
        var serverColumn = doc.Descendants(ns + "DataGridTextColumn")
            .FirstOrDefault(element => (string?)element.Attribute("Header") == "Сервер");
        Equal(true, sessionColumn is not null);
        Equal(true, serverColumn is not null);
        Equal(true, ((string?)sessionColumn!.Attribute("ElementStyle"))?.Contains("SessionTextCellStyle") == true);
        Equal(true, ((string?)serverColumn!.Attribute("ElementStyle"))?.Contains("SessionTextCellStyle") == true);
        var textCellStyle = doc.Descendants(ns + "Style")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Key") == "SessionTextCellStyle");
        Equal(true, textCellStyle is not null);
        var verticalCenter = textCellStyle!.Descendants(ns + "Setter")
            .FirstOrDefault(element => (string?)element.Attribute("Property") == "VerticalAlignment");
        Equal("Center", (string?)verticalCenter?.Attribute("Value"));

        // Note preview appears near-instantly: 200 ms instead of the WPF default 1000 ms.
        var noteIconStyle = doc.Descendants(ns + "Style")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Key") == "SessionNoteIconButtonStyle");
        Equal(true, noteIconStyle is not null);
        var initialShowDelay = noteIconStyle!.Descendants(ns + "Setter")
            .FirstOrDefault(element => (string?)element.Attribute("Property") == "ToolTipService.InitialShowDelay");
        Equal("200", (string?)initialShowDelay?.Attribute("Value"));

        // Header toggle with strike-through off state.
        var toggle = doc.Descendants(ns + "ToggleButton")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Name") == "SessionNotesToggleButton");
        Equal(true, toggle is not null);
        Equal("SessionNotesToggle_Click", (string?)toggle!.Attribute("Click"));
        Equal("False", (string?)toggle.Attribute("IsThreeState"));
        Equal("False", (string?)toggle.Attribute("Focusable"));
        Equal(true, xamlText.Contains("x:Name=\"SessionNotesStrike\""));
        Equal(true, xamlText.Contains("RelativeSource AncestorType=ToggleButton") &&
                    xamlText.Contains("Property=\"IsChecked\""));
        Equal(true, xamlText.Contains("Property=\"Visibility\""));
        var searchBox = doc.Descendants(ns + "TextBox")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Name") == "SearchBox");
        Equal(true, ((string?)searchBox!.Attribute("ToolTip"))?.Contains("заметк") == true);

        // Note editor is plain multiline text; Save is not the default button.
        var noteXaml = File.ReadAllText(ResolveSourceFile(
            Path.Combine("src", "KiTTYManager.App", "SessionNoteWindow.xaml")));
        var noteDoc = System.Xml.Linq.XDocument.Parse(noteXaml);
        var noteNs = noteDoc.Root!.Name.Namespace;
        var noteBox = noteDoc.Descendants(noteNs + "TextBox")
            .FirstOrDefault(element => (string?)element.Attribute(xNs + "Name") == "NoteBox");
        Equal(true, noteBox is not null);
        Equal("True", (string?)noteBox!.Attribute("AcceptsReturn"));
        Equal("Wrap", (string?)noteBox.Attribute("TextWrapping"));
        Equal("Auto", (string?)noteBox.Attribute("VerticalScrollBarVisibility"));
        var noteButtons = noteDoc.Descendants(noteNs + "Button").ToList();
        Equal(true, noteButtons.Any(button => (string?)button.Attribute("IsCancel") == "True"));
        var saveButton = noteButtons.FirstOrDefault(button =>
            (string?)button.Attribute("Click") == "Save_Click");
        Equal(true, saveButton is not null);
        Equal(true, saveButton!.Attribute("IsDefault") is null);

        // Code-behind wiring.
        var csText = File.ReadAllText(ResolveSourceFile(
            Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml.cs")));
        var searchableBody = ExtractMethodBody(csText, "static string ComputeSearchableText(");
        Equal(true, ContainsUncommentedCode(searchableBody, "server.Note"));
        var editBody = ExtractMethodBody(csText, "void EditSessionNote(");
        Equal(true, ContainsUncommentedCode(editBody, "new SessionNoteWindow("));
        Equal(true, ContainsUncommentedCode(editBody, "SessionNotePolicy.Normalize"));
        Equal(true, ContainsUncommentedCode(editBody, "SaveConfig();"));
        var toggleBody = ExtractMethodBody(csText, "void SessionNotesToggle_Click(");
        Equal(true, ContainsUncommentedCode(toggleBody, "config.ShowSessionNotes"));
        Equal(true, ContainsUncommentedCode(toggleBody, "SaveConfig();"));
        Equal(true, ContainsUncommentedCode(toggleBody, "RebuildIndex();"));
        Equal(true, ContainsUncommentedCode(toggleBody, "SyncSessionNotesToggle();"));
        var rebuildBody = ExtractMethodBody(csText, "void RebuildIndex(");
        Equal(true, ContainsUncommentedCode(rebuildBody, "SessionNotePolicy.HasNote"));
        Equal(true, ContainsUncommentedCode(rebuildBody, "SessionNotePolicy.Preview"));
        Equal(true, ContainsUncommentedCode(rebuildBody, "config.ShowSessionNotes"));
        var menuBody = ExtractMethodBody(csText, "void SessionsGrid_ContextMenuOpening(");
        Equal(true, ContainsUncommentedCode(menuBody, "SessionNoteMenuItem.IsEnabled"));
        Equal(false, ContainsUncommentedCode(menuBody, "SessionNoteMenuItem.Visibility"));
        var doubleClickBody = ExtractMethodBody(csText, "SessionsGrid_MouseDoubleClick");
        Equal(true, ContainsUncommentedCode(doubleClickBody, "FindParent<Button>"));
        var dragBody = ExtractMethodBody(csText, "void SessionsGrid_PreviewMouseLeftButtonDown(");
        Equal(true, ContainsUncommentedCode(dragBody, "FindParent<Button>"));
        var keyDownBody = ExtractMethodBody(csText, "SessionsGrid_PreviewKeyDown");
        Equal(true, ContainsUncommentedCode(keyDownBody, "FindParent<Button>"));

        var noteCsText = File.ReadAllText(ResolveSourceFile(
            Path.Combine("src", "KiTTYManager.App", "SessionNoteWindow.xaml.cs")));
        var saveBody = ExtractMethodBody(noteCsText, "void Save_Click(");
        Equal(true, ContainsUncommentedCode(saveBody, "DialogResult = true"));

        // Help entry is searchable by the new keywords.
        Equal(true, HelpContent.Search("заметка").Any(entry => entry.Section == "Сессия"));
        Equal(true, HelpContent.Search("иконка предпросмотр").Any(entry => entry.Section == "Сессия"));
        Equal(HelpContent.Entries.Count,
            HelpContent.Entries.Select(entry => (entry.Section, entry.Title)).Distinct().Count());
    }

    private static ManagerConfig NoteConfig(Guid serverId, string note, string name) => new()
    {
        Groups =
        [
            new()
            {
                Name = "notes",
                Servers =
                [
                    new ManagedServer
                    {
                        Id = serverId, Name = name, Host = "127.0.0.1", Port = 22, Username = "test", Note = note
                    }
                ]
            }
        ]
    };
}
