namespace Networking
{
    // Direction the wire flows. ClientToServer means clients invoke and
    // the server subscribes; ServerToClient is the inverse. There is no
    // BothWays — pick a direction and pair two events if you genuinely
    // need bidirectional traffic (rare in practice; most game state is
    // owned by one side).
    public enum Scope
    {
        ClientToServer,
        ServerToClient,
    }
}
