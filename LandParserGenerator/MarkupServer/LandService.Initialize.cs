using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		[JsonRpcMethod("land/initialize", UseSingleObjectParameterDeserialization = true)]
		public Task<InitializeResult> InitializeAsync(InitializeParams p)
		{
			try
			{
				// Начиная с этого релиза сервер использует только компактный протокол v2.
				// Клиент может присылать protocolVersion, но мы его игнорируем и всегда отвечаем v2.
				Debug($"[init] clientProtocolVersion={p?.protocolVersion} (serverProtocolVersion=2) gqlPath={p?.graphqlParserPath}, tsPath={p?.typescriptParserPath}");

				// Поддерживаем два режима:
				// 1) Плагин прислал явные пути к *.land грамматикам
				// 2) Плагин не прислал пути (старый протокол) — берём файлы рядом с сервером (graphql.land / ts_resolvers.land)
				var gqlGrammarPath = p.graphqlParserPath;
				var tsGrammarPath = p.typescriptParserPath;
				if (string.IsNullOrWhiteSpace(gqlGrammarPath))
					gqlGrammarPath = Path.Combine(AppContext.BaseDirectory, "graphql.land");
				if (string.IsNullOrWhiteSpace(tsGrammarPath))
					tsGrammarPath = Path.Combine(AppContext.BaseDirectory, "ts_resolvers.land");

				gqlGrammarPath = Path.GetFullPath(gqlGrammarPath);
				tsGrammarPath = Path.GetFullPath(tsGrammarPath);

				var messages = new List<Message>();
				graphqlParser = Builder.BuildParser(
				    GrammarType.LR,
				    File.ReadAllText(gqlGrammarPath),
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
				    File.ReadAllText(tsGrammarPath),
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
					protocolVersion = 2,
					capabilities = new Capabilities { pushUpdates = false }
				});
			}
			catch (Exception ex)
			{
				try { Console.Error.WriteLine("[fatal] " + ex); } catch { }
				return Task.FromResult(new InitializeResult
				{
					serverVersion = "1.0.0",
					protocolVersion = 2,
					capabilities = new Capabilities { pushUpdates = false }
				});
			}
		}
	}
}