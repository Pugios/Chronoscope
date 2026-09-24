namespace TimeViewer;

// The tables the data pipeline reads, joins and reduces. See ARCHITECTURE.md for the flow.
//
// Every row carries a Duration string straight from ManicTime's CSV, and a DurationSeconds
// computed from Start/End. Nothing sums the string: it was re-parsed at every call site, which
// cost ~5 TimeSpan.Parse calls per row per render and quietly used the ambient culture.
// The two were verified identical across all 86,398 rows of the current export.

// Process -> Tag, as stored in tags.csv
public class TagsTable
{
    public string Process { get; set; } = "";
    public string Tag { get; set; } = "";
}

// ManicTime/Applications, straight out of mtc.exe
public class AppsTable
{
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; } = "";
    public string Process { get; set; } = "";

    public double DurationSeconds => (End - Start).TotalSeconds;

    public override string ToString() => $"{Name} | {Start} | {End} | {Duration} | {Process}";
}

// ManicTime/Documents, straight out of mtc.exe
public class DocumentsTable
{
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; } = "";
    public string Domain { get; set; } = "";

    public double DurationSeconds => (End - Start).TotalSeconds;
}

// Applications joined to tags.csv by Process. Also the pipeline's final output, after the
// Explorer rules have renamed Process and ReduceTable has dropped the document columns.
public class AppsTagsTable
{
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; } = "";
    public string Process { get; set; } = "";
    public string OriginalProcess { get; set; } = "";
    public string Tag { get; set; } = "";

    public double DurationSeconds => (End - Start).TotalSeconds;

    public override string ToString() => $"{Name} | {Start} | {End} | {Duration} | {Process} | {OriginalProcess} | {Tag}";
}

// The above plus the document columns, which is what the Explorer rules match against.
// Kept cached in its own right so ExplorerSettingsPage can preview rules without a reload.
public class AppsTagsDocumentsTable
{
    public string Name { get; set; } = "";
    public string DocName { get; set; } = "";
    public string Domain { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; } = "";
    public string Process { get; set; } = "";
    public string OriginalProcess { get; set; } = "";
    public string Tag { get; set; } = "";

    public double DurationSeconds => (End - Start).TotalSeconds;
}

// One Explorer rule, as stored in explorer-processes.csv. Rules re-tag a multi-purpose app
// (a browser, a file manager) by matching on the document it had open.
public class ExplorerRule
{
    public string Process { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Column { get; set; } = "";     // "Name", "DocName", or "Domain"
    public string MatchType { get; set; } = "";  // "Prefix", "Suffix", or "Include"
    public string Pattern { get; set; } = "";    // "github.com", "C:/Users/Documents/Project", ...
    public int Order { get; set; }               // lowest Order wins; reassigned 1..n on edit

    public ExplorerRule() { }

    // ExplorerSettingsPage edits a working copy so Cancel can genuinely discard it. Without this
    // it held the DataService's own instances, and reassigning Order while redrawing the list
    // reordered the live rules whether or not the user confirmed.
    public ExplorerRule(ExplorerRule other)
    {
        Process = other.Process;
        Tag = other.Tag;
        Column = other.Column;
        MatchType = other.MatchType;
        Pattern = other.Pattern;
        Order = other.Order;
    }
}
