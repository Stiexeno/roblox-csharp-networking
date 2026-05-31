-- Networking plugin runtime.
--
-- Provides the resolver pair the compiler emits against:
--   RegisterRemote(name) — server-side, called from the auto-emitted
--     `_NetworkEventsBootstrap.server.luau` to materialize one
--     RemoteEvent per [NetworkEvent] discovered in the project.
--   GetRemote(name) — either side, returns the RemoteEvent the
--     transformer-emitted `local _evt_X = ...` preludes cache into.
--
-- Storage convention: ReplicatedStorage.Remotes.Events.<Name>. Folder
-- is created lazily on first use so the order of plugin/init.luau
-- requires vs the bootstrap script doesn't matter.

local ReplicatedStorage = game:GetService("ReplicatedStorage")
local RunService = game:GetService("RunService")

local Networking = {}

-- Idempotent folder creation. Both Remotes/ and Remotes/Events/ get
-- created on demand so a fresh project (or a server that boots before
-- the bootstrap runs, somehow) still resolves cleanly.
local function ensureFolder()
	local remotes = ReplicatedStorage:FindFirstChild("Remotes")
	if not remotes then
		remotes = Instance.new("Folder")
		remotes.Name = "Remotes"
		remotes.Parent = ReplicatedStorage
	end
	local events = remotes:FindFirstChild("Events")
	if not events then
		events = Instance.new("Folder")
		events.Name = "Events"
		events.Parent = remotes
	end
	return events
end

-- Server-only. The auto-emitted bootstrap calls this once per discovered
-- [NetworkEvent]. Idempotent so a double-boot (Studio Play Solo restart,
-- replicated server warm restart) doesn't error.
function Networking.RegisterRemote(name)
	assert(RunService:IsServer(), "[Networking] RegisterRemote may only run on the server")
	local folder = ensureFolder()
	local existing = folder:FindFirstChild(name)
	if existing then return existing end
	local remote = Instance.new("RemoteEvent")
	remote.Name = name
	remote.Parent = folder
	return remote
end

-- Server-side: delegate to RegisterRemote so a module-level prelude
-- that resolves a remote before _NetworkEventsBootstrap.server.luau
-- has run just creates the remote itself rather than erroring. The
-- bootstrap is still useful for warming every remote up at server
-- boot so clients aren't stuck on WaitForChild during the first
-- module's lazy creation, but it's no longer a hard ordering
-- dependency.
--
-- Client-side: WaitForChild. The server creates remotes either via
-- bootstrap (eagerly) or on first GetRemote call (lazily); either
-- way the wait here resolves once the server's first touch has
-- replicated.
--
-- Edit-mode short-circuit: when the place isn't actually running
-- (Studio plugins requiring user modules for inspection — the
-- Component editor's discovery sweep is the canonical case),
-- return nil so the module load succeeds without creating phantom
-- RemoteEvents in the place file. Real method calls only happen
-- at play time when RunService:IsRunning() flips true.
function Networking.GetRemote(name)
	if not RunService:IsRunning() then
		return nil
	end
	if RunService:IsServer() then
		return Networking.RegisterRemote(name)
	end
	return ensureFolder():WaitForChild(name)
end

return Networking
