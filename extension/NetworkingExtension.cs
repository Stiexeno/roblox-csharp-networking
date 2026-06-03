using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobloxCSharp.Common.Diagnostics;
using RobloxCSharp.Plugins;
using RobloxCSharp.Rojo;
using RobloxCSharp.Transformer;
using RobloxCSharp.Transformer.AST;
using RobloxCSharp.Transformer.AST.Expressions;
using RobloxCSharp.Transformer.AST.Statements;
using RobloxCSharp.Transformer.Extensibility;
using RobloxCSharp.Transformer.Factory;

namespace RobloxCSharp.Extensions.Networking
{
	/// <summary>
	/// Transpiler hook backing <see cref="Networking.NetworkEventAttribute"/>.
	/// Discovers every <c>[NetworkEvent]</c>-tagged field at compile time,
	/// rewrites <c>field += handler</c> and <c>field?.Invoke(...)</c> into
	/// RemoteEvent <c>Connect</c> / <c>FireServer</c> / <c>FireClient</c> /
	/// <c>FireAllClients</c> calls, injects per-module preludes that resolve
	/// the remote handle, and emits a server bootstrap script that registers
	/// every discovered remote before any user code runs.
	/// </summary>
	public sealed class NetworkingExtension : IRobloxCSharpExtension
	{
		private const string AttributeName = "NetworkEventAttribute";
		private const string AttributeNamespace = "Networking";
		private const string PlayerTypeName = "Player";
		private const string NetworkingRequireLocal = "_Networking";
		private const string PreludeLocalPrefix = "_evt_";
		private const string GetRemoteMethod = "GetRemote";
		private const string RegisterRemoteMethod = "RegisterRemote";

		private sealed record EventInfo(string Name, string Scope, bool FirstParamIsPlayer);

		private readonly Dictionary<IFieldSymbol, EventInfo> _events =
			new(SymbolEqualityComparer.Default);

		private readonly HashSet<INamedTypeSymbol> _eventContainers =
			new(SymbolEqualityComparer.Default);

		public string Name => "Networking";

		public void OnCompile(Compilation compilation, IReadOnlyList<Plugin> plugins, DiagnosticBag diagnostics)
		{
			_events.Clear();
			_eventContainers.Clear();
			foreach (SyntaxTree tree in compilation.SyntaxTrees)
			{
				SemanticModel sm = compilation.GetSemanticModel(tree);
				foreach (FieldDeclarationSyntax field in tree.GetRoot()
					.DescendantNodes()
					.OfType<FieldDeclarationSyntax>())
				{
					foreach (VariableDeclaratorSyntax decl in field.Declaration.Variables)
					{
						if (sm.GetDeclaredSymbol(decl) is not IFieldSymbol symbol) continue;
						if (!TryReadScope(symbol, out string scope)) continue;
						_events[symbol] = new EventInfo(symbol.Name, scope, FirstTypeArgIsPlayer(symbol.Type));
						if (symbol.ContainingType is not null)
						{
							_eventContainers.Add(symbol.ContainingType);
						}
					}
				}
			}
		}

		public LuaNode TryRewrite(SyntaxNode syntax, TransformerState state)
		{

			if (syntax is CompilationUnitSyntax cuSyntax
				&& IsNetworkEventOnlyFile(cuSyntax, state))
			{
				LuaCompilationUnit empty = LuaFactory.CompilationUnit();
				empty.SkipEmit = true;
				return empty;
			}

			if (syntax is AssignmentExpressionSyntax assign
				&& assign.IsKind(SyntaxKind.AddAssignmentExpression))
			{
				if (state.SemanticModel.GetSymbolInfo(assign.Left).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					return RewriteSubscribe(info, assign.Right, state);
				}
			}

			if (syntax is ExpressionStatementSyntax exprStatement)
			{
				if (TryRewriteFireExpression(exprStatement.Expression, state) is LuaNode fired)
				{
					return fired;
				}
			}

			return null;
		}

		private LuaNode TryRewriteFireExpression(ExpressionSyntax expr, TransformerState state)
		{

			if (expr is ConditionalAccessExpressionSyntax cond
				&& cond.WhenNotNull is InvocationExpressionSyntax condInvoke
				&& condInvoke.Expression is MemberBindingExpressionSyntax binding
				&& binding.Name.Identifier.ValueText == "Invoke")
			{
				if (state.SemanticModel.GetSymbolInfo(cond.Expression).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					return RewriteFire(info, condInvoke.ArgumentList, state);
				}
			}

			if (expr is InvocationExpressionSyntax invoke
				&& invoke.Expression is MemberAccessExpressionSyntax memberAccess
				&& memberAccess.Name.Identifier.ValueText == "Invoke")
			{
				if (state.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					return RewriteFire(info, invoke.ArgumentList, state);
				}
			}

			return null;
		}

