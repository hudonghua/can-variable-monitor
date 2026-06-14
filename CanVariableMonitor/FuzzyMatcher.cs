namespace CanVariableMonitor;

internal static class FuzzyMatcher
{
    public static List<MapSymbol> Search(IEnumerable<MapSymbol> symbols, string query, int limit = 80)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return symbols.Take(limit).ToList();
        }

        var matches = new List<(MapSymbol Symbol, int Score)>(limit * 2);
        foreach (MapSymbol symbol in symbols)
        {
            int score = Score(symbol.Name, query);
            if (score < 10_000)
            {
                matches.Add((symbol, score));
            }
        }

        matches.Sort(static (a, b) =>
        {
            int byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : a.Symbol.Name.Length.CompareTo(b.Symbol.Name.Length);
        });

        int count = Math.Min(limit, matches.Count);
        var result = new List<MapSymbol>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(matches[i].Symbol);
        }
        return result;
    }

    private static int Score(string candidate, string query)
    {
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 10 + candidate.Length - query.Length;
        int contains = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (contains >= 0) return 100 + contains + candidate.Length - query.Length;
        return IsSubsequence(candidate, query) ? 1000 + candidate.Length : 10_000;
    }

    private static bool IsSubsequence(string candidate, string query)
    {
        int qi = 0;
        foreach (char ch in candidate)
        {
            if (qi < query.Length && char.ToUpperInvariant(ch) == char.ToUpperInvariant(query[qi]))
            {
                qi++;
            }
        }

        return qi == query.Length;
    }
}
