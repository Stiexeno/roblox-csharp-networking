using System;

namespace Networking
{
    /// <summary>
    /// Marks a static delegate field as a networked event. The transpiler
    /// rewrites <c>field += handler</c> and <c>field?.Invoke(...)</c> into
    /// <c>RemoteEvent:OnServerEvent:Connect</c> / <c>:FireServer</c> calls
    /// against an auto-registered <c>RemoteEvent</c> whose name matches the field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NetworkEventAttribute : Attribute
    {
        public NetworkEventAttribute(Scope scope) { Scope = scope; }

        /// <summary>Direction of the wire: client→server or server→client.</summary>
        public Scope Scope { get; }
    }
}
