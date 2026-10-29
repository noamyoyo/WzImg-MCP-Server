namespace WzImgMCP.Core;

/// <summary>limit and offset of the tools that return a long list one page at a time.</summary>
public static class Paging
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    /// <summary>Limit from 1 to <see cref="MaxLimit"/> (a limit below 1 is 1), offset from 0.</summary>
    public static (int Limit, int Offset) Normalize(int limit, int offset)
        => (Math.Clamp(limit, 1, MaxLimit), Math.Max(offset, 0));

    /// <summary>The offset of the next page, or null when this page reaches the end of <paramref name="total"/> items.</summary>
    public static int? NextOffset(int offset, int returned, int total)
        => offset + returned < total ? offset + returned : null;
}
