using System.Text;
using Avalonia;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Languages.Markup;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>A file open in a tab: its text, how to write it back, and where its view was left so
/// switching tabs returns to the same carets and scroll.</summary>
public sealed class OpenDocument : Observable
{
    private IHighlighter? parser;
    private IHighlighter? highlighter;
    private IReadOnlyList<Marker> markers = [];
    private IReadOnlyList<string> projectContexts = [];
    private string? projectContext;
    private IReadOnlyList<LineMark> lineMarks = [];
    private IReadOnlyList<LineAnnotation> lineAnnotations = [];
    private IReadOnlyList<LineAnnotation> liveValues = [];
    private IReadOnlyList<InlayHint> inlayHints = [];
    private IReadOnlyList<TestMark> testMarks = [];
    private IReadOnlyList<ColourMark> colourMarks = [];
    private IReadOnlyList<ChangeMark> changeMarks = [];
    private IReadOnlyList<string?> gutterAnnotations = [];
    private IReadOnlyList<int> bookmarks = [];
    private IReadOnlyList<FoldRegion> foldRegions = [];
    private IFoldFinder? finder;
    private bool softWrap;
    private bool showWhitespaces;

    /// <summary>The indentation the file's own lines show, found when its text came from disk; null when they do not tell.</summary>
    private IndentOptions? detected;

    /// <summary>A CR file: each break, held as '\n', is written as '\r'.</summary>
    private bool crBreaks;

    /// <summary>The person chose the line separator, which a save keeps over the .editorconfig's <c>end_of_line</c>.</summary>
    private bool separatorChosen;

    /// <summary>The person chose the encoding, which a save keeps over the .editorconfig's <c>charset</c> and a read from
    /// disk tries first.</summary>
    private bool encodingChosen;

    public OpenDocument(string path, FileText file)
    {
        Path = path;
        Text = new TextDocument(file.Text);
        Encoding = file.Encoding;
        HasBom = file.HasBom;
        EncodingName = file.EncodingName;
        crBreaks = file.CrBreaks;
        Language = Language.ForPath(path);
        detected = Language.Detect(file.Text);
        Conventions = EditorConfig.For(path);
        Indentation = Choose();
        FollowLineEnding(file.Text.Contains('\n', StringComparison.Ordinal));
        parser = Parser();
        highlighter = parser;
        finder = Finder();
        Text.Changed += (_, change) =>
        {
            // Ahead of what moves with the change (the caret, then the breadcrumbs), which found afresh would read the file.
            ServerLines.After(change);
            Selections = TextEditing.MapThrough(Selections, change.Edits).Clamp(change.After.Length);
            if (foldRegions.Count > 0)
            {
                FoldRegion[] moved = new FoldRegion[foldRegions.Count];
                for (int i = 0; i < moved.Length; i++)
                {
                    FoldRegion region = foldRegions[i];
                    moved[i] = region with
                    {
                        Start = TextEditing.MapOffset(region.Start, change.Edits, afterInsertions: true),
                        End = TextEditing.MapOffset(region.End, change.Edits, afterInsertions: true),
                    };
                }
                FoldRegions = moved;
            }
            FollowHeld(change.Edits);
            Raise(nameof(IsModified));
        };
    }

    private string? encodingWarning;

    /// <summary>What the editor's banner says once the text holds a character the file's encoding cannot (windows-1252
    /// has no '→'): that the next save writes the file as UTF-8, said before that save changes every other non-ASCII
    /// byte of it. Null while the encoding holds the whole text.</summary>
    public string? EncodingWarning
    {
        get => encodingWarning;
        private set => Set(ref encodingWarning, value);
    }

    /// <summary>Follows whether the encoding holds the text: only what an edit put in while it does, the whole text while
    /// it does not (so taking the character out again takes the warning away) or when <paramref name="edits"/> is null.</summary>
    private void FollowHeld(IReadOnlyList<TextEdit>? edits)
    {
        if (Encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding)
        {
            EncodingWarning = null;
            return;
        }
        string? unheld = null;
        if (encodingWarning is null && edits is not null)
        {
            foreach (TextEdit edit in edits)
            {
                if (edit.Text.Length > 0 && FileText.Unheld(Encoding, edit.Text) is { } first)
                {
                    unheld = first;
                    break;
                }
            }
        }
        else
        {
            unheld = FileText.Unheld(Encoding, Text.Snapshot.ToString());
        }
        EncodingWarning = unheld is null ? null : $"{EncodingName} cannot hold '{unheld}': the next save writes {Name} as UTF-8";
    }

