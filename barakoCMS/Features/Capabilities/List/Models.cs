using barakoCMS.Models;

namespace barakoCMS.Features.Capabilities.List;

/// <summary>
/// Defaults to the largest page, like the module list. The whole vocabulary is a few dozen names
/// and a console asking nothing should get all of them.
/// </summary>
internal sealed class ListCapabilitiesRequest : ListRequest;
