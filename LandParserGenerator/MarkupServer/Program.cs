using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Land.Core.Specification;
using Land.Core;
using StreamJsonRpc;

namespace LandServer
{
	// Консольное приложение .NET Framework 4.6.1
	// ВАЖНО: ничего не писать в Console.WriteLine — это stdout/протокол!
	internal static class Program
	{
		private static async Task Main(string[] args)
		{
			var messages = new List<Message>();

			var parser = Builder.BuildParser(
			    GrammarType.LR,
			    File.ReadAllText("graphql.land"),
			    messages
			);

			if (messages.Any(m => m.Type == MessageType.Error))
			{
				var error = messages.First(m => m.Type == MessageType.Error);

				Console.Error.WriteLine($"Cannot generate parser {error.Text}");
				Environment.Exit(1);
			}
			else
			{
				Console.Error.WriteLine("Parser Generated");
			}

			var node = parser.Parse("""
				type DateInternal @shareable {
				    duration: Int! @inaccessible
				    unit: DurationUnit!
				}
				""").Item1;

			Console.Error.WriteLine($"{node.Children[0]} {node.Children[0].Children[0]}");


			using (var input = Console.OpenStandardInput())
			using (var output = Console.OpenStandardOutput())
			{
				var service = new LandService();
				var rpc = JsonRpc.Attach(output, input, service);
				await rpc.Completion; // держим процесс, пока клиент не закроет канал
			}
		}
	}

	// ====== DTO ======
	public class Arg { public string TypeNorm; public string NameNorm; }

	public class Anchor
	{
		public string Id;
		public int StartOffset;
		public int EndOffset;
		public string MethodNameNorm;
		public string ParentNameNorm;
		public string ReturnTypeNorm;
		public List<Arg> Args;
		public int? OrdinalInParent;
		public Dictionary<string, int> NeighborBag;
	}

	public class InitializeParams
	{
		public int protocolVersion { get; set; }
		public string projectPath { get; set; }  
		public string offsetEncoding { get; set; }
		public string graphqlParserPath { get; set; }
	}
	public class InitializeResult
	{
		public string serverVersion { get; set; }
		public int protocolVersion { get; set; }
		public Capabilities capabilities { get; set; }
	}
	public class Capabilities { public bool pushUpdates { get; set; } }

	public class ListAnchorsParams
	{
		public string filePath { get; set; }
	}

	public class ListAnchorsResult { public string filePath; public List<Anchor> anchors; }

	// ====== RPC ======
	public class LandService
	{
		[JsonRpcMethod("land/initialize", UseSingleObjectParameterDeserialization = true)]
		public Task<InitializeResult> InitializeAsync(InitializeParams p)
		{
			// читаем и запоминаем путь проекта
			var _projectPath = (!string.IsNullOrWhiteSpace(p.projectPath) && Directory.Exists(p.projectPath))
			    ? p.projectPath
			    : null;

			Console.Error.WriteLine($"[init] protocolVersion={p.protocolVersion} projectPath={_projectPath ?? "<null>"}");

			var messages = new List<Message>();

			var parser = Builder.BuildParser(
			    GrammarType.LR,
			    File.ReadAllText(p.graphqlParserPath),
			    messages
			);

			if (messages.Any(m => m.Type == MessageType.Error))
			{
				var error = messages.First(m => m.Type == MessageType.Error);

				Console.Error.WriteLine($"Cannot generate parser {error.Text}");
				Environment.Exit(1);
			}
			else
			{
				Console.Error.WriteLine("Parser Generated");
			}

			var node = parser.Parse("""
				type DateInternal @shareable {
				    duration: Int! @inaccessible
				    unit: DurationUnit!
				}
				""").Item1;

			Console.Error.WriteLine($"{node.Children[0]} {node.Children[0].Children[0]}");

			return Task.FromResult(new InitializeResult
			{
				serverVersion = "1.0.0",
				protocolVersion = 1,
				capabilities = new Capabilities { pushUpdates = false }
			});
		}



		[JsonRpcMethod("land/listAnchors", UseSingleObjectParameterDeserialization = true)]
		public Task<ListAnchorsResult> ListAnchorsAsync(ListAnchorsParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.filePath))
				throw new ArgumentException("filePath is required");
			return ListAnchorsCoreAsync(p.filePath);
		}

		private Task<ListAnchorsResult> ListAnchorsCoreAsync(string filePath)
		{
			var anchors = new List<Anchor> {
				new Anchor {
				    Id="a1", StartOffset=0, EndOffset=0,
				    MethodNameNorm="Foo", ParentNameNorm="Bar", ReturnTypeNorm="void",
				    Args = new List<Arg>{ new Arg{ TypeNorm="int", NameNorm="x"} }
				}
			    };
			return Task.FromResult(new ListAnchorsResult { filePath = filePath, anchors = anchors });
		}


		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
