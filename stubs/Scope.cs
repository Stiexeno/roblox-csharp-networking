namespace Networking
{
    /// <summary>
    /// Wire direction for a <see cref="NetworkEventAttribute"/>.
    /// </summary>
    public enum Scope
    {
        /// <summary>Client invokes; server handles. Fires <c>FireServer</c>, connects <c>OnServerEvent</c>.</summary>
        ClientToServer,

        /// <summary>Server invokes; client handles. Fires <c>FireClient</c> / <c>FireAllClients</c>, connects <c>OnClientEvent</c>.</summary>
        ServerToClient,
    }
}
