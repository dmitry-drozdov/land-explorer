using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Land.Core.Specification;
using Land.Core;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Net;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using VPTree;

namespace LandServer
{
	// Консольное приложение .NET Framework 4.6.1
	// ВАЖНО: ничего не писать в Console.WriteLine — это stdout/протокол!
	internal static class Program
	{
		private static async Task<int> Main(string[] args)
		{
			try
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

				if (args.Length >= 2 && args[0] == "--tcp" || true)
				{
					var ep = ParseEndPoint("127.0.0.1:7711"); // args[1]
					Console.Error.WriteLine($"[boot] TCP mode on {ep}");

					var listener = new TcpListener(ep);
					listener.Start();

					using var client = await listener.AcceptTcpClientAsync(); // один клиент на сессию
					using var stream = client.GetStream();
					var svc = new LandService();
					var rpc = JsonRpc.Attach(stream, stream, svc); // HeaderDelimitedMessageHandler по-умолчанию
					Console.Error.WriteLine("[boot] client connected");
					await rpc.Completion;
					Console.Error.WriteLine("[boot] rpc completion");
					return 0;
				}
				else
				{
					Console.Error.WriteLine("[boot] stdio mode");
					using var input = Console.OpenStandardInput();
					using var output = Console.OpenStandardOutput();
					var svc = new LandService();
					var rpc = JsonRpc.Attach(output, input, svc);
					await rpc.Completion;
					Console.Error.WriteLine("[boot] rpc completion");
					return 0;
				}
			}
			catch (Exception ex)
			{
				try { Console.Error.WriteLine("[fatal] " + ex); } catch { }
				return 1;
			}
		}

		private static IPEndPoint ParseEndPoint(string s)
		{
			var parts = s.Split(':');
			var host = parts[0];
			var port = int.Parse(parts[1]);
			var ip = host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
			return new IPEndPoint(ip, port);
		}
	}

	// ====== DTO ======
	public class Arg { public string TypeNorm; public string NameNorm; }

	public class Anchor
	{
		public string Id;
		public string Filepath;
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
		public string folderPath { get; set; }
	}

	public class ListAnchorsResult { public string folderPath; public List<Anchor> anchors; }

	// ====== RPC ======
	public class LandService
	{
		private BaseParser parser;
		[JsonRpcMethod("land/initialize", UseSingleObjectParameterDeserialization = true)]
		public Task<InitializeResult> InitializeAsync(InitializeParams p)
		{
			try
			{
				Console.Error.WriteLine($"[init] protocolVersion={p.protocolVersion} p.graphqlParserPath={p.graphqlParserPath}");

				var messages = new List<Message>();

				parser = Builder.BuildParser(
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

				return Task.FromResult(new InitializeResult
				{
					serverVersion = "1.0.0",
					protocolVersion = 1,
					capabilities = new Capabilities { pushUpdates = false }
				});
			}
			catch (Exception ex)
			{
				try { Console.Error.WriteLine("[fatal] " + ex); } catch { }
				return Task.FromResult(new InitializeResult
				{
					serverVersion = "1.0.0",
					protocolVersion = 1,
					capabilities = new Capabilities { pushUpdates = false }
				});
			}
		}



		[JsonRpcMethod("land/listAnchors", UseSingleObjectParameterDeserialization = true)]
		public Task<ListAnchorsResult> ListAnchorsAsync(ListAnchorsParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.folderPath))
				throw new ArgumentException("filePath is required");
			return ListAnchorsCoreAsync(p.folderPath, "graphql");
		}

		private Task<ListAnchorsResult> ListAnchorsCoreAsync(string folderPath, string extension)
		{
			var anchors = new List<Anchor>();
			var gqlFiles = GetAllFiles(folderPath, extension);
			foreach (var gqlFile in gqlFiles)
			{
				var txt = File.ReadAllText(gqlFile);
				var root = parser.Parse(txt).Item1;

				var funcs = GetFuncs(root);
				foreach (var func in funcs)
				{
					anchors.Add(GetAnchorFromNode(func, gqlFile));
				}
			}

			return Task.FromResult(new ListAnchorsResult { folderPath = folderPath, anchors = anchors });
		}

		private Anchor GetAnchorFromNode(Node n, string filepath)
		{
			var typeName = n.Parent.Children[1].ToString().Replace("id: ", "");
			var name = n.Children.First().ToString().Replace("id: ", "");

			var args = n.Children.Skip(1).
				TakeWhile(x => x.Type == "func_arg").
				Select(x => new Tuple<string, string>(
					x.Children[0].Children[1].Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", ""), // type
					x.Children[0].Children[0].ToString().Replace("id: ", "") // name
				));

			var returnType = n.Children.Last().Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "");

			var anchor = MethodAnchor.FromRaw(
				n.Id.ToString(),
				n.Location.Start.Offset,
				n.Location.End.Offset,
				name,
				args,
				new List<string> { returnType },
				typeName);
			return new Anchor
			{
				Id = anchor.Id,
				Filepath = filepath,
				StartOffset = n.Location.Start.Offset,
				EndOffset = n.Location.End.Offset,
				MethodNameNorm = anchor.MethodNameNorm,
				ParentNameNorm = anchor.ParentNameNorm,
				ReturnTypeNorm = anchor.ReturnTypeNorm,
				Args = anchor.Args.Select(x => new Arg { NameNorm = x.NameNorm, TypeNorm = x.TypeNorm }).ToList()
			};
		}

		private List<Node> GetFuncs(Node root)
		{
			var res = new List<Node>();
			if (root == null)
			{
				return res;
			}

			foreach (var child in root.Children)
			{
				if (child.ToString() != "type_def")
				{
					continue;
				}
				foreach (var def in child.Children)
				{
					if (def.ToString() == "func_line")
					{
						res.Add(def);
					}
				}
			}

			return res;
		}

		private IEnumerable<string> GetAllFiles(string folder, string ext)
		{
			return Directory.EnumerateFiles(folder, $"*.{ext}", SearchOption.AllDirectories).
				Where(x => !x.Contains(@"\vendor\"));
		}


		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
