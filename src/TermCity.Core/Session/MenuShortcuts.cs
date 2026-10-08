namespace TermCity.Core.Session;

public readonly record struct MenuShortcut(char Letter, int UnderlineIndex, bool Shift = false)
{
    public string DisplayLabel(string label) => UnderlineIndex < 0
        ? $"{label} [{Letter}]" : Shift ? $"{label} [Shift]" : label;
}

public static class MenuShortcuts
{
    public static IReadOnlyList<MenuShortcut> Assign(IReadOnlyList<SessionChoice> choices)
    {
        var candidates = choices.Select(choice => choice.Label
            .Where(char.IsAsciiLetter).Select(char.ToUpperInvariant).Distinct().ToArray()).ToArray();
        var owners = new Dictionary<char, int>();
        var primary = new bool[choices.Count];
        var letters = new char[choices.Count];
        for (int i = 0; i < choices.Count; i++)
            if (candidates[i].Length > 0 && owners.TryAdd(candidates[i][0], i))
            {
                primary[i] = true;
                letters[i] = candidates[i][0];
            }

        bool AssignLetter(int index, HashSet<int> visited)
        {
            if (!visited.Add(index)) return false;
            foreach (char letter in candidates[index])
                if (!owners.ContainsKey(letter))
                {
                    owners[letter] = index;
                    letters[index] = letter;
                    return true;
                }
            foreach (char letter in candidates[index])
            {
                int owner = owners[letter];
                if (!primary[owner] && AssignLetter(owner, visited))
                {
                    owners[letter] = index;
                    letters[index] = letter;
                    return true;
                }
            }
            return false;
        }

        for (int i = 0; i < choices.Count; i++)
            if (letters[i] == '\0') AssignLetter(i, []);
        var shifted = new HashSet<char>();
        var result = new MenuShortcut[choices.Count];
        for (int i = 0; i < choices.Count; i++)
        {
            bool shift = false;
            char letter = letters[i];
            if (letter == '\0')
            {
                // Large menus can exhaust the letters in their labels. Shift keeps the shortcut unique
                // and still underlines a letter in the actual option rather than adding a numbered prefix.
                letter = candidates[i].FirstOrDefault(candidate => !shifted.Contains(candidate));
                shift = letter != '\0';
                if (shift) shifted.Add(letter);
                else
                {
                    letter = Enumerable.Range('A', 26).Select(value => (char)value)
                        .FirstOrDefault(candidate => !owners.ContainsKey(candidate));
                    if (letter == '\0') throw new InvalidOperationException("Menu has no available letter shortcuts.");
                    owners.Add(letter, i);
                }
            }
            int underline = choices[i].Label.IndexOf(letter.ToString(), StringComparison.OrdinalIgnoreCase);
            result[i] = new(letter, underline, shift);
        }
        return result;
    }
}
