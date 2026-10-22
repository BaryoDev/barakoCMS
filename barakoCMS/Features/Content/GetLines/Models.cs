namespace barakoCMS.Features.Content.GetLines;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    /// <summary>The header, shaped as GET /api/contents/{id} shapes it.</summary>
    public barakoCMS.Features.Content.Get.Response Header { get; set; } = new();

    /// <summary>How many lines the header has, before the caller's read rules. More than are listed when the list is capped.</summary>
    public int Total { get; set; }

    /// <summary>The lines the caller may read, oldest first.</summary>
    public List<barakoCMS.Features.Content.Get.Response> Lines { get; set; } = [];
}
