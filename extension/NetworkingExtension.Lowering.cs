using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobloxCSharp.Transformer;
using RobloxCSharp.Transformer.AST;
using RobloxCSharp.Transformer.AST.Expressions;
using RobloxCSharp.Transformer.Factory;

namespace RobloxCSharp.Extensions.Networking
{
	// Subscribe / unsubscribe / fire lowering. All wire traffic routes
	// through the plugin runtime (`_Networking.Connect/Disconnect/
	// FireClient`) so connection tracking, server-side validation and
	// the nil-player broadcast branch live in one place.
	public sealed partial class NetworkingExtension
	{
		private LuaNode RewriteSubscribe(EventInfo info, AssignmentExpressionSyntax assign, TransformerState state)
		{
			ReportWrongSide(info, assign, isFire: false, state);

			string signalProperty = info.Scope == ClientToServerScope ? "OnServerEvent" : "OnClientEvent";
			bool injectLocalPlayer = info.Scope != ClientToServerScope && info.FirstParamIsPlayer;

			LuaExpression key = BuildHandlerKey(state, assign.Right);
			LuaExpression wrapper = injectLocalPlayer
				? BuildLocalPlayerForwardingHandler(state, assign.Right)
				: BuildInstanceMethodWrapper(state, assign.Right);

			// Only validate what an exploiter controls: ClientToServer wire
			// arguments. An all-"any" descriptor list checks nothing — skip it.
			bool validate = info.Scope == ClientToServerScope
				&& info.WireParamTypes.Any(t => t != AnyParamType);

			LuaInvocationExpression connect = LuaFactory.Invocation(
				LuaFactory.MemberAccess(NetworkingRequireLocal, ConnectMethod));
			connect.Arguments.Add(LuaFactory.Identifier(PreludeLocalPrefix + info.Name));
			connect.Arguments.Add(LuaFactory.LiteralExpression(info.Name));
			connect.Arguments.Add(LuaFactory.LiteralExpression(signalProperty));
			connect.Arguments.Add(key);
			if (wrapper is not null || validate)
			{
				connect.Arguments.Add(wrapper ?? (LuaExpression)LuaFactory.LiteralExpression(null));
			}
			if (validate)
			{
				connect.Arguments.Add(LuaFactory.Table(inline: true,
					info.WireParamTypes.Select(t => (LuaNode)LuaFactory.LiteralExpression(t))));
			}
			return connect;
		}

		private LuaNode RewriteUnsubscribe(EventInfo info, AssignmentExpressionSyntax assign, TransformerState state)
		{
			ReportWrongSide(info, assign, isFire: false, state);

			LuaInvocationExpression disconnect = LuaFactory.Invocation(
				LuaFactory.MemberAccess(NetworkingRequireLocal, DisconnectMethod));
			disconnect.Arguments.Add(LuaFactory.Identifier(PreludeLocalPrefix + info.Name));
			disconnect.Arguments.Add(BuildHandlerKey(state, assign.Right));
			return disconnect;
		}

		// Stable identity for the += / -= round trip. An instance-method
		// group lowers to `self.<Method>` — the __index lookup yields the
		// same function value every time — while everything else (static
		// method group, lambda, delegate-typed expression) keys by its own
		// transformed value.
		private static LuaExpression BuildHandlerKey(TransformerState state, ExpressionSyntax handlerSyntax)
		{
			if (state.SemanticModel.GetSymbolInfo(handlerSyntax).Symbol is IMethodSymbol method
				&& !method.IsStatic
				&& handlerSyntax is IdentifierNameSyntax)
			{
				return LuaFactory.MemberAccess(LuaFactory.Identifier(Syntax.Self), method.Name);
			}
			return state.Transform(handlerSyntax) as LuaExpression;
		}

