namespace Barako.Fixture.Library;

// A host's own class library: it references barakoCMS and defines no module. A type in it that
// cannot load is the host's build to fix, so the endpoint scan must still see it and fail as it
// always has, not skip the library and quietly drop its endpoints.
public static class LibraryHelper
{
    public static string Describe() => "a library, not a module";
}

public class LeansOnAMissingType : barakoCMS.Models.NeverShipped
{
}