		private LuaNode RewriteSubscribe(EventInfo info, ExpressionSyntax handler, TransformerState state)
		{
			string signalProperty = info.Scope == "ClientToServer" ? "OnServerEvent" : "OnClientEvent";
			string remoteLocal = PreludeLocalPrefix + info.Name;

			bool injectLocalPlayer = info.Scope == "ServerToClient" && info.FirstParamIsPlayer;
			LuaExpression handlerExpr = injectLocalPlayer
				? BuildLocalPlayerForwardingHandler(state, handler)
				: BindInstanceMethodIfNeeded(state, handler);

			LuaInvocationExpression connect = LuaFactory.Invocation(
				LuaFactory.MemberAccess(
					LuaFactory.MemberAccess(remoteLocal, signalProperty),
					"Connect",
					isMethodCall: true));
			connect.Arguments.Add(handlerExpr);
			return connect;
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
				call = LuaFactory.Invocation(handler);
			}

			call.Arguments.Add(localPlayer);
			call.Arguments.Add(LuaFactory.Identifier("..."));

			return LuaFactory.Function(
				statements: new[] { (LuaNode)LuaFactory.Return(call) },
				parameters: new[] { (LuaNode)LuaFactory.Identifier("...") });
		}

		private static LuaExpression BindInstanceMethodIfNeeded(TransformerState state, ExpressionSyntax handlerSyntax)
		{
			LuaExpression handler = state.Transform(handlerSyntax) as LuaExpression;
			if (handler is null) return null;

			if (state.SemanticModel.GetSymbolInfo(handlerSyntax).Symbol is not IMethodSymbol method) return handler;
			if (method.IsStatic) return handler;
			if (handlerSyntax is not IdentifierNameSyntax) return handler;

			LuaInvocationExpression call = LuaFactory.Invocation(
				LuaFactory.MemberAccess(LuaFactory.Identifier(Syntax.Self), method.Name, isMethodCall: true));
			call.Arguments.Add(LuaFactory.Identifier("..."));

			return LuaFactory.Function(
				statements: new[] { (LuaNode)LuaFactory.Return(call) },
				parameters: new[] { (LuaNode)LuaFactory.Identifier("...") });
		}

