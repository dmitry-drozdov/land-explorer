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
using Jaeger;
using VPTree;


namespace MarkupServer
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
		public string typescriptParserPath { get; set; }
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
		private BaseParser graphqlParser;
		private BaseParser typescriptParser;
		[JsonRpcMethod("land/initialize", UseSingleObjectParameterDeserialization = true)]
		public Task<InitializeResult> InitializeAsync(InitializeParams p)
		{
			try
			{
				Debug($"[init] protocolVersion={p.protocolVersion} p.graphqlParserPath={p.graphqlParserPath}, p.typescriptPath={p.typescriptParserPath}");

				var messages = new List<Message>();
				graphqlParser = Builder.BuildParser(
				    GrammarType.LR,
				    File.ReadAllText(p.graphqlParserPath),
				    messages
				);
				if (messages.Any(m => m.Type == MessageType.Error))
				{
					var error = messages.First(m => m.Type == MessageType.Error);
					Debug($"Cannot generate parser {error.Text}");
					Environment.Exit(1);
				}
				else
					Debug("Parser GQL Generated");


				messages = new List<Message>();
				typescriptParser = Builder.BuildParser(
				    GrammarType.LR,
				    File.ReadAllText(p.typescriptParserPath),
				    messages
				);
				if (messages.Any(m => m.Type == MessageType.Error))
				{
					var error = messages.First(m => m.Type == MessageType.Error);
					Debug($"Cannot generate parser {error.Text}");
					Environment.Exit(1);
				}
				else
					Debug("Parser TS Generated");

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



		/*[JsonRpcMethod("land/listAnchors", UseSingleObjectParameterDeserialization = true)]
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
		}*/

		[JsonRpcMethod("land/listAnchors", UseSingleObjectParameterDeserialization = true)]
		public Task<ListTreeResult> ListAnchorsAsync(ListTreeParams p)
		{
			var roots = new List<TreeNode> { };
			Tracing.Init();



			IEnumerable<string> gqlFiles = GetAllFiles(p.folderPath, "graphql");
			List<MethodAnchor> gqlAnchors = new List<MethodAnchor>();

			var gqlAnchorsCnt = 0;
			using (var scope = Tracing.Tracer.BuildSpan("ProcessGqlFiles").StartActive())
				foreach (var gqlFile in gqlFiles)
				{
					var txt = File.ReadAllText(gqlFile);
					Node root;
					using (Tracing.Tracer.BuildSpan("ParseGql").StartActive())
						root = graphqlParser.Parse(txt).Item1;

					var funcs = GetFuncs(root);
					foreach (var funcsPerType in funcs)
					{
						var group = new TreeNode
						{
							Name = funcsPerType.Key,
							NodeType = "group",
						};
						foreach (var func in funcsPerType.Value)
						{
							var anchor = GetAnchorFromGqlNode(func, gqlFile);

							gqlAnchors.Add(new MethodAnchor
							{
								ParentNameNorm = anchor.ParentNameNorm,
								MethodNameNorm = anchor.MethodNameNorm,
								ReturnTypeNorm = anchor.ReturnTypeNorm,
								StartOffset = anchor.StartOffset ?? 0,
								EndOffset = anchor.EndOffset ?? 0,
								Args = anchor.Args.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
							});

							gqlAnchorsCnt++;
							var subgroup = new TreeNode
							{
								Name = anchor.Name,
								NodeType = "group",
							};
							anchor.Name = "graphql";
							subgroup.Children.Add(anchor);
							group.Children.Add(subgroup);
						}

						roots.Add(group);
					}
				}


			var _w = new Dist.Weights();
			VPTree<MethodAnchor> _tree;
			using (var scope = Tracing.Tracer.BuildSpan("BuildTree").StartActive())
				_tree = new VPTree<MethodAnchor>(gqlAnchors, (a, b) => Dist.AnchorDistance(a, b, _w), 42);

			/*for (int i = 0; i < 10; i++)
				using (var scope = Tracing.Tracer.BuildSpan("FindPoint").StartActive())
				{
					var res = _tree.KNearest(gqlAnchors[i],2);
					Debug($"{res[0].Dist} {res[1].Dist}");
					
				}*/

			IEnumerable<string> tsFiles = GetAllFiles(p.folderPath, "ts");

			var tsAnchors = 0;
			using (var scope = Tracing.Tracer.BuildSpan("ProcessTsFiles").StartActive())
				foreach (var tsFile in tsFiles)
				{
					var txt = File.ReadAllText(tsFile);
					var root = typescriptParser.Parse(txt).Item1;
					var nodesPerClass = GetTsNodes(root);
					foreach (var nodes in nodesPerClass)
					{
						foreach (var node in nodes.Value)
						{
							var anchor = GetAnchorFromTsNode(node, tsFile, nodes.Key);
							tsAnchors++;
							roots.Add(anchor);
						}
					}
				}

			Debug($"gqlAnchors={gqlAnchorsCnt}, tsAnchors={tsAnchors}");

			return Task.FromResult(new ListTreeResult { Roots = roots });
		}

		private TreeNode GetAnchorFromTsNode(Node n, string filepath, string parentName)
		{
			var name = n.Children[0].ToString().Replace("ID: ", "").ToLower();
			var args = n.Children[1].Children.Select(x => new Tuple<string, string>(
				x.ToString().Replace("arg", ""),
				"")).
				Where(x => x.Item1 != "").ToList();

			return new TreeNode
			{
				Id = n.Id.ToString(),
				Name = name,
				NodeType = "anchor",
				Filepath = filepath,
				StartOffset = n.Location.Start.Offset,
				EndOffset = n.Location.End.Offset,
				MethodNameNorm = NormalizeName(name),
				ParentNameNorm = NormalizeName(parentName),
				ReturnTypeNorm = NormalizeReturnTypes(new List<string> { "" }),
				Args = NormalizeArgs(args),
			};
		}

		private TreeNode GetAnchorFromGqlNode(Node n, string filepath)
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

			return new TreeNode
			{
				Id = n.Id.ToString(),
				Name = name,
				NodeType = "anchor",
				Filepath = filepath,
				StartOffset = n.Location.Start.Offset,
				EndOffset = n.Location.End.Offset,
				MethodNameNorm = NormalizeName(name),
				ParentNameNorm = NormalizeName(typeName),
				ReturnTypeNorm = NormalizeReturnTypes(new List<string> { returnType }),
				Args = NormalizeArgs(args),
			};
		}

		public static List<Arg> NormalizeArgs(IEnumerable<Tuple<string, string>> args)
		{
			var res = new List<Arg>();
			foreach (var t in args)
			{
				var ar = new Arg
				{
					TypeNorm = NormalizeTypeName(t != null ? (t.Item1 ?? "") : ""),
					NameNorm = NormalizeName(t != null ? (t.Item2 ?? "") : "")
				};
				res.Add(ar);
			}
			return res;
		}

		public static string NormalizeName(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			// split camelCase/PascalCase
			var withSpaces = System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
			withSpaces = withSpaces.Replace('_', ' ');
			var tokens = withSpaces.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			return string.Join(" ", tokens);
		}

		public static string NormalizeTypeName(string t)
		{
			if (string.IsNullOrWhiteSpace(t)) return "";
			var s = t.Trim();
			s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
			s = s.Replace(" *", "*").Replace("* ", "*");
			s = s.Replace(" [", "[").Replace("[ ", "[");
			s = s.Replace(" ]", "]").Replace("] ", "]");
			s = s.Replace(" ,", ",").Replace(", ", ",");
			return s;
		}

		public static string NormalizeReturnTypes(IEnumerable<string> returns)
		{
			if (returns == null) return "";
			var arr = returns.Select(NormalizeTypeName).ToArray();
			if (arr.Length == 0) return "";
			return string.Join(",", arr);
		}

		private Dictionary<string, List<Node>> GetFuncs(Node root)
		{
			var res = new Dictionary<string, List<Node>>();
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

				var key = child.Children[1].ToString().Replace("id: ", "");
				foreach (var def in child.Children)
				{
					if (def.ToString() == "func_line")
					{
						if (!res.ContainsKey(key))
							res.Add(key, new List<Node>());

						res[key].Add(def);
					}
				}
			}

			return res;
		}

		private void Debug(string msg)
		{
			Console.Error.WriteLine(msg);
		}

		private Dictionary<string, List<Node>> GetTsNodes(Node root)
		{
			var res = new Dictionary<string, List<Node>>();
			if (root == null)
			{
				return res;
			}

			foreach (var child in root.Children)
			{
				var nodeName = child.ToString();
				if (nodeName == "struct" || nodeName == "class" || nodeName == "lamda_struct")
				{
					var className = child.Children[1].ToString();
					var list = new List<Node>();
					GetTsNodesHelp(child, list);

					if (!res.ContainsKey(className))
						res.Add(className, new List<Node>());

					res[className].AddRange(list);
				}
			}
			return res;
		}

		public void GetTsNodesHelp(Node root, List<Node> tsNodes)
		{
			var nodeName = root.ToString();
			if (nodeName == "func" || nodeName == "sub_field_func_impl" || nodeName == "sub_field_any")
			{
				tsNodes.Add(root);
				return;
			}
			foreach (var child in root.Children)
			{
				GetTsNodesHelp(child, tsNodes);
			}
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
