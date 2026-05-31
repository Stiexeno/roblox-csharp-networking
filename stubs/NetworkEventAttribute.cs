using System;

namespace Networking
{
    // Marks a static delegate field as a networked event. The transformer
    // rewrites `+=` against a marked symbol into RemoteEvent:Connect on
    // the appropriate signal (OnServerEvent on the server, OnClientEvent
    // on the client) and `?.Invoke(...)` into the matching Fire call
    // (FireServer / FireClient / FireAllClients) based on Scope and
    // whether the first parameter is a Player.
    //
    // Declared as a plain `static Action<...>` field (NOT a C# `event`)
    // so both subscription (+=) and invocation (?.Invoke) work from
    // outside the declaring class. C# `event` semantics would block
    // invocation with CS0070 at every call site outside the declaring
    // type, which is exactly the boundary we want to cross.
    //
    // Trade-off: users could write `Events.X = null` to wipe all
    // handlers. Don't. The transformer rewrites += and ?.Invoke; plain
    // assignment is not supported and produces unintended Luau.
    //
    // Naming convention: the field name doubles as the RemoteEvent name
    // under ReplicatedStorage.Remotes.Events.<Name>. Rename-safe because
    // both sides discover via the same C# symbol.
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NetworkEventAttribute : Attribute
    {
        public NetworkEventAttribute(Scope scope) { Scope = scope; }
        public Scope Scope { get; }
    }
}
