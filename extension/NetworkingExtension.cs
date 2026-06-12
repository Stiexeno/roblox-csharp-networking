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
	/// rewrites <c>field += handler</c> / <c>field -= handler</c> and
	/// <c>field?.Invoke(...)</c> into runtime <c>Connect</c> /
	/// <c>Disconnect</c> / fire calls, injects per-module preludes that
	/// resolve the remote handle, and emits a server bootstrap script that
	/// registers every discovered remote before any user code runs.
	/// </summary>
	public sealed partial class NetworkingExtension : IRobloxCSharpExtension
	{
		private const string AttributeName = "NetworkEventAttribute";
		private const string AttributeNamespace = "Networking";
		private const string PlayerTypeName = "Player";
		private const string InstanceTypeName = "Instance";
		private const string NetworkingRequireLocal = "_Networking";
		private const string PreludeLocalPrefix = "_evt_";
		private const string GetRemoteMethod = "GetRemote";
		private const string RegisterRemoteMethod = "RegisterRemote";
		private const string ConnectMethod = "Connect";
		private const string DisconnectMethod = "Disconnect";
		private const string FireClientMethod = "FireClient";
		private const string ClientToServerScope = "ClientToServer";
		private const string AnyParamType = "any";

		private sealed record EventInfo(
			string Name, string Scope, bool FirstParamIsPlayer, IReadOnlyList<string> WireParamTypes);

		private readonly Dictionary<IFieldSymbol, EventInfo> _events =
			new(SymbolEqualityComparer.Default);

		private readonly HashSet<INamedTypeSymbol> _eventContainers =
			new(SymbolEqualityComparer.Default);

		// The plugin's own stub types (Scope, NetworkEventAttribute). They
		// have no runtime/*.luau counterpart, so any would-be import of
		// them must be suppressed alongside the event containers.
		private readonly HashSet<INamedTypeSymbol> _stubTypes =
			new(SymbolEqualityComparer.Default);

		public string Name => "Networking";

		public void OnCompile(Compilation compilation, IReadOnlyList<Plugin> plugins, DiagnosticBag diagnostics)
		{
			_events.Clear();
			_eventContainers.Clear();
			_stubTypes.Clear();
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
						bool firstParamIsPlayer = FirstTypeArgIsPlayer(symbol.Type);
						_events[symbol] = new EventInfo(
							symbol.Name, scope, firstParamIsPlayer,
							BuildWireParamTypes(symbol.Type, firstParamIsPlayer));
						if (symbol.ContainingType is not null)
						{
							_eventContainers.Add(symbol.ContainingType);
						}
					}
				}
			}

			CollectStubTypes(compilation);
			ReportMixedContainers(diagnostics);
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
				&& (assign.IsKind(SyntaxKind.AddAssignmentExpression)
					|| assign.IsKind(SyntaxKind.SubtractAssignmentExpression)))
			{
				if (state.SemanticModel.GetSymbolInfo(assign.Left).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					return assign.IsKind(SyntaxKind.AddAssignmentExpression)
						? RewriteSubscribe(info, assign, state)
						: RewriteUnsubscribe(info, assign, state);
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
					return RewriteFire(info, cond, condInvoke.ArgumentList, state);
				}
			}

			if (expr is InvocationExpressionSyntax invoke
				&& invoke.Expression is MemberAccessExpressionSyntax memberAccess
				&& memberAccess.Name.Identifier.ValueText == "Invoke")
			{
				if (state.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol is IFieldSymbol field
					&& _events.TryGetValue(field, out EventInfo info))
				{
					return RewriteFire(info, invoke, invoke.ArgumentList, state);
				}
			}

			return null;
		}

		public IEnumerable<INamedTypeSymbol> ContributeImports(CompilationUnitSyntax syntax, TransformerState state)
		{
			yield break;
		}

		public IEnumerable<INamedTypeSymbol> SuppressImports(CompilationUnitSyntax syntax, TransformerState state)
		{
			return _eventContainers.Concat(_stubTypes);
		}

		public void OnUnitTransformed(LuaCompilationUnit unit, CompilationUnitSyntax syntax, TransformerState state)
		{
			// Identifier scan covers both shapes: the `.Name` of a qualified
			// `Events.ChatSubmitted` and a bare `ChatSubmitted` brought in
			// via `using static` both bind to the field symbol.
			HashSet<string> referenced = new(StringComparer.Ordinal);
			foreach (IdentifierNameSyntax id in syntax.DescendantNodes()
				.OfType<IdentifierNameSyntax>())
			{
				if (state.SemanticModel.GetSymbolInfo(id).Symbol is IFieldSymbol field
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

			string bootstrapDir = ResolveBootstrapDir(outDir, resolver);
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
	}
}