    /// <summary>Raised after a save, so the language server hears of it.</summary>
    public event EventHandler? Saved;

    /// <summary>A parse of the C# text as it stands landed (<see cref="CSharpHighlighter.Parsed"/>), for work on its
    /// syntax off the UI thread.</summary>
    public event EventHandler<ParsedText>? Parsed;

    /// <summary>The file's C# syntax, parsed as it is typed, which everything else reading it shares rather than parse
    /// the file again; null for a file that is not C#.</summary>
    public CSharpHighlighter? Syntax => parser as CSharpHighlighter;

    /// <summary>Raised when a save wrote the file as UTF-8 because its own encoding could not hold the text, with what
    /// the status bar says of it.</summary>
    public event EventHandler<string>? Converted;

    /// <summary>Raised when an undo or redo of a line separator or encoding change could not write the file, with what the
    /// status bar says; the file is left modified, for the next save to try again.</summary>
    public event EventHandler<string>? WriteFailed;

    /// <summary>Colours from the in-process parser, replaced by the language server's layered over it
    /// once that is running; null for a language with neither.</summary>
    public IHighlighter? Highlighter
    {
        get => highlighter;
        set => Set(ref highlighter, value);
    }

    /// <summary>The language service's markers (diagnostics), in this text's offsets.</summary>
    public IReadOnlyList<Marker> Markers
    {
        get => markers;
        set => Set(ref markers, value);
    }

    private int checks;

    /// <summary>Whether the file's own checks are under way, or waiting for typing to pause or for their server to start
    /// (XAML's, an MSBuild file's, a web language server's, ESLint's), which an agent's diagnostics waits out rather than
    /// all of the IDE's work.</summary>
    public bool IsChecking => checks > 0;

    /// <summary>Holds <see cref="IsChecking"/> until the scope ends, on the UI thread.</summary>
    public IDisposable Checking()
    {
        UiThread.Check();
        checks++;
        return new CheckHeld(this);
    }

