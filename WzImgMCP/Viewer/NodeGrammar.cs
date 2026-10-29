using System.Globalization;

namespace WzImgMCP.Viewer;

/// <summary>One step of a node path: the child called <see cref="Name"/>, the <see cref="Index"/>-th (0-based, file order) of that name.</summary>
public readonly record struct NodeSegment(string Name, int Index);

/// <summary>
/// The grammar of the <c>node</c> parameter of <c>/v1/canvas</c> and <c>/v1/frames</c>, and the rule that decides which paths of a diff can be
/// sent as a node unchanged.
///
/// A node is at most 200 characters of ASCII letters, digits, space, '_', '-', '.', with '/' between segments. A segment is a name,
/// optionally followed by '#k' (k decimal without a leading zero, at most 6 digits): <c>name#k</c> is the k-th child with exactly that name
/// in file order, a bare <c>name</c> is the first (the same as <c>name#0</c>). No empty segment, no "." segment, no ".." anywhere. A real
/// node whose own name contains '#' cannot be addressed.
/// </summary>
public static class NodeGrammar
{
    public const int MaxNodeChars = 200;
    public const int MaxIndexDigits = 6;

    /// <summary>Checks the grammar and returns the segments. Throws <c>bad_path</c> (400).</summary>
    public static NodeSegment[] Parse(string? node)
    {
        if (string.IsNullOrEmpty(node))
            throw Bad("node is empty");
        if (node.Length > MaxNodeChars)
            throw Bad($"node is longer than {MaxNodeChars} characters");
        foreach (var c in node)
            if (!(IsNameChar(c) || c is '/' or '#'))
                throw Bad("node contains a character that is not allowed");
        if (node.Contains("..", StringComparison.Ordinal))
            throw Bad("node must not contain '..'");

        var parts = node.Split('/');
        var segments = new NodeSegment[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part == ".")
                throw Bad("node has an empty segment or starts or ends with '/'");
            int hash = part.IndexOf('#');
            if (hash < 0)
            {
                segments[i] = new NodeSegment(part, 0);
                continue;
            }
            if (hash == 0)
                throw Bad("a node segment needs a name before '#'");
            var digits = part.Substring(hash + 1);
            if (digits.Length is 0 or > MaxIndexDigits || digits.Any(c => c is < '0' or > '9') || (digits.Length > 1 && digits[0] == '0'))
                throw Bad("'#' must be followed by a decimal index without a leading zero (at most 6 digits)");
            segments[i] = new NodeSegment(part.Substring(0, hash), int.Parse(digits, CultureInfo.InvariantCulture));
        }
        return segments;
    }

    /// <summary>True when <paramref name="name"/> can be one segment of a node as it is: the allowed characters, not empty, not ".", no "..".</summary>
    public static bool IsAddressableName(string name)
        => name.Length > 0 && name != "." && !name.Contains("..", StringComparison.Ordinal) && name.All(IsNameChar);

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.';

    private static ViewerApiException Bad(string message) => new(400, "bad_path", message);
}