		private LuaNode RewriteFire(EventInfo info, ArgumentListSyntax args, TransformerState state)
		{
			string remoteLocal = PreludeLocalPrefix + info.Name;

			if (info.Scope == "ClientToServer")
			{
				int skip = info.FirstParamIsPlayer ? 1 : 0;
				return BuildFireCall(remoteLocal, "FireServer", args, skip, state);
			}

			if (info.FirstParamIsPlayer && args.Arguments.Count > 0)
			{
				ArgumentSyntax targetArg = args.Arguments[0];
				bool targetIsNull = targetArg.Expression is LiteralExpressionSyntax lit
					&& lit.IsKind(SyntaxKind.NullLiteralExpression);

				return targetIsNull
					? BuildFireCall(remoteLocal, "FireAllClients", args, skip: 1, state)
					: BuildFireCall(remoteLocal, "FireClient", args, skip: 0, state);
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

		public IEnumerable<INamedTypeSymbol> ContributeImports(CompilationUnitSyntax syntax, TransformerState state)
		{
			yield break;
		}

		public IEnumerable<INamedTypeSymbol> SuppressImports(CompilationUnitSyntax syntax, TransformerState state)
		{
			return _eventContainers;
		}

		public void OnUnitTransformed(LuaCompilationUnit unit, CompilationUnitSyntax syntax, TransformerState state)
		{
			HashSet<string> referenced = new(StringComparer.Ordinal);
			foreach (MemberAccessExpressionSyntax ma in syntax.DescendantNodes()
				.OfType<MemberAccessExpressionSyntax>())
			{
				if (state.SemanticModel.GetSymbolInfo(ma).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					referenced.Add(info.Name);
				}
			}
			if (referenced.Count == 0) return;

			int insertIndex = 0;
			for (int i = 0; i < unit.Members.Count; i++)
			{
				if (unit.Members[i] is LuaImportDeclaration) insertIndex = i + 1;
			}

			unit.Members.Insert(insertIndex++, new LuaImportDeclaration(
				NetworkingRequireLocal,
				"game:GetService(\"ReplicatedStorage\")",
				new[] { "Plugins", "Networking" },
				useRawRequire: true));

			foreach (string name in referenced.OrderBy(n => n, StringComparer.Ordinal))
			{
				LuaInvocationExpression getRemote = LuaFactory.Invocation(
					LuaFactory.MemberAccess(NetworkingRequireLocal, GetRemoteMethod));
				getRemote.Arguments.Add(LuaFactory.LiteralExpression(name));
				unit.Members.Insert(insertIndex++,
					LuaFactory.LocalDeclaration(PreludeLocalPrefix + name, getRemote));
			}
		}

		public void EmitArtifacts(string outDir, IReadOnlyList<Plugin> plugins, RojoResolver resolver, DiagnosticBag diagnostics)
		{
			if (_events.Count == 0) return;

			string bootstrapDir = Path.Combine(outDir, "server");
			Directory.CreateDirectory(bootstrapDir);
			string bootstrapPath = Path.Combine(bootstrapDir, "_NetworkEventsBootstrap.server.luau");

			StringBuilder sb = new();
			sb.AppendLine("-- Auto-generated by NetworkingExtension. Registers every");
			sb.AppendLine("-- [NetworkEvent] discovered at compile time so the wire");
			sb.AppendLine("-- exists before any user code calls FireServer / Connect.");
			sb.AppendLine();
			sb.AppendLine("local Networking = require(game:GetService(\"ReplicatedStorage\"):WaitForChild(\"Plugins\"):WaitForChild(\"Networking\"))");
			foreach (string name in _events.Values
				.Select(v => v.Name)
				.OrderBy(n => n, StringComparer.Ordinal))
			{
				sb.AppendLine($"Networking.{RegisterRemoteMethod}(\"{name}\")");
			}

			File.WriteAllText(bootstrapPath, sb.ToString());
		}

		private static bool TryReadScope(IFieldSymbol symbol, out string scope)
		{
			foreach (AttributeData attr in symbol.GetAttributes())
			{
				if (attr.AttributeClass is not { Name: AttributeName } cls) continue;
				if (cls.ContainingNamespace?.Name != AttributeNamespace) continue;
				if (attr.ConstructorArguments.Length == 0) continue;

				TypedConstant arg = attr.ConstructorArguments[0];
				if (arg.Type is not INamedTypeSymbol enumType || arg.Value is not int value) continue;

				foreach (ISymbol member in enumType.GetMembers())
				{
					if (member is IFieldSymbol f && f.HasConstantValue
						&& f.ConstantValue is int v && v == value)
					{
						scope = f.Name;
						return true;
					}
				}
			}
			scope = null;
			return false;
		}

		private static bool FirstTypeArgIsPlayer(ITypeSymbol type)
		{
			return type is INamedTypeSymbol named
				&& named.TypeArguments.Length > 0
				&& named.TypeArguments[0].Name == PlayerTypeName;
		}

		private bool IsNetworkEventOnlyFile(CompilationUnitSyntax unit, TransformerState state)
		{
			if (_events.Count == 0) return false;

			bool sawNetworkEvent = false;
			return WalkMembers(unit.Members, state, ref sawNetworkEvent) && sawNetworkEvent;
		}

		private bool WalkMembers(SyntaxList<MemberDeclarationSyntax> members, TransformerState state, ref bool sawNetworkEvent)
		{
			foreach (MemberDeclarationSyntax m in members)
			{
				switch (m)
				{
					case BaseNamespaceDeclarationSyntax ns:
						if (!WalkMembers(ns.Members, state, ref sawNetworkEvent)) return false;
						break;
					case ClassDeclarationSyntax cls:
						foreach (MemberDeclarationSyntax cm in cls.Members)
						{
							if (cm is not FieldDeclarationSyntax field) return false;
							foreach (VariableDeclaratorSyntax decl in field.Declaration.Variables)
							{
								if (state.SemanticModel.GetDeclaredSymbol(decl) is IFieldSymbol s
									&& _events.ContainsKey(s))
								{
									sawNetworkEvent = true;
								}
								else
								{
									return false;
								}
							}
						}
						break;
					default:
						return false;
				}
			}
			return true;
		}
	}
}
