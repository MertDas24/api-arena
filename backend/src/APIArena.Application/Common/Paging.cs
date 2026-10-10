namespace APIArena.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>Bounded paging (OWN-04): page ≥ 1, 1 ≤ pageSize ≤ 50; sort keys come from an allow-list per endpoint.</summary>
public sealed record PageRequest(int Page, int PageSize, string Sort, bool Descending)
{
    public const int MaxPageSize = 50;

    public static PageRequest Create(int? page, int? pageSize, string? sort, string? order, IReadOnlyCollection<string> allowedSorts, string defaultSort)
    {
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
        var s = sort ?? defaultSort;
        if (!allowedSorts.Contains(s))
        {
            throw ValidationFailedException.For("sort", $"İzin verilen sıralama alanları: {string.Join(", ", allowedSorts)}.");
        }

        var desc = order is null ? true : order.ToLowerInvariant() switch
        {
            "asc" => false,
            "desc" => true,
            _ => throw ValidationFailedException.For("order", "order 'asc' veya 'desc' olmalı."),
        };
        return new PageRequest(p, size, s, desc);
    }

    public int Skip => (Page - 1) * PageSize;
}
