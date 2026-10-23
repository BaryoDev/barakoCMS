namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// A way to start a sign-in that a module adds. Register it with
/// <c>services.AddSingleton&lt;ISignInMethod&gt;(new MyMethod())</c> and a tenant can name it in
/// <c>signIn.methods</c>. It is off until a tenant names it.
/// </summary>
public interface ISignInMethod
{
    /// <summary>The name a tenant writes in <c>signIn.methods</c>. Lower case.</summary>
    string Name { get; }
}
