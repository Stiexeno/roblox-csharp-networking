using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobloxCSharp.Extensions.Networking;
using RobloxCSharp.Transformer;
using RobloxCSharp.Transformer.AST;
using RobloxCSharp.Transformer.AST.Expressions;

namespace Networking.Tests
{
	public class NetworkingExtensionTests
	{
		private const string EventsDecl = @"
using System;
using Networking;
using RobloxCSharp.RobloxApi;

public static class Events
{
	[NetworkEvent(Scope.ClientToServer)]
	public static Action<Player, string> ChatSubmitted;

	[NetworkEvent(Scope.ServerToClient)]
	public static Action<string> BroadcastBanner;

	[NetworkEvent(Scope.ServerToClient)]
	public static Action<Player, int> ScoreUpdate;
}
";

		private static (TransformerState State, NetworkingExtension Extension, CompilationUnitSyntax UserRoot)
			Setup(string userBody, string extraEventsDecl = null)
		{
			string source = $@"
{EventsDecl}
{extraEventsDecl ?? ""}
public class Test
{{
	{userBody}
}}
";
			(TransformerState state, CompilationUnitSyntax root, var compilation) = TestHarness.Compile(source);
			NetworkingExtension ext = new();
			TestHarness.OnCompile(ext, compilation);
			return (state, ext, root);
		}

		[Fact]
		public void OnCompile_Discovers_AllNetworkEventFields_AndSuppressesContainersAndStubs()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup("void Run() {}");

			List<INamedTypeSymbol> suppressed = ext.SuppressImports(root, state).ToList();

			Assert.Contains(suppressed, t => t.Name == "Events");
			Assert.Contains(suppressed, t => t.Name == "Scope");
			Assert.Contains(suppressed, t => t.Name == "NetworkEventAttribute");
		}