    private sealed class CheckHeld(OpenDocument document) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (!released)
            {
                released = true;
                document.checks--;
            }
        }
    }

    private string? notice;

    /// <summary>What the editor's banner says of the file beyond where it comes from: why its language server cannot run
    /// for it (no Node.js), as Rider's editor notifications say it; null for nothing.</summary>
    public string? Notice
    {
        get => notice;
        set => Set(ref notice, value);
    }

    private NoticeAction? noticeAction;

    /// <summary>What the banner offers to do about its notice, as a link after it (Run 'npm install'); null for nothing.</summary>
    public NoticeAction? NoticeAction
    {
        get => noticeAction;
        set => Set(ref noticeAction, value);
    }

    /// <summary>The projects the language server compiles the file in (a target framework of a multi-targeted project
    /// each), by the server's names for them; none while it has not said, or for a file it does not know.</summary>
    public IReadOnlyList<string> ProjectContexts
    {
        get => projectContexts;
        set => Set(ref projectContexts, value);
    }

    /// <summary>The one of <see cref="ProjectContexts"/> the server answers for the file in: its colours, diagnostics and
    /// everything else asked of it.</summary>
    public string? ProjectContext
    {
        get => projectContext;
        set => Set(ref projectContext, value);
    }

    /// <summary>The debugger's marks in the gutter: breakpoints and the execution point.</summary>
    public IReadOnlyList<LineMark> LineMarks
    {
        get => lineMarks;
        set => Set(ref lineMarks, value);
    }

    /// <summary>The tests' run icons beside the test methods and classes it declares.</summary>
    public IReadOnlyList<TestMark> TestMarks
    {
        get => testMarks;
        set => Set(ref testMarks, value);
    }

    /// <summary>The colours its lines write or name (XAML's, C#'s), each a swatch in the gutter.</summary>
    public IReadOnlyList<ColourMark> ColourMarks
    {
        get => colourMarks;
        set => Set(ref colourMarks, value);
    }

    /// <summary>The change bars: the lines that differ from the last commit.</summary>
    public IReadOnlyList<ChangeMark> ChangeMarks
    {
        get => changeMarks;
        set => Set(ref changeMarks, value);
    }

    /// <summary>What folds in the text and which of it is folded, in this text's offsets.</summary>
    public IReadOnlyList<FoldRegion> FoldRegions
    {
        get => foldRegions;
        set => Set(ref foldRegions, value);
    }

    /// <summary>Which of the regions first found in the file fold as it opens (the window's: those folded as it last
    /// closed, else those Settings folds by default); none while unset. A region that holds a caret stays open.</summary>
    public Func<FoldRegion, bool>? FoldsAtFirst { get; set; }

    /// <summary>Whether the file's regions have been found, so which of them are folded is the person's to keep.</summary>
    public bool FoldsFound { get; private set; }

    /// <summary>What finds the file's regions: its C# parse (<see cref="Syntax"/>), or XML's or JSON's scan.</summary>
    public IFoldFinder? FoldFinder => finder;

    /// <summary>The bookmarked lines.</summary>
    public IReadOnlyList<int> Bookmarks
    {
        get => bookmarks;
        set => Set(ref bookmarks, value);
    }

    /// <summary>Text per line at the gutter's left (Git blame's), empty for none.</summary>
    public IReadOnlyList<string?> GutterAnnotations
    {
        get => gutterAnnotations;
        set => Set(ref gutterAnnotations, value);
    }

    /// <summary>The language service's words inside lines (parameter names before arguments), in this text's offsets.</summary>
    public IReadOnlyList<InlayHint> InlayHints
    {
        get => inlayHints;
        set => Set(ref inlayHints, value);
    }

    /// <summary>The debugger's text after lines: inline values, the exception it stopped on.</summary>
    public IReadOnlyList<LineAnnotation> LineAnnotations
    {
        get => lineAnnotations;
        set => Set(ref lineAnnotations, value);
    }

    /// <summary>A debugged Avalonia app's values after an .axaml file's lines: each binding's value on the control it is read
    /// for (LiveTree/BindingValues), shown after <see cref="LineAnnotations"/>.</summary>
    public IReadOnlyList<LineAnnotation> LiveValues
    {
        get => liveValues;
        set => Set(ref liveValues, value);
    }

    /// <summary>What its editors show after its lines: the debugger's annotations, then the app's live values.</summary>
    public IReadOnlyList<LineAnnotation> ShownAnnotations => liveValues.Count == 0 ? lineAnnotations : [.. lineAnnotations, .. liveValues];

    /// <summary>Whether the editors showing the file wrap its lines at their width: Settings' soft wrap masks decide as it
    /// opens, View, Active Editor, Soft-Wrap after.</summary>
    public bool SoftWrap
    {
        get => softWrap;
        set => Set(ref softWrap, value);
    }

    /// <summary>Whether the editors showing the file draw its spaces and tabs: Settings' Show whitespaces as it opens, View,
    /// Active Editor, Show Whitespaces after.</summary>
    public bool ShowWhitespaces
    {
        get => showWhitespaces;
        set => Set(ref showWhitespaces, value);
    }

    /// <summary>Lets go of everything the open file holds beyond its text, when its tab closes.</summary>
    public void Close()
    {
        (Highlighter as IDisposable)?.Dispose();
        (finder as FoldScan)?.Dispose();
    }

    public string Path { get; private set; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Takes the file's new path once it moved on disk, keeping its text, undo, carets and folds; a new name
    /// of another language takes that language's parser.</summary>
    public void MoveTo(string path)
    {
        Path = path;
        Language language = Language.ForPath(path);
        if (language != Language)
        {
            Language = language;
            (parser as IDisposable)?.Dispose();
            (finder as FoldScan)?.Dispose();
            parser = Parser();
            FoldRegions = [];
            FoldsFound = false;
            finder = Finder();
            TestMarks = [];
            Highlighter = parser;
        }
        Raise(nameof(Path));
        Raise(nameof(Name));
        ReadConventions();
    }

    /// <summary>What the .editorconfig files over the file say of it, as last read.</summary>
    public EditorConfig Conventions { get; private set; }

    /// <summary>How Enter, Tab, a paste, a template and the language server's edits indent the file, and why.</summary>
    public FileIndent Indentation { get; private set; }

    /// <summary>Reads the .editorconfig files over the file again (one changed on disk, the file moved) and follows them:
    /// its indentation, and the line break a file with none yet starts with.</summary>
    public void ReadConventions()
    {
        Conventions = EditorConfig.For(Path);
        Raise(nameof(Conventions));
        FollowLineEnding(Text.Snapshot.LineCount > 1);
        Choose(raise: true);
    }

    /// <summary>The indentation from the .editorconfig, else the file's own lines, else its language; raised as changed.</summary>
    private FileIndent Choose(bool raise = false)
    {
        FileIndent chosen = FileIndent.Choose(Conventions, detected, Language.Indent);
        if (raise && chosen != Indentation)
        {
            Indentation = chosen;
            Raise(nameof(Indentation));
        }
        return chosen;
    }

    /// <summary>A file with no line break yet takes the .editorconfig's <c>end_of_line</c> for the first one, unless the
    /// person chose one; one with breaks keeps its own until it is saved.</summary>
    private void FollowLineEnding(bool hasBreak)
    {
        if (!hasBreak && !separatorChosen)
        {
            SetSeparator(Conventions.EndOfLine ?? LineSeparator.LF);
        }
    }

    /// <summary>What Enter inserts: the file's own line break, or the one its .editorconfig gives it ('\n' in a CR file,
    /// whose breaks are held so).</summary>
    public string LineEnding => Text.LineEnding;

    /// <summary>How the file's lines end on disk, as the status bar's line separator widget shows it.</summary>
    public LineSeparator Separator => crBreaks ? LineSeparator.CR : Text.LineEnding == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    /// <summary>The line separator a file read from disk has, by its first break.</summary>
    private static LineSeparator SeparatorOf(FileText file) =>
        file.CrBreaks ? LineSeparator.CR : TextDocument.DetectLineEnding(file.Text) == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    private void SetSeparator(LineSeparator separator)
    {
        if (Separator != separator)
        {
            crBreaks = separator == LineSeparator.CR;
            Text.LineEnding = separator.Held();
            Raise(nameof(LineEnding));
        }
    }

    /// <summary>The line separator widget's tip, Rider's, with what the .editorconfig says of it.</summary>
    public string SeparatorTip
    {
        get
        {
            string tip = $"Line Separator: {Separator.Escaped()}";
            if (Conventions.EndOfLine is not { } asked)
            {
                return tip;
            }
            string said = Conventions.Said("end_of_line")!;
            return asked == Separator ? $"{tip}\nAs the .editorconfig asks: {said}"
                : separatorChosen ? $"{tip}\nChosen over the .editorconfig's {said}"
                : $"{tip}\nThe next save makes it {asked}, as the .editorconfig asks: {said}";
        }
    }

    /// <summary>The encoding widget's tip, Rider's, with what the .editorconfig says of it.</summary>
    public string EncodingTip
    {
        get
        {
            string tip = $"File Encoding: {EncodingName}";
            if (Conventions.Charset is not { } asked)
            {
                return tip;
            }
            string said = Conventions.Said("charset")!;
            return asked.Name == EncodingName ? $"{tip}\nAs the .editorconfig asks: {said}"
                : encodingChosen ? $"{tip}\nChosen over the .editorconfig's {said}"
                : $"{tip}\nThe next save makes it {asked.Name}, as the .editorconfig asks: {said}";
        }
    }

    /// <summary>Rider's Line Separators: every break made <paramref name="to"/>'s and the file written so, as one undoable
    /// step whose undo and redo write it too, and which a save keeps over the .editorconfig's <c>end_of_line</c>.</summary>
    public void ConvertLineSeparators(LineSeparator to)
    {
        IReadOnlyList<TextEdit> edits = TextEditing.Saving(Text.Snapshot.ToString(), Selections, false, false, to.Held(), Text.LineEnding)?.Edits ?? [];
        Change(edits, Current with { Separator = to, SeparatorChosen = true }, write: true);
    }

    /// <summary>What another encoding would do to the file, read again or written in it.</summary>
    public EncodingChange Weigh(FileEncoding to)
    {
        byte[]? bytes;
        try
        {
            bytes = File.Exists(Path) ? FileText.Read(Path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bytes = null;
        }
        return EncodingChange.Weigh(bytes, Text.Snapshot.ToString(), Encoding, crBreaks, to);
    }

    /// <summary>Rider's Reload: the file's bytes read again in the encoding replace the text, as one undoable step; the
    /// file is left as it is.</summary>
    public void ReloadIn(FileEncoding to)
    {
        FileText read = FileText.Decode(FileText.Read(Path), to.Encoding);
        LineSeparator separator = read.Text.Contains('\n', StringComparison.Ordinal) ? SeparatorOf(read) : Separator;
        // UTF-16 is written with its BOM, the only way it is read back as UTF-16.
        bool bom = read.HasBom || to.CodePage is 1200 or 1201;
        Change(LineDiff.Edits(Text.Snapshot.ToString(), read.Text), Current with { Encoding = to.Encoding, Bom = bom, Separator = separator, EncodingChosen = true }, write: false);
    }

    /// <summary>Rider's Convert: the text written in the encoding, as one undoable step whose undo and redo write it too.
    /// A character it cannot hold, converted anyway, becomes '?' in the text as on disk.</summary>
    public void ConvertTo(FileEncoding to) =>
        Change(EncodingChange.Lost(to.Encoding, Text.Snapshot.ToString()), Current with { Encoding = to.Encoding, Bom = to.Bom, EncodingChosen = true }, write: true);

    /// <summary>Rider's change and forget: an encoding that reads the file's bytes as the text it has and writes the text as
    /// the bytes it has (an ASCII file) is taken with nothing read or written, as one undoable step.</summary>
    public void TakeEncoding(FileEncoding to) =>
        Change([], Current with { Encoding = to.Encoding, Bom = to.Bom, EncodingChosen = true }, write: false);

    /// <summary>How the file is stored, which an undoable change of it puts back.</summary>
    private sealed record Stored(Encoding Encoding, bool Bom, LineSeparator Separator, bool SeparatorChosen, bool EncodingChosen);

    private Stored Current => new(Encoding, HasBom, Separator, separatorChosen, encodingChosen);

    /// <summary>One undoable step that changes how the file is stored, and its text with it: undone or redone, the file
    /// goes back to how it was or on again, written when the step wrote it, else saved as far as the file, read in the
    /// encoding taken, is the text.</summary>
    private void Change(IReadOnlyList<TextEdit> edits, Stored after, bool write)
    {
        Stored before = Current;
        SelectionSet carets = Selections;
        TextEdit[] sorted = [.. edits.OrderBy(e => e.Start)];
        string changed = Text.Snapshot.Apply(sorted).ToString();
        // Written before anything changes, so a write that fails leaves the file and the editor as they were.
        (FileText, string?)? written = write ? FileText.Save(Path, changed, after.Encoding, after.Bom, after.Separator == LineSeparator.CR) : null;
        Text.Perform(sorted, carets, TextEditing.MapThrough(carets, sorted).Clamp(changed.Length),
            () => Undoable(() => Settle(before, write)),
            () => Undoable(() => Settle(after, write)));
        Restore(after);
        if (written is { } done)
        {
            Wrote(done);
        }
        else
        {
            MarkSavedIfOnDisk();
        }
    }

    /// <summary>The file as an undo or redo leaves it: stored so, written when the step wrote it.</summary>
    private void Settle(Stored stored, bool write)
    {
        Restore(stored);
        if (write)
        {
            Write(Encoding, HasBom);
        }
        else
        {
            MarkSavedIfOnDisk();
        }
    }

    private void Restore(Stored stored)
    {
        separatorChosen = stored.SeparatorChosen;
        encodingChosen = stored.EncodingChosen;
        SetSeparator(stored.Separator);
        Encoding = stored.Encoding;
        HasBom = stored.Bom;
        EncodingName = FileEncoding.Of(Encoding, HasBom).Name;
        // The widgets' tips name the choices as well.
        Raise(nameof(LineEnding));
        Raise(nameof(EncodingName));
        FollowHeld(null);
    }

    private void MarkSavedIfOnDisk()
    {
        if (OnDisk())
        {
            Text.MarkSaved();
            Raise(nameof(IsModified));
        }
    }

    /// <summary>Whether the file's bytes, read in the encoding taken, are the text as it stands.</summary>
    private bool OnDisk()
    {
        try
        {
            return File.Exists(Path) && FileText.Decode(FileText.Read(Path), Encoding).Text == Text.Snapshot.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An undo or redo's write, which says why when it fails rather than end the key press that asked for it.</summary>
    private void Undoable(Action settle)
    {
        try
        {
            settle();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteFailed?.Invoke(this, $"{Name} could not be saved: {error.Message}");
        }
    }

    /// <summary>The in-process parser's colours and folds for the file's language (C#'s parse, another language's TextMate
    /// grammar); null for a language with neither.</summary>
    private IHighlighter? Parser()
    {
        if (Language.Name != "C#")
        {
            IHighlighter? colours = Languages.Highlighters.For(Language.Name, Text);
            if (colours is Languages.TextMate.TextMateHighlighter grammar)
            {
                grammar.Failed += (_, error) => IdeErrors.Report(error);
            }
            return colours;
        }
        CSharpHighlighter csharp = new(Text);
        csharp.Parsed += (_, parsed) => Parsed?.Invoke(this, parsed);
        return csharp;
    }

    /// <summary>What finds the file's folds: C#'s parse, XML's and JSON's scan; none for another language.</summary>
    private IFoldFinder? Finder()
    {
        IFoldFinder? found = parser as IFoldFinder ?? Language.Name switch
        {
            "XML" => FoldScan.Xml(Text),
            "JSON" => FoldScan.Json(Text),
            _ => null,
        };
        if (found is not null)
        {
            found.FoldsChanged += (_, _) =>
            {
                if (ReferenceEquals(found, finder))
                {
                    TakeFolds(found.Folds);
                }
            };
        }
        return found;
    }

    /// <summary>Regions found for the text as it stands. The first fold as <see cref="FoldsAtFirst"/> says; after, they keep
    /// the folds of the ones they replace, matched by where they start. The regions carried through the keys are usually
    /// the finder's already, and stay; with none folded the finder's own are taken.</summary>
    private void TakeFolds(IReadOnlyList<FoldRegion> found)
    {
        if (!FoldsFound)
        {
            FoldsFound = true;
            FoldRegions = Opening(found);
            return;
        }
        if (Same(foldRegions, found))
        {
            return;
        }
        HashSet<int>? folded = null;
        for (int i = 0; i < foldRegions.Count; i++)
        {
            if (foldRegions[i].Folded)
            {
                (folded ??= []).Add(foldRegions[i].Start);
            }
        }
        if (folded is null)
        {
            FoldRegions = found;
        }
        else
        {
            FoldRegion[] kept = new FoldRegion[found.Count];
            for (int i = 0; i < kept.Length; i++)
            {
                kept[i] = found[i] with { Folded = folded.Contains(found[i].Start) };
            }
            FoldRegions = kept;
        }
    }

    /// <summary>The first regions, folded as the file opens: as <see cref="FoldsAtFirst"/> says, but for one hiding a caret.</summary>
    private IReadOnlyList<FoldRegion> Opening(IReadOnlyList<FoldRegion> found)
    {
        if (FoldsAtFirst is not { } folds || found.Count == 0)
        {
            return found;
        }
        TextSnapshot text = Text.Snapshot;
        int[] carets = [.. Selections.Items.Select(s => text.GetLineFromOffset(Math.Min(s.Caret, text.Length)))];
        FoldRegion[] opened = new FoldRegion[found.Count];
        for (int i = 0; i < opened.Length; i++)
        {
            FoldRegion region = found[i];
            bool hides = region.End <= text.Length && carets.Any(line => line > text.GetLineFromOffset(region.Start) && line <= text.GetLineFromOffset(region.End));
            opened[i] = region with { Folded = !hides && folds(region) };
        }
        return opened;
    }

    /// <summary>Whether two lists of regions fold the same text, whatever is folded.</summary>
    private static bool Same(IReadOnlyList<FoldRegion> kept, IReadOnlyList<FoldRegion> found)
    {
        if (kept.Count != found.Count)
        {
            return false;
        }
        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i].Start != found[i].Start || kept[i].End != found[i].End || kept[i].Placeholder != found[i].Placeholder || kept[i].Kind != found[i].Kind)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the language server made this file from an assembly's metadata (Go to Declaration on a
    /// library's symbol): Roslyn decompiles it, or takes its source from Source Link or the PDB, into a folder of its
    /// own, and nothing typed there would reach the library.</summary>
    public bool IsDecompiled => Path.Contains($"{System.IO.Path.DirectorySeparatorChar}MetadataAsSource{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Whether the editor shows it read-only, under a bar that says why: decompiled, the licence or notices the
    /// app carries, or an MSBuild file of the .NET SDK or a package that Go to Declaration went into.</summary>
    public bool IsReadOnly => IsDecompiled || IsWorkbook || Product.Carries(Path) || MSBuild.MSBuildFiles.IsInstalled(Path);

    /// <summary>Whether this is an Excel workbook, shown as its sheets' cells: its tab holds no text, and nothing is ever
    /// written over it.</summary>
    public bool IsWorkbook => Tables.Workbook.IsWorkbook(Path);

    /// <summary>What the bar over a read-only file says it came from.</summary>
    public string Origin => IsWorkbook ? "An Excel workbook, shown as its sheets' cells, read-only"
        : Product.Carries(Path) ? "Passenger's own file, as the app carries it, read-only"
        : MSBuild.MSBuildFiles.IsInstalled(Path) ? "An MSBuild file of the .NET SDK or a package, as installed, read-only"
        : Path.Contains("SourceLink", StringComparison.OrdinalIgnoreCase) || Path.Contains("Embedded", StringComparison.OrdinalIgnoreCase)
        ? "Source from the library's Source Link or PDB, read-only"
        : "Decompiled code: the language server made this from the assembly, so it is read-only";
    public TextDocument Text { get; }
    /// <summary>How the file is stored, which a save keeps: as it was read, as it is on disk since (a BOM a formatter
    /// added), or UTF-8 once a save needed it.</summary>
    public Encoding Encoding { get; private set; }
    public bool HasBom { get; private set; }
    public string EncodingName { get; private set; }
    public Language Language { get; private set; }
    public bool IsModified => Text.IsModified;
    public Avalonia.Media.Geometry Icon => Explorer.FileIcons.ForFile(Path);
    public Avalonia.Media.IBrush IconBrush => Explorer.FileIcons.BrushForFile(Path);

    /// <summary>The carets: the editor's while the file is shown there, as left otherwise, moved by every change to the text.</summary>
    public SelectionSet Selections { get; set; } = SelectionSet.Single(0);
    public Vector Scroll { get; set; }

    /// <summary>Whether something other than this IDE changed the file's text on disk since it was last loaded or saved.
    /// A change only to how it is stored there (a BOM a formatter added, a conversion to UTF-8) is taken at once, over
    /// unsaved edits too, since they were made over the same text: the next save keeps it, and no question is asked.</summary>
    public bool ChangedOnDisk()
    {
        FileText? file;
        try
        {
            file = File.Exists(Path) ? FileText.Load(Path, Chosen) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (file is null)
        {
            return false;
        }
        if (file.Text != Text.Saved.ToString())
        {
            return true;
        }
        StoreAs(file);
        return false;
    }

    /// <summary>The encoding the person chose for the file, which a read from disk tries before guessing.</summary>
    private Encoding? Chosen => encodingChosen ? Encoding : null;

    /// <summary>Takes the file's text and encoding from disk again when something else changed them there (a checkout, a
    /// rollback), as one undoable step over only the lines that differ, so what is anchored elsewhere stays put; over unsaved
    /// changes only when asked (File Cache Conflict's Load File System Changes). Whether it changed the text.</summary>
    public bool Reload(bool overUnsaved = false)
    {
        FileText? file;
        try
        {
            file = (IsModified && !overUnsaved) || !File.Exists(Path) ? null : FileText.Load(Path, Chosen);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (file is null)
        {
            return false;
        }
        StoreAs(file);
        if (Text.Snapshot.ToString() == file.Text)
        {
            return false;
        }
        Text.SetText(file.Text, Selections);
        Text.MarkSaved();
        if (file.Text.Contains('\n', StringComparison.Ordinal))
        {
            // Enter goes on writing the breaks the file has now.
            SetSeparator(SeparatorOf(file));
        }
        // Text written elsewhere may indent otherwise than what it replaced.
        detected = Language.Detect(file.Text);
        Choose(raise: true);
        Raise(nameof(IsModified));
        return true;
    }

    /// <summary>Writes the file, first doing to its text what its .editorconfig asks of a save (trailing whitespace off
    /// every line without one of <paramref name="carets"/>, a final line break, every break its <c>end_of_line</c>, as
    /// one undoable step) and in its <c>charset</c>; a line separator or encoding the person chose stays.</summary>
    public void Save(SelectionSet? carets = null)
    {
        if (IsWorkbook)
        {
            // Its tab's text is no workbook's: written, it would empty the file.
            throw new IOException($"{Name} is a workbook, shown read-only.");
        }
        ReadConventions();
        string text = Text.Snapshot.ToString();
        SelectionSet kept = carets ?? Selections;
        LineSeparator? asked = separatorChosen ? null : Conventions.EndOfLine;
        if (TextEditing.Saving(text, kept, Conventions.TrimTrailingWhitespace, Conventions.InsertFinalNewline, asked?.Held(), Text.LineEnding) is { } plan)
        {
            Text.BreakUndoGroup();
            Text.Edit(plan.Edits, kept, plan.Selections);
            Text.BreakUndoGroup();
        }
        if (asked is { } separator)
        {
            SetSeparator(separator);
        }
        FileEncoding? charset = encodingChosen ? null : Conventions.Charset;
        Write(charset?.Encoding ?? Encoding, charset?.Bom ?? HasBom);
    }

    /// <summary>Writes the text as it stands, in an encoding and the file's line separator, and takes that as how the
    /// file is stored.</summary>
    private void Write(Encoding encoding, bool bom) => Wrote(FileText.Save(Path, Text.Snapshot.ToString(), encoding, bom, crBreaks));

    /// <summary>Takes how a write stored the file, which now holds the text.</summary>
    private void Wrote((FileText Written, string? Converted) write)
    {
        (FileText written, string? converted) = write;
        StoreAs(written);
        // Written, the text is in the encoding it was written in.
        EncodingWarning = null;
        Text.MarkSaved();
        Raise(nameof(IsModified));
        Saved?.Invoke(this, EventArgs.Empty);
        if (converted is not null)
        {
            Converted?.Invoke(this, converted);
        }
    }

    /// <summary>Takes how the file is stored now, unless that is what it has already in all but name (ASCII reads as
    /// UTF-8 but is the same bytes in Latin-1), so the next save writes it the same way.</summary>
    private void StoreAs(FileText file)
    {
        if (file.CrBreaks != crBreaks && file.Text.Contains('\n', StringComparison.Ordinal))
        {
            SetSeparator(SeparatorOf(file));
        }
        if (file.IsStoredAs(Encoding, HasBom))
        {
            return;
        }
        Encoding = file.Encoding;
        HasBom = file.HasBom;
        EncodingName = file.EncodingName;
        Raise(nameof(EncodingName));
        // The file may be stored otherwise under unsaved edits (a conversion on disk).
        FollowHeld(null);
    }
}

/// <summary>A link the editor's banner offers after its notice, as Rider's editor notifications offer theirs: its words and
/// what it does.</summary>
public sealed record NoticeAction(string Text, Action Run);

/// <summary>What the editor needs to know about a file's language before any language server answers: its name (which
/// <see cref="Languages.Highlighters"/> colours it by), its comments and how it indents. A language with no line comment
/// of its own comments a line with its block comment, as IntelliJ's do (<c>&lt;!-- --&gt;</c> in XML).</summary>
public sealed record Language(string Name, string? LineComment, int IndentSize = 4, string? BlockStart = null, string? BlockEnd = null)
{
    public static readonly Language PlainText = new("Text", null);

    /// <summary>How a file of the language indents when neither an .editorconfig nor its own lines say.</summary>
    public IndentOptions Indent => new(IndentSize);

    /// <summary>The block comment's two ends, or null for a language with none.</summary>
    public (string Start, string End)? BlockComment => BlockStart is { } start && BlockEnd is { } end ? (start, end) : null;

    /// <summary>Whether a pasted block takes the file's indentation: not where indentation is the text's own (Markdown's
    /// code blocks, plain text).</summary>
    public bool ReindentsPaste => Name is not ("Text" or "Markdown");

    /// <summary>Which markup the language is, whose tags indentation follows: XML's (XAML, MSBuild) or HTML's.</summary>
    public MarkupKind Markup => Name switch
    {
        "XML" => MarkupKind.Xml,
        "HTML" => MarkupKind.Html,
        _ => MarkupKind.None,
    };

    /// <summary>The indentation a file's own lines show, read as tags where the language is markup.</summary>
    public IndentOptions? Detect(ReadOnlySpan<char> text) =>
        Passenger.Text.Indentation.Detect(text, Indent.TabWidth, markup: Markup != MarkupKind.None);

    private static readonly Language Xml = new("XML", null, 2, "<!--", "-->");

    public static Language ForPath(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => new Language("C#", "//", 4, "/*", "*/"),
            ".razor" or ".cshtml" => new Language("Razor", null, 4, "@*", "*@"),
            ".csproj" or ".fsproj" or ".vbproj" or ".proj" or ".props" or ".targets" or ".xml" or ".axaml" or ".xaml" or ".slnx"
                or ".resx" or ".config" or ".nuspec" or ".manifest" or ".runsettings" or ".xsd" or ".xslt" or ".xsl" or ".svg"
                or ".plist" or ".storyboard" => Xml,
            ".json" or ".jsonc" => new Language("JSON", "//", 2, "/*", "*/"),
            ".yml" or ".yaml" => new Language("YAML", "#", 2),
            ".md" or ".markdown" => new Language("Markdown", null, 2, "<!--", "-->"),
            ".html" or ".htm" => new Language("HTML", null, 4, "<!--", "-->"),
            ".css" => new Language("CSS", null, 4, "/*", "*/"),
            ".js" or ".mjs" or ".cjs" or ".jsx" => new Language("JavaScript", "//", 4, "/*", "*/"),
            ".ts" or ".mts" or ".cts" or ".tsx" => new Language("TypeScript", "//", 4, "/*", "*/"),
            ".sh" or ".bash" or ".zsh" => new Language("Shell", "#"),
            ".dockerfile" => new Language("Dockerfile", "#"),
            ".scss" or ".less" or ".rs" or ".go" or ".java" or ".kt" or ".swift" or ".c" or ".h" or ".cpp" => new Language("Code", "//", 4, "/*", "*/"),
            ".py" or ".toml" or ".ps1" or ".rb" => new Language("Script", "#"),
            _ when name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase) => new Language("Dockerfile", "#"),
            _ when name.Equals("justfile", StringComparison.OrdinalIgnoreCase) => new Language("Script", "#"),
            _ => PlainText,
        };
    }
}
