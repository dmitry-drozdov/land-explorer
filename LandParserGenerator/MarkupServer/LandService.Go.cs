using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkupServer
{
	/// <summary>
	/// Go: точки привязки — функции и методы (сущность <c>land func</c> грамматики golang_markup.land, вторая грамматика
	/// из работы о легковесных грамматиках Go). Описание точки — те же пять признаков, что у поля GraphQL:
	/// имя функции; пары «имя–тип» параметров (группы <c>a, b int</c> раскрываются, как в GoPreprocessor.Postprocess);
	/// список результатов «T1,T2»; родитель — тип получателя без <c>*</c> и параметров типа, у функции без получателя —
	/// имя пакета; соседи — мешок по группе (файл, родитель), как у остальных языков.
	/// Разбор Go включается, только если при инициализации передан путь к грамматике (goParserPath): плагин его
	/// не передаёт, поведение для GraphQL и TypeScript не меняется. Используется стендами markup/experiments/e20_go_real_history.
	/// </summary>
	public partial class LandService
	{
		private const string LangGo = "go";
		private const string AnchorKindGoFunc = "goFunc";

		private BaseParser goParser;

		private void InitGoParser(string grammarPath)
		{
			if (string.IsNullOrWhiteSpace(grammarPath))
				return;
			grammarPath = Path.GetFullPath(grammarPath);
			var messages = new List<Message>();
			goParser = Builder.BuildParser(GrammarType.LR, File.ReadAllText(grammarPath), messages);
			if (messages.Any(m => m.Type == MessageType.Error))
			{
				var error = messages.First(m => m.Type == MessageType.Error);
				Debug($"Cannot generate Go parser {error.Text}");
				Environment.Exit(1);
			}
			Debug("Parser Go Generated");
		}

		private sealed class GoFuncSketch
		{
			public Node Node;
			public string Name;
			public string Receiver;       // базовый тип получателя или null у функции
			public List<Tuple<string, string>> Args;  // (тип, имя)
			public List<string> Returns;
		}

		/// <summary>Разбирает файлы Go и добавляет точки для всех функций и методов; мешки соседей — общей реализацией.</summary>
		private int ParseGoFiles(IEnumerable<string> goFiles, List<TreeNode> goAnchors)
		{
			var count = 0;
			foreach (var goFile1 in goFiles)
			{
				var goFile = Path.GetFullPath(goFile1);
				var txt = File.ReadAllText(goFile);
				Node root;
				_swParse.Start();
				try
				{
					using (Tracing.Tracer.BuildSpan("ParseGo").StartActive())
						root = goParser.Parse(txt).Item1;
				}
				catch (Exception ex)
				{
					// файл, который легковесный парсер не разбирает, пропускается целиком; остальные файлы версии обрабатываются
					Debug($"[go] parse failed: {goFile}: {ex.GetType().Name}: {ex.Message}");
					root = null;
				}
				finally
				{
					_swParse.Stop();
				}
				if (root == null)
					continue;

				if (Environment.GetEnvironmentVariable("LAND_GO_DUMP") == "1")
					Debug("[go-tree] " + goFile + "\n" + DumpGoTree(root, txt, 0));

				string pkg;
				var funcs = new List<GoFuncSketch>();
				try
				{
					pkg = GetGoPackageName(root, txt);
					CollectGoFuncs(root, txt, funcs);
				}
				catch (Exception ex)
				{
					Debug($"[go] parse failed: {goFile}: {ex.GetType().Name}: {ex.Message}");
					continue;
				}

				var anchorsOfFile = new List<TreeNode>();
				foreach (var f in funcs)
				{
					var parentRaw = f.Receiver ?? pkg;
					anchorsOfFile.Add(new TreeNode
					{
						Id = NewAnchorId("go"),
						// отображаемое имя: «Получатель.Имя» у метода, «Имя» у функции
						Name = f.Receiver != null ? f.Receiver + "." + f.Name : f.Name,
						NodeType = "anchor",
						Language = LangGo,
						AnchorKind = AnchorKindGoFunc,
						AnchorFamily = AnchorFamilyCallable,
						Filepath = goFile,
						StartOffset = f.Node.Location.Start.Offset,
						EndOffset = f.Node.Location.End.Offset,
						MethodNameNorm = NormalizeName(f.Name),
						ParentNameNorm = NormalizeName(parentRaw),
						ParentNameRaw = parentRaw,
						ReturnTypeNorm = NormalizeReturnTypes(f.Returns),
						Args = NormalizeArgs(f.Args),
					});
				}

				// порядок внутри группы соседей (файл, нормализованный родитель) — по положению в файле
				foreach (var g in anchorsOfFile.GroupBy(x => x.ParentNameNorm ?? "", StringComparer.Ordinal))
				{
					var i = 0;
					foreach (var a in g.OrderBy(x => x.StartOffset ?? 0))
						a.OrdinalInParent = i++;
				}

				foreach (var a in anchorsOfFile)
				{
					goAnchors.Add(a);
					nodesById[a.Id] = a;
					count++;
				}
			}

			_swBags.Start();
			AssignNeighborBags(goAnchors);
			_swBags.Stop();
			return count;
		}

		private static string GetGoPackageName(Node root, string text)
		{
			var stack = new Stack<Node>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				var n = stack.Pop();
				if (n == null) continue;
				if (n.Type == "package_name")
					return CleanGoIdent(Slice(n, text));
				if (n.Type == "func" || n.Type == "package_content") continue;
				if (n.Children != null)
					for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
			}
			return "";
		}

		private static void CollectGoFuncs(Node n, string text, List<GoFuncSketch> res)
		{
			if (n == null) return;
			if (n.Type == "func")
			{
				var s = SketchGoFunc(n, text);
				if (s != null) res.Add(s);
				return;   // тело — Any, вложенных объявлений func в дереве нет
			}
			if (n.Children != null)
				foreach (var ch in n.Children) CollectGoFuncs(ch, text, res);
		}

		private static GoFuncSketch SketchGoFunc(Node fn, string text)
		{
			var children = fn.Children ?? new List<Node>();
			var nameNode = children.FirstOrDefault(c => c.Type == "f_name");
			if (nameNode == null) return null;
			var name = CleanGoIdent(Slice(nameNode, text));
			if (name.Length == 0) return null;

			string receiver = null;
			var recv = children.FirstOrDefault(c => c.Type == "f_reciever");
			if (recv != null)
			{
				var ft = (recv.Children ?? new List<Node>()).FirstOrDefault(c => c.Type == "f_type");
				receiver = ft != null ? GoReceiverBaseType(Slice(ft, text)) : GoReceiverBaseType(Slice(recv, text));
			}

			var argsNode = children.FirstOrDefault(c => c.Type == "f_args");
			var args = new List<Tuple<string, string>>();
			if (argsNode != null)
			{
				var items = (argsNode.Children ?? new List<Node>()).Where(c => c.Type == "f_arg").ToList();
				foreach (var (type, pname) in GoParamList(items, text))
					args.Add(new Tuple<string, string>(type, pname));
			}

			var returns = new List<string>();
			var retNode = children.FirstOrDefault(c => c.Type == "f_returns");
			if (retNode != null)
			{
				var rch = retNode.Children ?? new List<Node>();
				var items = rch.Where(c => c.Type == "f_return").ToList();
				if (items.Count > 0)
					returns.AddRange(GoParamList(items, text).Select(x => x.type));
				else
				{
					// один тип без скобок или нераспознанный хвост (Any): текст результатов целиком
					var s = Slice(retNode, text).Trim();
					if (s.StartsWith("(") && s.EndsWith(")")) s = s.Substring(1, s.Length - 2).Trim();
					if (s.Length > 0) returns.Add(BalanceGoBrackets(s));
				}
			}

			return new GoFuncSketch { Node = fn, Name = name, Receiver = receiver, Args = args, Returns = returns };
		}

		/// <summary>
		/// Список параметров или именованных результатов Go: элемент из двух частей — «имя тип», из одной — либо тип
		/// (если в списке нет ни одного элемента из двух частей), либо имя из группы <c>a, b int</c>, тип которого берётся
		/// у ближайшего элемента справа (правило спецификации Go; так же раскрывает группы GoPreprocessor.Postprocess).
		/// </summary>
		private static List<(string type, string name)> GoParamList(List<Node> items, string text)
		{
			var parts = new List<(string first, string second)>();
			foreach (var it in items)
			{
				var ch = (it.Children ?? new List<Node>()).Where(c => c.Location != null).ToList();
				var spread = ch.Any(c => c.Type == "SPREAD");
				var typeish = ch.Where(c => c.Type != "SPREAD").ToList();
				if (typeish.Count >= 2)
				{
					// «x []int» грамматика разбирает как go_type(ID x, generic2 []) + go_type(int): скобки относятся к типу
					var head = typeish[0];
					var hch = (head.Children ?? new List<Node>()).Where(c => c.Location != null).ToList();
					Node nameNode = head, typeFrom = typeish[1];
					if (head.Type == "go_type" && hch.Count == 2 && hch[0].Type == "ID" && hch[1].Type == "generic2")
					{
						nameNode = hch[0];
						typeFrom = hch[1];
					}
					var first = CleanGoIdent(Slice(nameNode, text));
					var t = SliceRange(typeFrom, typeish[typeish.Count - 1], text).Trim();
					parts.Add((first, (spread ? "..." : "") + t));
				}
				else if (typeish.Count == 1)
				{
					var t = Slice(typeish[0], text).Trim();
					parts.Add(((spread ? "..." : "") + t, null));
				}
				else
				{
					parts.Add((Slice(it, text).Trim(), null));
				}
			}

			var named = parts.Any(p => p.second != null);
			var res = new List<(string type, string name)>(parts.Count);
			if (!named)
			{
				foreach (var p in parts) res.Add((p.first, ""));
				return res;
			}

			string lastType = "";
			var tmp = new (string type, string name)[parts.Count];
			for (var i = parts.Count - 1; i >= 0; i--)
			{
				var p = parts[i];
				if (p.second != null) { lastType = p.second; tmp[i] = (p.second, p.first); }
				else tmp[i] = (lastType, CleanGoIdent(p.first));
			}
			res.AddRange(tmp);
			return res.Select(x => (BalanceGoBrackets(x.type), x.name)).ToList();
		}

		/// <summary>Скобки LB/RB в грамматике пустые (void), и срез типа-функции теряет закрывающую «)»: дописываем недостающие.</summary>
		private static string BalanceGoBrackets(string s)
		{
			if (string.IsNullOrEmpty(s)) return s ?? "";
			int round = 0, square = 0;
			foreach (var ch in s)
			{
				if (ch == '(') round++; else if (ch == ')') round--;
				else if (ch == '[') square++; else if (ch == ']') square--;
			}
			var sb = new StringBuilder(s);
			for (; square > 0; square--) sb.Append(']');
			for (; round > 0; round--) sb.Append(')');
			return sb.ToString();
		}

		private static string SliceRange(Node from, Node to, string text)
		{
			if (from?.Location == null || to?.Location == null) return "";
			var starts = CodePointStarts(text);
			var start = CpToUtf16(starts, Math.Max(0, from.Location.Start.Offset));
			var endExclusive = Math.Min(text.Length, CpToUtf16(starts, to.Location.End.Offset + 1));
			return endExclusive <= start ? "" : text.Substring(start, endExclusive - start);
		}

		private static readonly Regex GoRecvRe = new Regex(@"^\(\s*(?:[\p{L}_][\p{L}\p{Nd}_]*\s+)?\*?\s*\(?\s*\*?\s*([\p{L}_][\p{L}\p{Nd}_]*)", RegexOptions.Compiled);

		/// <summary>«(s *Store[T])» → «Store».</summary>
		private static string GoReceiverBaseType(string recvText)
		{
			var s = (recvText ?? "").Trim();
			var m = GoRecvRe.Match(s);
			if (m.Success) return m.Groups[1].Value;
			s = s.Trim('(', ')', ' ', '*');
			var br = s.IndexOf('[');
			if (br >= 0) s = s.Substring(0, br);
			var sp = s.LastIndexOf(' ');
			if (sp >= 0) s = s.Substring(sp + 1);
			return s.Trim('*', ' ');
		}

		private static string CleanGoIdent(string s) => (s ?? "").Trim().Trim(',', ';');

		private static string DumpGoTree(Node n, string text, int depth)
		{
			var sb = new StringBuilder();
			void Walk(Node x, int d)
			{
				if (x == null || d > 12) return;
				var s = Slice(x, text).Replace("\r", " ").Replace("\n", " ");
				if (s.Length > 60) s = s.Substring(0, 60) + "…";
				sb.Append(new string(' ', d * 2)).Append(x.Type).Append(" | ").Append(x.ToString()).Append(" | «").Append(s).Append("»\n");
				if (x.Type == "func" && d > 0 && x.Children != null)
				{
					foreach (var c in x.Children) Walk(c, d + 1);
					return;
				}
				if (x.Children != null) foreach (var c in x.Children) Walk(c, d + 1);
			}
			Walk(n, depth);
			return sb.ToString();
		}
	}
}
