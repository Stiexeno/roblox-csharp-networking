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
		public void OnCompile_Discovers_AllNetworkEventFields_AndSuppressesContainers()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup("void Run() {}");

			IEnumerable<INamedTypeSymbol> suppressed = ext.SuppressImports(root, state);

			Assert.Contains(suppressed, t => t.Name == "Events");
		}

		[Fact]
		public void ClientToServerSubscribe_LowersToOnServerEventConnect()
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
			Assert.True(connectMember.IsMethodCall);

			LuaMemberAccessExpression signalAccess = Assert.IsType<LuaMemberAccessExpression>(connectMember.Target);
			Assert.Equal("OnServerEvent", signalAccess.MemberName);

			LuaIdentifier remoteLocal = Assert.IsType<LuaIdentifier>(signalAccess.Target);
			Assert.Equal("_evt_ChatSubmitted", remoteLocal.Name);
		}

		[Fact]
		public void ServerToClientSubscribe_LowersToOnClientEventConnect()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.BroadcastBanner += msg => System.Console.WriteLine(msg);
				}");

			AssignmentExpressionSyntax assign = TestHarness.FirstNode<AssignmentExpressionSyntax>(root);
			LuaNode result = ext.TryRewrite(assign, state);

			LuaInvocationExpression connect = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression connectMember = Assert.IsType<LuaMemberAccessExpression>(connect.Expression);
			LuaMemberAccessExpression signalAccess = Assert.IsType<LuaMemberAccessExpression>(connectMember.Target);
			Assert.Equal("OnClientEvent", signalAccess.MemberName);
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
		public void ServerToClientFire_NullTarget_LowersToFireAllClients()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run() {
					Events.ScoreUpdate?.Invoke(null, 100);
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireAllClients", method.MemberName);
		}

		[Fact]
		public void ServerToClientFire_SpecificPlayer_LowersToFireClient()
		{
			(TransformerState state, NetworkingExtension ext, CompilationUnitSyntax root) = Setup(@"
				void Run(RobloxCSharp.RobloxApi.Player p) {
					Events.ScoreUpdate?.Invoke(p, 42);
				}");

			ExpressionStatementSyntax stmt = TestHarness.FirstNode<ExpressionStatementSyntax>(root);
			LuaNode result = ext.TryRewrite(stmt, state);

			LuaInvocationExpression call = Assert.IsType<LuaInvocationExpression>(result);
			LuaMemberAccessExpression method = Assert.IsType<LuaMemberAccessExpression>(call.Expression);
			Assert.Equal("FireClient", method.MemberName);
			Assert.Equal(2, call.Arguments.Count);
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
