namespace Chronoscope;

// Tag groups: a second, coarser level above the tags. Each tag belongs to at most one group
// ("Coding", "Writing" -> "Work"), stored in tag-groups.csv beside tags.csv.
//
// The pages show groups NEXT TO the tags rather than instead of them: the day's pie and legend
// have a group level, and Statistics and Trend have one more card / line per group. For the
// last two, a group is simply one more "tag" in the data: WithGroupRows feeds every grouped row
// in a second time under the group's key, and the aggregations run on that unchanged.
//
// Since a tag is in one group only, a group's total is exactly the sum of its tags. That is also
// why a group must stay out of anything that adds tags up (a stacked chart, a sum of bars): there
// it would count its hours a second time.
public static class TagGrouping
{
    // Groups shown next to tags need one name space for both, and
    // a group may share its name with a tag. A group's entry is keyed with this prefix; the name
    // shown is the part after it. Stored lists (card order, hidden cards) keep the key as is.
    public const string GroupKeyPrefix = "group:";

    public static string GroupKey(string group) => GroupKeyPrefix + group;
    public static bool IsGroupKey(string key) => key.StartsWith(GroupKeyPrefix, StringComparison.Ordinal);
    public static string NameOf(string key) => IsGroupKey(key) ? key[GroupKeyPrefix.Length..] : key;

    // The rows as they are, followed by a copy of every grouped row keyed by its group: aggregate
    // that once and every tag AND every group gets its own totals. Only the fields the
    // aggregations read are copied.
    public static IEnumerable<AppsTagsTable> WithGroupRows(IEnumerable<AppsTagsTable> rows, IReadOnlyDictionary<string, string> groupByTag)
    {
        foreach (var a in rows)
        {
            yield return a;
            if (groupByTag.TryGetValue(a.Tag, out var group) && !string.IsNullOrWhiteSpace(group))
                yield return new AppsTagsTable { Tag = GroupKey(group), Start = a.Start, End = a.End, Process = a.Process };
        }
    }
}
