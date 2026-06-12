-- Networking plugin runtime.
--
-- Provides the helpers the compiler emits against:
--   RegisterRemote(name) — server-side, called from the auto-emitted
--     `_NetworkEventsBootstrap.server.luau` to materialize one
--     RemoteEvent per [NetworkEvent] discovered in the project.
--   GetRemote(name) — either side, returns the RemoteEvent the
--     transformer-emitted `local _evt_X = ...` preludes cache into.
--   Connect / Disconnect — `+=` / `-=` lowering. Tracks the
--     RBXScriptConnection per handler key so unsubscribe works.
--   FireClient(remote, player, ...) — ServerToClient fire with a
--     runtime nil-check: nil player broadcasts via FireAllClients.
--
-- Storage convention: ReplicatedStorage.Remotes.Events.<Name>. The
-- folder chain is created lazily on first use — ON THE SERVER ONLY —
-- so the order of plugin/init.luau requires vs the bootstrap script
-- doesn't matter.

local ReplicatedStorage = game:GetService("ReplicatedStorage")
local RunService = game:GetService("RunService")

local Networking = {}

-- remote → { [handlerKey] = RBXScriptConnection }. Weak keys so a
-- destroyed remote doesn't pin its connection table.
local connections = setmetatable({}, { __mode = "k" })

-- Idempotent folder creation. SERVER ONLY — a client must never
-- Instance.new these folders: the server's own Remotes/Events folder
-- replicates as a *different* instance, so WaitForChild against a
-- client-local clone would hang forever.
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
-- has run just creates the remote itself rather than erroring.
--
-- Client-side: WaitForChild the whole chain. Never create folders
-- here — only the server-created (and replicated) instances count.
--
-- Edit-mode short-circuit: when the place isn't actually running
-- (Studio plugins requiring user modules for inspection), return nil
-- so the module load succeeds without creating phantom RemoteEvents
-- in the place file. Connect / Disconnect / FireClient below tolerate
-- the nil for the same reason.
function Networking.GetRemote(name)
	if not RunService:IsRunning() then
		return nil
	end
	if RunService:IsServer() then
		return Networking.RegisterRemote(name)
	end
	return ReplicatedStorage:WaitForChild("Remotes")
		:WaitForChild("Events")
		:WaitForChild(name)
end

-- Lightweight server-side argument validation. The extension passes a
-- descriptor per wire argument derived from the delegate's parameter
-- types: "number" / "string" / "boolean" / "Instance" / "Player"; "any"
-- passes everything through (complex types — tables — are not deep-
-- checked).
local function typeMatches(value, expected)
	if expected == "any" then
		return true
	end
	local valueType = typeof(value)
	if expected == "Player" then
		return valueType == "Instance" and value:IsA("Player")
	end
	if expected == "Instance" then
		return valueType == "Instance"
	end
	return valueType == expected
end

local function wrapValidated(eventName, paramTypes, handler)
	return function(player, ...)
		for i = 1, #paramTypes do
			local value = select(i, ...)
			if not typeMatches(value, paramTypes[i]) then
				warn(string.format(
					"[Networking] %s: dropped event from %s — argument %d expected %s, got %s",
					eventName, tostring(player), i, paramTypes[i], typeof(value)))
				return
			end
		end
		return handler(player, ...)
	end
end

-- `field += handler` lowering. `key` is the stable identity of the C#
-- handler (the method's function value, or the closure itself for
-- lambdas); `handler` is the actually-connected function when the
-- compiler had to wrap (instance-method bind, LocalPlayer injection) —
-- nil means connect the key directly. `paramTypes` is only passed for
-- ClientToServer subscriptions (server-side validation).
--
-- Re-subscribing the same key replaces the previous connection rather
-- than stacking a duplicate.
function Networking.Connect(remote, eventName, signalName, key, handler, paramTypes)
	if remote == nil then return end
	handler = handler or key
	if paramTypes ~= nil then
		handler = wrapValidated(eventName, paramTypes, handler)
	end
	local byKey = connections[remote]
	if byKey == nil then
		byKey = {}
		connections[remote] = byKey
	end
	local previous = byKey[key]
	if previous ~= nil then
		previous:Disconnect()
	end
	byKey[key] = remote[signalName]:Connect(handler)
end

-- `field -= handler` lowering. No-op when the key was never connected
-- (mirrors C# delegate semantics for removing an absent handler).
function Networking.Disconnect(remote, key)
	if remote == nil then return end
	local byKey = connections[remote]
	local connection = byKey and byKey[key]
	if connection ~= nil then
		connection:Disconnect()
		byKey[key] = nil
	end
end

-- ServerToClient fire whose first delegate parameter is a Player. The
-- target is only known at runtime: nil broadcasts, anything else is a
-- targeted send.
function Networking.FireClient(remote, player, ...)
	if remote == nil then return end
	if player == nil then
		remote:FireAllClients(...)
	else
		remote:FireClient(player, ...)
	end
end

return Networking
