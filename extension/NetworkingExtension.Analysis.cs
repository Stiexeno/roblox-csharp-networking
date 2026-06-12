using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobloxCSharp.Common.Diagnostics;
using RobloxCSharp.Rojo;
using RobloxCSharp.Transformer;
using RobloxCSharp.Transformer.Symbols;

namespace RobloxCSharp.Extensions.Networking
{
	// Compile-time analysis: attribute/scope reading, delegate parameter
	// type descriptors, mixed-container and wrong-side diagnostics, and
	// Rojo-aware placement of the bootstrap artifact.
	public sealed partial class NetworkingExtension
	{
		private static bool TryReadScope(IFieldSymbol symbol, out string scope)
		{
			foreach (AttributeData attr in symbol.GetAttributes())
			{
				if (attr.AttributeClass is not { Name: AttributeName } cls) continue;
				if (cls.ContainingNamespace is not { Name: AttributeNamespace } ns
					|| ns.ContainingNamespace?.IsGlobalNamespace != true) continue;
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
				&& IsPlayerType(named.TypeArguments[0]);
		}

		private static bool IsPlayerType(ITypeSymbol type)
			=> type.Name == PlayerTypeName && IsRobloxInstance(type);

		private static bool IsRobloxInstance(ITypeSymbol type)
		{
			for (ITypeSymbol t = type; t is not null; t = t.BaseType)
			{
				if (t.Name == InstanceTypeName) return true;
			}
			return false;
		}

		// Descriptor per wire argument for the runtime's server-side
		// validation. Primitives map to their typeof() names, Player and
		// Instance get an IsA / typeof check, everything else passes
		// through unvalidated ("any").
		private static IReadOnlyList<string> BuildWireParamTypes(ITypeSymbol fieldType, bool firstParamIsPlayer)
		{
			if (fieldType is not INamedTypeSymbol named || named.TypeArguments.Length == 0)
			{
				return Array.Empty<string>();
			}

			List<string> types = new();
			for (int i = firstParamIsPlayer ? 1 : 0; i < named.TypeArguments.Length; i++)
			{
				types.Add(DescribeParamType(named.TypeArguments[i]));
			}
			return types;
		}

		private static string DescribeParamType(ITypeSymbol type)
		{
			switch (type.SpecialType)
			{
				case SpecialType.System_String:
					return "string";
				case SpecialType.System_Boolean:
					return "boolean";
				case SpecialType.System_Byte:
				case SpecialType.System_SByte:
				case SpecialType.System_Int16:
				case SpecialType.System_UInt16:
				case SpecialType.System_Int32:
				case SpecialType.System_UInt32:
				case SpecialType.System_Int64:
				case SpecialType.System_UInt64:
				case SpecialType.System_Single:
				case SpecialType.System_Double:
				case SpecialType.System_Decimal:
					return "number";
			}
			if (IsPlayerType(type)) return PlayerTypeName;
			if (IsRobloxInstance(type)) return InstanceTypeName;
			return AnyParamType;
		}

		private void CollectStubTypes(Compilation compilation)
		{
			foreach (string metadataName in new[]
			{
				AttributeNamespace + ".Scope",
				AttributeNamespace + "." + AttributeName,
			})
			{
				if (compilation.GetTypeByMetadataName(metadataName) is INamedTypeSymbol stub)
				{
					_stubTypes.Add(stub);
				}
			}
		}

		// Event containers are compiled away (SkipEmit + import suppression),
		// so any other member declared alongside the events would be
		// unreachable at runtime. Make that a hard compile error instead of
		// a silent nil index.
		private void ReportMixedContainers(DiagnosticBag diagnostics)
		{
			foreach (INamedTypeSymbol container in _eventContainers)
			{
				foreach (ISymbol member in container.GetMembers())
				{
					if (member.IsImplicitlyDeclared) continue;
					if (member is IFieldSymbol field && _events.ContainsKey(field)) continue;

					SyntaxNode node = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
						?? container.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
					if (node is null) continue;

					diagnostics.ReportUnsupported(node,
						$"member '{member.Name}' in [NetworkEvent] container '{container.Name}'",
						"Classes declaring [NetworkEvent] fields are compiled away; move other members to a separate class.");
				}
			}
		}

		// Compile-time wrong-side detection. ClientToServer is invoked on
		// the client and handled on the server; ServerToClient the inverse.
		// Shared / unresolvable units (no PathTranslator, e.g. unit tests)
		// are left alone.
		private static void ReportWrongSide(EventInfo info, SyntaxNode node, bool isFire, TransformerState state)
		{
			RoutingContext? context = ResolveRoutingContext(state);
			if (context is not (RoutingContext.Server or RoutingContext.Client)) return;

			bool clientToServer = info.Scope == ClientToServerScope;
			RoutingContext expected = isFire == clientToServer ? RoutingContext.Client : RoutingContext.Server;
			if (context == expected) return;

			string action = isFire ? "invoking" : "subscribing to";
			state.Diagnostics.ReportUnsupported(node,
				$"{action} {info.Scope} event '{info.Name}' from {Lower(context.Value)} code",
				$"{info.Scope} events are {(isFire ? "invoked" : "handled")} on the {Lower(expected)} only.");
		}

		private static string Lower(RoutingContext context) => context.ToString().ToLowerInvariant();

		// The PathTranslator routes through the CLI's ContextResolver
		// (filename suffix / [Server]/[Client] attribute / src layout), so
		// the first output segment under outDir is the unit's context.
		private static RoutingContext? ResolveRoutingContext(TransformerState state)
		{
			PathTranslator translator = state.PathTranslator;
			string filePath = state.SemanticModel?.SyntaxTree?.FilePath;
			if (translator is null || string.IsNullOrEmpty(filePath)) return null;

			string relative = Path.GetRelativePath(translator.OutDir, translator.GetOutputPath(filePath))
				.Replace('\\', '/');
			if (relative.StartsWith("server/", StringComparison.OrdinalIgnoreCase)) return RoutingContext.Server;
			if (relative.StartsWith("client/", StringComparison.OrdinalIgnoreCase)) return RoutingContext.Client;
			return RoutingContext.Shared;
		}

		// The bootstrap is a .server.luau Script, so it must land in an
		// output partition the Rojo config mounts under a server container
		// (ServerScriptService preferred). Falls back to outDir/server for
		// non-game projects or a null resolver.
		private static string ResolveBootstrapDir(string outDir, RojoResolver resolver)
		{
			string fallback = null;
			if (resolver is not null)
			{
				foreach (PartitionInfo partition in resolver.Partitions)
				{
					if (resolver.GetNetworkType(partition.RbxPath) != NetworkType.Server) continue;
					if (!IsDescendantOf(partition.FsPath, outDir)) continue;
					if (partition.RbxPath.Length > 0 && partition.RbxPath[0] == "ServerScriptService")
					{
						return partition.FsPath;
					}
					fallback ??= partition.FsPath;
				}
			}
			return fallback ?? Path.Combine(outDir, "server");
		}

		private static bool IsDescendantOf(string path, string dir)
		{
			string relative = Path.GetRelativePath(Path.GetFullPath(dir), Path.GetFullPath(path));
			return !relative.StartsWith("..", StringComparison.Ordinal)
				&& !Path.IsPathRooted(relative);
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