		[Fact]
		public void ClientToServerSubscribe_LowersToRuntimeConnect_WithValidation()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.ChatSubmitted += (player, msg) => System.Console.WriteLine(msg);
				}");

			AssignmentExpressionSyntax assign = TestHarness.FirstNode<AssignmentExpressionSyntax>(root);
			LuaNode result = ext.TryRewrite(assign, state);

			LuaInvocationExpression connect = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression connectMember = Assert.IsType<LuaMemberAccessExpression>(connect.Expression);
			Assert.Equal("Connect", connectMember.MemberName);
			Assert.Equal("_Networking", Assert.IsType<LuaIdentifier>(connectMember.Target).Name);

			// remote, eventName, signalName, key, handler(nil), paramTypes
			Assert.Equal(6, connect.Arguments.Count);
			Assert.Equal("_evt_ChatSubmitted", Assert.IsType<LuaIdentifier>(connect.Arguments[0]).Name);
			Assert.Equal("ChatSubmitted", Assert.IsType<LuaLiteralExpression>(connect.Arguments[1]).Value);
			Assert.Equal("OnServerEvent", Assert.IsType<LuaLiteralExpression>(connect.Arguments[2]).Value);
			Assert.Null(Assert.IsType<LuaLiteralExpression>(connect.Arguments[4]).Value);

			LuaTableExpression types = Assert.IsType<LuaTableExpression>(connect.Arguments[5]);
			Assert.Equal("string", Assert.IsType<LuaLiteralExpression>(Assert.Single(types.Elements)).Value);
		}

		[Fact]
		public void ServerToClientSubscribe_LowersToRuntimeConnect_OnClientEvent()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.BroadcastBanner += msg => System.Console.WriteLine(msg);
				}");

			AssignmentExpressionSyntax assign = TestHarness.FirstNode<AssignmentExpressionSyntax>(root);
			LuaNode result = ext.TryRewrite(assign, state);

			LuaInvocationExpression connect = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression connectMember = Assert.IsType<LuaMemberAccessExpression>(connect.Expression);
			Assert.Equal("Connect", connectMember.MemberName);

			// remote, eventName, signalName, key — no wrapper, no validation.
			Assert.Equal(4, connect.Arguments.Count);
			Assert.Equal("OnClientEvent", Assert.IsType<LuaLiteralExpression>(connect.Arguments[2]).Value);
		}

		[Fact]
		public void SubtractAssignment_LowersToRuntimeDisconnect()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				static void Handler(string msg) { }
				void Run() {
					Events.BroadcastBanner -= Handler;
				}");

			AssignmentExpressionSyntax assign = TestHarness.FirstNode<AssignmentExpressionSyntax>(root);
			LuaNode result = ext.TryRewrite(assign, state);

			LuaInvocationExpression disconnect = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression member = Assert.IsType<LuaMemberAccessExpression>(disconnect.Expression);
			Assert.Equal("Disconnect", member.MemberName);
			Assert.Equal("_Networking", Assert.IsType<LuaIdentifier>(member.Target).Name);
			Assert.Equal(2, disconnect.Arguments.Count);
			Assert.Equal("_evt_BroadcastBanner", Assert.IsType<LuaIdentifier>(disconnect.Arguments[0]).Name);
		}

		[Fact]
		public void ClientToServerFire_LowersToFireServer_DroppingNothing()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.ChatSubmitted?.Invoke(null, ""hi"");
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireServer", method.MemberName);
			Assert.True(method.IsMethodCall);

			Assert.Single(call.Arguments);
		}

		[Fact]
		public void ServerToClientFire_PlayerTarget_RoutesThroughRuntimeFireClient()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run(RobloxCSharp.RobloxApi.Player p) {
					Events.ScoreUpdate?.Invoke(p, 42);
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			// Runtime branches on the target at runtime: nil → FireAllClients,
			// player → FireClient. A nil VARIABLE must broadcast, not error.
			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireClient", method.MemberName);
			Assert.Equal("_Networking", Assert.IsType<LuaIdentifier>(method.Target).Name);
			Assert.Equal(3, call.Arguments.Count);
			Assert.Equal("_evt_ScoreUpdate", Assert.IsType<LuaIdentifier>(call.Arguments[0]).Name);
		}

		[Fact]
		public void ServerToClientFire_NullLiteralTarget_AlsoRoutesThroughRuntimeFireClient()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.ScoreUpdate?.Invoke(null, 100);
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireClient", method.MemberName);
			Assert.Equal(3, call.Arguments.Count);
		}

		[Fact]
		public void ServerToClientFire_NoPlayerInSignature_AlwaysFireAllClients()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.BroadcastBanner?.Invoke(""hello"");
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireAllClients", method.MemberName);
			Assert.Single(call.Arguments);
		}

		[Fact]
		public void NonConditionalInvoke_AlsoLowers()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.BroadcastBanner.Invoke(""hello"");
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireAllClients", method.MemberName);
		}

		[Fact]
		public void NonNetworkEventAssignment_PassesThrough()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				private int _count;
				void Run() { _count += 1; }");

			AssignmentExpressionSyntax assign = TestHarness.FirstNode<AssignmentExpressionSyntax>(root);
			LuaNode result = ext.TryRewrite(assign, state);

			Assert.Null(result);
		}

		[Fact]
		public void NonNetworkEventInvoke_PassesThrough()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				private System.Action _cb;
				void Run() { _cb?.Invoke(); }");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			Assert.Null(result);
		}

		[Fact]
		public void IsNetworkEventOnlyFile_AllFieldsAreNetworkEvents_ReturnsEmptyUnit()
		{
			string source = $@"
{EventsDecl}
";
			(TransformerState state, CompilationUnitSyntax root, var compilation) = TestHarness.Compile(source);
			NetworkingExtension ext = new();
			TestHarness.OnCompile(ext, compilation);

			LuaNode result = ext.TryRewrite(root, state);

			LuaCompilationUnit unit = Assert.IsType<LuaCompilationUnit>(result);
			Assert.True(unit.SkipEmit);
		}

		[Fact]
		public void IsNetworkEventOnlyFile_MixedWithRegularFields_DoesNotShortCircuit()
		{
			string source = $@"
using System;
using Networking;
using RobloxCSharp.RobloxApi;

public static class Mixed
{{
	[NetworkEvent(Scope.ClientToServer)]
	public static Action<string> Evt;

	public static int RegularField;
}}
";
			(TransformerState state, CompilationUnitSyntax root, var compilation) = TestHarness.Compile(source);
			NetworkingExtension ext = new();
			TestHarness.OnCompile(ext, compilation);

			LuaNode result = ext.TryRewrite(root, state);

			Assert.Null(result);
		}

		[Fact]
		public void MixedContainer_ReportsRC0020()
		{
			string source = @"
using System;
using Networking;

public static class Mixed
{
	[NetworkEvent(Scope.ClientToServer)]
	public static Action<string> Evt;

	public static int RegularField;
}
";
			(_, _, var compilation) = TestHarness.Compile(source);
			NetworkingExtension ext = new();
			DiagnosticBag diagnostics = new();
			TestHarness.OnCompile(ext, compilation, diagnostics);

			Assert.Contains(diagnostics.Items, d =>
				d.Code == "RC0020" && d.Message.Contains("RegularField"));
		}

		[Fact]
		public void PureContainer_ReportsNothing()
		{
			(_, _, var compilation) = TestHarness.Compile(EventsDecl);
			NetworkingExtension ext = new();
			DiagnosticBag diagnostics = new();
			TestHarness.OnCompile(ext, compilation, diagnostics);

			Assert.Empty(diagnostics.Items);
		}

		[Fact]
		public void EmitArtifacts_WritesBootstrapFile()
		{
			(_, NetworkingExtension ext, _) = Setup("void Run() {}");
			string tempDir = Path.Combine(Path.GetTempPath(), $"rbxcsnetwork-test-{Guid.NewGuid():N}");
			try
			{
				ext.EmitArtifacts(tempDir, Array.Empty<Plugin>(), null, new DiagnosticBag());

				string path = Path.Combine(tempDir, "server", "_NetworkEventsBootstrap.server.luau");
				Assert.True(File.Exists(path));
				string content = File.ReadAllText(path);
				Assert.Contains("Networking.RegisterRemote(\"ChatSubmitted\")", content);
				Assert.Contains("Networking.RegisterRemote(\"BroadcastBanner\")", content);
				Assert.Contains("Networking.RegisterRemote(\"ScoreUpdate\")", content);
			}
			finally
			{
				if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
			}
		}

		[Fact]
		public void EmitArtifacts_NoEvents_WritesNothing()
		{
			(TransformerState state, CompilationUnitSyntax root, var compilation) = TestHarness.Compile("public class Empty {}");
			NetworkingExtension ext = new();
			TestHarness.OnCompile(ext, compilation);

			string tempDir = Path.Combine(Path.GetTempPath(), $"rbxcsnetwork-empty-{Guid.NewGuid():N}");
			ext.EmitArtifacts(tempDir, Array.Empty<Plugin>(), null, new DiagnosticBag());

			Assert.False(Directory.Exists(tempDir));
		}

		[Fact]
		public void Name_IsNetworking()
		{
			Assert.Equal("Networking", new NetworkingExtension().Name);
		}
	}
}
