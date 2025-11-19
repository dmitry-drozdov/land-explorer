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
	}
}