		private static LuaExpression BuildLocalPlayerForwardingHandler(TransformerState state, ExpressionSyntax handlerSyntax)
		{
			LuaInvocationExpression getPlayers = LuaFactory.Invocation(
				LuaFactory.MemberAccess("game", "GetService", isMethodCall: true));
			getPlayers.Arguments.Add(LuaFactory.LiteralExpression("Players"));
			LuaExpression localPlayer = LuaFactory.MemberAccess(getPlayers, "LocalPlayer");

			LuaInvocationExpression call;
			if (state.SemanticModel.GetSymbolInfo(handlerSyntax).Symbol is IMethodSymbol method
				&& !method.IsStatic
				&& handlerSyntax is IdentifierNameSyntax)
			{

				call = LuaFactory.Invocation(
					LuaFactory.MemberAccess(
						LuaFactory.Identifier(Syntax.Self), method.Name, isMethodCall: true));
			}
			else
			{

				LuaExpression handler = state.Transform(handlerSyntax) as LuaExpression;
				// A function literal is not a Lua prefixexp — calling it
				// inline requires parentheses: `(function() end)(...)`.
				if (handler is LuaFunctionExpression)
				{
					handler = LuaFactory.Parenthesized(handler);
				}
				call = LuaFactory.Invocation(handler);
			}

			call.Arguments.Add(localPlayer);
			call.Arguments.Add(LuaFactory.Identifier("..."));

			return LuaFactory.Function(
				statements: new[] { (LuaNode)LuaFactory.Return(call) },
				parameters: new[] { (LuaNode)LuaFactory.Identifier("...") });
		}

		// `field += OnThing` where OnThing is an instance method needs a
		// `self`-binding wrapper. Returns null when the handler can be
		// connected as-is (static method, lambda, delegate value).
		private static LuaExpression BuildInstanceMethodWrapper(TransformerState state, ExpressionSyntax handlerSyntax)
		{
			if (state.SemanticModel.GetSymbolInfo(handlerSyntax).Symbol is not IMethodSymbol method) return null;
			if (method.IsStatic) return null;
			if (handlerSyntax is not IdentifierNameSyntax) return null;

			LuaInvocationExpression call = LuaFactory.Invocation(
				LuaFactory.MemberAccess(LuaFactory.Identifier(Syntax.Self), method.Name, isMethodCall: true));
			call.Arguments.Add(LuaFactory.Identifier("..."));

			return LuaFactory.Function(
				statements: new[] { (LuaNode)LuaFactory.Return(call) },
				parameters: new[] { (LuaNode)LuaFactory.Identifier("...") });
		}

		private LuaNode RewriteFire(EventInfo info, ExpressionSyntax fireSyntax, ArgumentListSyntax args, TransformerState state)
		{
			ReportWrongSide(info, fireSyntax, isFire: true, state);

			string remoteLocal = PreludeLocalPrefix + info.Name;

			if (info.Scope == ClientToServerScope)
			{
				int skip = info.FirstParamIsPlayer ? 1 : 0;
				return BuildFireCall(remoteLocal, "FireServer", args, skip, state);
			}

			if (info.FirstParamIsPlayer && args.Arguments.Count > 0)
			{
				// The target Player is only known at runtime (a nil variable
				// must broadcast, not error), so route through the runtime's
				// FireClient helper instead of a direct :FireClient.
				LuaInvocationExpression call = LuaFactory.Invocation(
					LuaFactory.MemberAccess(NetworkingRequireLocal, FireClientMethod));
				call.Arguments.Add(LuaFactory.Identifier(remoteLocal));
				foreach (ArgumentSyntax arg in args.Arguments)
				{
					call.Arguments.Add(state.Transform(arg.Expression) as LuaExpression);
				}
				return call;
			}

			return BuildFireCall(remoteLocal, "FireAllClients", args, skip: 0, state);
		}

		private static LuaInvocationExpression BuildFireCall(
			string remoteLocal, string method, ArgumentListSyntax args, int skip, TransformerState state)
		{
			LuaInvocationExpression call = LuaFactory.Invocation(
				LuaFactory.MemberAccess(remoteLocal, method, isMethodCall: true));
			for (int i = skip; i < args.Arguments.Count; i++)
			{
				call.Arguments.Add(state.Transform(args.Arguments[i].Expression) as LuaExpression);
			}
			return call;
		}
	}
}
