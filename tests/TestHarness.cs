using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobloxCSharp;
using RobloxCSharp.Common.Diagnostics;
using RobloxCSharp.Plugins;
using RobloxCSharp.Transformer;

namespace Networking.Tests
{
	internal static class TestHarness
	{
		public const string Stubs = @"
using System;

namespace Networking
{
	public enum Scope { ClientToServer, ServerToClient }

	[AttributeUsage(AttributeTargets.Field)]
	public sealed class NetworkEventAttribute : Attribute
	{
		public NetworkEventAttribute(Scope scope) { Scope = scope; }
		public Scope Scope { get; }
	}
}

namespace RobloxCSharp.RobloxApi
{
	public class Instance { }
	public class Player : Instance { }
}
";

		public static (TransformerState State, CompilationUnitSyntax Root, CSharpCompilation Compilation) Compile(string userSource)
		{
			SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource);
			SyntaxTree stubsTree = CSharpSyntaxTree.ParseText(Stubs);
			CSharpCompilation compilation = CompilationFactory.Create("Anonymous", userTree, stubsTree);
			CSharpCompilationContext context = new(userTree, compilation);
			TransformerState state = new(context);
			return (state, (CompilationUnitSyntax)userTree.GetRoot(), compilation);
		}

		public static void OnCompile(IRobloxCSharpExtension extension, CSharpCompilation compilation)
		{
			OnCompile(extension, compilation, new DiagnosticBag());
		}

		public static void OnCompile(IRobloxCSharpExtension extension, CSharpCompilation compilation, DiagnosticBag diagnostics)
		{
			extension.OnCompile(compilation, Array.Empty<Plugin>(), diagnostics);
		}

		public static T FirstNode<T>(CompilationUnitSyntax root) where T : SyntaxNode
		{
			foreach (SyntaxNode node in root.DescendantNodes())
			{
				if (node is T t) return t;
			}
			throw new InvalidOperationException($"No {typeof(T).Name} found in source.");
		}
	}
}
