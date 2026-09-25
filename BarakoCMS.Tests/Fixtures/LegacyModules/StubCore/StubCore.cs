// Compile-time only, never shipped: a barakoCMS with one type the real one has never had, so the
// Broken fixture can name it. At run time barakoCMS.Models.NeverShipped resolves nowhere.
namespace barakoCMS.Modules
{
    public interface IBarakoModule
    {
        string Name { get; }
    }
}

namespace barakoCMS.Models
{
    public class NeverShipped
    {
    }
}
