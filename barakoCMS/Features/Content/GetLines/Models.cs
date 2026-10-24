namespace barakoCMS.Features.Content.GetLines;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    /// <summary>The header, shaped as GET /api/contents/{id} shapes it.</summary>
    public barakoCMS.Features.Content.Get.Response Header { get; set; } = new();

    /// <summary>True when the header has more lines than are listed. Counted over the lines the caller may read.</summary>
    public bool Truncated { get; set; }

    /// <summary>The lines the caller may read, oldest first.</summary>
    public List<barakoCMS.Features.Content.Get.Response> Lines { get; set; } = [];
}
