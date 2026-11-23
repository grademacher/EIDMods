namespace EIDMods.MapCore;

/// <summary>Cursor over a list that's processed a few items per frame.</summary>
public static class RoundRobin
{
    /// <summary>
    /// Returns the next index (0..count-1) and advances the cursor. <paramref name="completedPass"/> is
    /// true when the returned index is the last item, so the caller can do end-of-pass work (e.g.
    /// compaction) and restart from 0. A cursor beyond a list that shrank starts over at 0.
    /// </summary>
    public static int Next(ref int cursor, int count, out bool completedPass)
    {
        if (cursor >= count || cursor < 0) cursor = 0;
        int index = cursor++;
        completedPass = index == count - 1;
        if (completedPass) cursor = 0;
        return index;
    }
}
