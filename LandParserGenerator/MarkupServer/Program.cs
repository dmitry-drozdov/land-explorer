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
					return 1;
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

				// TCP-режим
				if (args.Length >= 2 && args[0] == "--tcp" || true)
				{
					var ep = ParseEndPoint("127.0.0.1:7711"); // args[1]
					Console.Error.WriteLine($"[boot] TCP mode on {ep}");

					var listener = new TcpListener(ep);
					listener.Start();

					// слушаем клиентов в цикле
					while (true)
					{
						Console.Error.WriteLine("[boot] waiting for client...");

						TcpClient client = null;

						try
						{
							client = await listener.AcceptTcpClientAsync();
							Console.Error.WriteLine("[boot] client accepted");

							using (client)
							using (var stream = client.GetStream())
							{
								var svc = new LandService();
								var rpc = JsonRpc.Attach(stream, stream, svc); // уже начинает слушать

								Console.Error.WriteLine("[boot] client connected");

								try
								{
									await rpc.Completion;
									Console.Error.WriteLine("[boot] rpc completion (normal)");
								}
								catch (IOException ex)
								{
									// Нормальная ситуация: клиент просто закрыл соединение
									Console.Error.WriteLine("[info] client disconnected (IO): " + ex.Message);
								}
								catch (ObjectDisposedException)
								{
									Console.Error.WriteLine("[info] client disconnected (disposed)");
								}
								catch (OperationCanceledException)
								{
									Console.Error.WriteLine("[info] client disconnected (canceled)");
								}
							}

							// после завершения RPC-сессии просто ждём следующего клиента
						}
						catch (Exception ex)
						{
							// Любая странная ошибка при accept/handle — логируем и продолжаем слушать
							Console.Error.WriteLine("[warn] exception while handling client: " + ex);
						}
					}
				}
				else
				{
					// STDIO-режим (единый клиент, после его смерти — спокойно выходим)
					Console.Error.WriteLine("[boot] stdio mode");

					using var input = Console.OpenStandardInput();
					using var output = Console.OpenStandardOutput();
					var svc = new LandService();
					var rpc = JsonRpc.Attach(output, input, svc);

					try
					{
						await rpc.Completion;
						Console.Error.WriteLine("[boot] rpc completion (stdio)");
					}
					catch (IOException ex)
					{
						Console.Error.WriteLine("[info] stdio disconnected (IO): " + ex.Message);
					}
					catch (ObjectDisposedException)
					{
						Console.Error.WriteLine("[info] stdio disconnected (disposed)");
					}
					catch (OperationCanceledException)
					{
						Console.Error.WriteLine("[info] stdio disconnected (canceled)");
					}

					return 0;
				}
			}
			catch (Exception ex)
			{
				// сюда теперь не долетят нормальные "клиент отключился"
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
		public string Id = Guid.NewGuid().ToString();
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
	
}
