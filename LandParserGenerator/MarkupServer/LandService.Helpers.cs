using Land.Core.Parsing.Tree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		private static string Sha256Hex(string s)
		{
			var bytes = Encoding.UTF8.GetBytes(s);
			var hash = SHA256.HashData(bytes);
			var sb = new StringBuilder(hash.Length * 2);
			foreach (var b in hash)
				sb.Append(b.ToString("x2"));
			return sb.ToString();
		}

		private static string NewAnchorId(string kind)
		{
			// ВАЖНО: ID якоря НЕ должен зависеть ни от offsets, ни от имени метода/поля.
			// Иначе при редактировании текста или при rename якорь перестанет находиться по id.
			//
			// Логика перепривязки (updateAnchor) использует этот id как «идентификатор сущности якоря»
			// и достаёт по нему признаки старой версии из кэша (nodesById).
			// Поэтому id должен быть стабильным хотя бы в рамках одной сессии (между listAnchors и updateAnchor).
			return $"{kind}:{Guid.NewGuid():N}";
		}

		private static string MakeGroupId(string kind, params string[] parts)
		{
			// Группы можно идентифицировать детерминированно (чтобы дерево не прыгало).
			var key = kind + "|" + string.Join("|", parts.Select(p => p ?? ""));
			return $"{kind}:{Sha256Hex(key)}";
		}

		private TreeNode GetTreeNodeFromTsNode(Node n, string filepath, string parentName)
		{
			filepath = Path.GetFullPath(filepath);
			var name = n.Children[0].ToString().Replace("ID: ", "");
			var args = n.Children[1].Children.Select(x => new Tuple<string, string>(
				x.ToString().Replace("arg", ""),
				"")).
				Where(x => x.Item1 != "").ToList();

			var start = n.Location.Start.Offset;
			var end = n.Location.End.Offset;

			return new TreeNode
			{
				Id = NewAnchorId("ts"),
				Name = name,
				NodeType = "anchor",
				Filepath = filepath,
				StartOffset = start,
				EndOffset = end,
				MethodNameNorm = NormalizeName(name),
				ParentNameNorm = NormalizeName(parentName),
				ParentNameRaw = parentName,
				ReturnTypeNorm = NormalizeReturnTypes(new List<string> { "" }),
				Args = NormalizeArgs(args),
			};
		}

		private TreeNode GetTreeNodeFromGqlNode(Node n, string filepath)
		{
			filepath = Path.GetFullPath(filepath);
			var typeName = n.Parent.Children[1].ToString().Replace("id: ", "");
			var name = n.Children.First().ToString().Replace("id: ", "");

			var args = n.Children.Skip(1).
				TakeWhile(x => x.Type == "func_arg").
				Select(x => new Tuple<string, string>(
					x.Children[0].Children[1].Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", ""), // type
					x.Children[0].Children[0].ToString().Replace("id: ", "") // name
				));

			var returnType = n.Children.Last().Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "");

			var start = n.Location.Start.Offset;
			var end = n.Location.End.Offset;

			return new TreeNode
			{
				Id = NewAnchorId("gql"),
				Name = name,
				NodeType = "anchor",
				Filepath = filepath,
				StartOffset = start,
				EndOffset = end,
				MethodNameNorm = NormalizeName(name),
				ParentNameNorm = NormalizeName(typeName),
				ParentNameRaw = typeName,
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

		private int ParseGqlFile(string gqlFile, List<TreeNode> roots, List<MethodAnchor> gqlAnchors)
		{
			var gqlAnchorsCnt = 0;
			gqlFile = Path.GetFullPath(gqlFile);
			var txt = File.ReadAllText(gqlFile);
			Node root;
			using (Tracing.Tracer.BuildSpan("ParseGql").StartActive())
				root = graphqlParser.Parse(txt).Item1;

			var funcs = GetFuncs(root);
			foreach (var funcsPerType in funcs)
			{
				var group = new TreeNode
				{
					Id = MakeGroupId("gqlType", gqlFile, funcsPerType.Key),
					Name = funcsPerType.Key,
					NodeType = "group",
				};
				foreach (var func in funcsPerType.Value)
				{
					var treeNode = GetTreeNodeFromGqlNode(func, gqlFile);
					nodesById[treeNode.Id] = treeNode;

					gqlAnchors.Add(new MethodAnchor
					{
						Id = treeNode.Id,
						ParentNameNorm = treeNode.ParentNameNorm,
						MethodNameNorm = treeNode.MethodNameNorm,
						ReturnTypeNorm = treeNode.ReturnTypeNorm,
						StartOffset = treeNode.StartOffset ?? 0,
						EndOffset = treeNode.EndOffset ?? 0,
						Args = treeNode.Args.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
					});
					gqlAnchorsCnt++;
					group.Children.Add(treeNode);
				}

				roots.Add(group);
			}
			return gqlAnchorsCnt;
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



		private static string CleanTsId(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			return s.Replace("ID: ", "").Replace("id: ", "").Trim();
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
					var className = CleanTsId(child.Children[1].ToString());
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

		private static double TokenOverlap(string aNorm, string bNorm)
		{
			if (string.IsNullOrWhiteSpace(aNorm) || string.IsNullOrWhiteSpace(bNorm)) return 0;
			var a = aNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var b = bNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (a.Length == 0 || b.Length == 0) return 0;
			var setB = new HashSet<string>(b);
			var inter = a.Count(setB.Contains);
			return (double)inter / a.Length;
		}

		private static bool ParentMatchLoose(string gqlParentNorm, string tsParentNorm)
		{
			if (string.IsNullOrWhiteSpace(gqlParentNorm) || string.IsNullOrWhiteSpace(tsParentNorm)) return false;
			if (gqlParentNorm == tsParentNorm) return true;
			// "query" должно матчиться с "query resolver" и т.п.
			if (tsParentNorm.Contains(gqlParentNorm) || gqlParentNorm.Contains(tsParentNorm)) return true;
			return TokenOverlap(gqlParentNorm, tsParentNorm) >= 0.8;
		}

		private TreeNode PickBestTsResolver(TreeNode gqlAnchor, List<TreeNode> candidates)
		{
			if (candidates == null || candidates.Count == 0) return null;
			var gqlParent = gqlAnchor?.ParentNameNorm ?? "";
			var gqlMethod = gqlAnchor?.MethodNameNorm ?? "";
			TreeNode best = null;
			double bestScore = double.NegativeInfinity;
			foreach (var ts in candidates)
			{
				if (ts == null) continue;
				if ((ts.MethodNameNorm ?? "") != gqlMethod) continue;
				var score = 0.0;
				var tsParent = ts.ParentNameNorm ?? "";
				// базовый скор по совпадению родителя
				if (tsParent == gqlParent) score += 10;
				else if (ParentMatchLoose(gqlParent, tsParent)) score += 5;
				else score += TokenOverlap(gqlParent, tsParent);

				// tie-breaker: путь содержит "resolver"
				var fp = (ts.Filepath ?? "").Replace('\\', '/').ToLowerInvariant();
				if (fp.Contains("resolver")) score += 0.2;

				if (score > bestScore)
				{
					bestScore = score;
					best = ts;
				}
			}
			// фильтр на совсем слабые матчи
			if (best == null) return null;
			if (bestScore < 1.0) return null;
			return best;
		}

		/// <summary>
		/// Объединяет GraphQL поле и TypeScript резолвер в один "field"-узел:
		/// gqlTypeGroup -> gqlFieldGroup -> [gqlAnchor, tsAnchor?]
		/// Неиспользованные TS-якоря уходят в отдельную группу "TypeScript (unmatched)".
		/// </summary>
		private List<TreeNode> MergeGqlAndTs(List<TreeNode> gqlRoots, List<TreeNode> tsNodes)
		{
			gqlRoots ??= new List<TreeNode>();
			tsNodes ??= new List<TreeNode>();

			var unusedTs = new List<TreeNode>(tsNodes.Where(x => x != null));

			// индекс TS по точному ключу parent|method
			var tsByExact = new Dictionary<string, List<TreeNode>>();
			var tsByMethod = new Dictionary<string, List<TreeNode>>();
			foreach (var ts in unusedTs)
			{
				var key = (ts.ParentNameNorm ?? "") + "|" + (ts.MethodNameNorm ?? "");
				if (!tsByExact.TryGetValue(key, out var list1))
					tsByExact[key] = list1 = new List<TreeNode>();
				list1.Add(ts);

				var m = ts.MethodNameNorm ?? "";
				if (!tsByMethod.TryGetValue(m, out var list2))
					tsByMethod[m] = list2 = new List<TreeNode>();
				list2.Add(ts);
			}

			TreeNode TakeTsMatch(TreeNode gqlAnchor)
			{
				var exactKey = (gqlAnchor.ParentNameNorm ?? "") + "|" + (gqlAnchor.MethodNameNorm ?? "");
				if (tsByExact.TryGetValue(exactKey, out var exactList) && exactList.Count > 0)
				{
					var ts = exactList[0];
					exactList.RemoveAt(0);
					unusedTs.RemoveAll(x => x.Id == ts.Id);
					return ts;
				}

				var method = gqlAnchor.MethodNameNorm ?? "";
				if (!tsByMethod.TryGetValue(method, out var cands) || cands.Count == 0)
					return null;

				// сначала фильтруем по "похожему" parent
				var filtered = cands.Where(x => ParentMatchLoose(gqlAnchor.ParentNameNorm ?? "", x.ParentNameNorm ?? "")).ToList();
				var best = PickBestTsResolver(gqlAnchor, filtered.Count > 0 ? filtered : cands);
				if (best == null) return null;

				// вынимаем из индексов
				cands.RemoveAll(x => x.Id == best.Id);
				unusedTs.RemoveAll(x => x.Id == best.Id);
				return best;
			}

			foreach (var typeGroup in gqlRoots.Where(x => x != null && x.NodeType == "group"))
			{
				var nextChildren = new List<TreeNode>();
				foreach (var gqlAnchor in (typeGroup.Children ?? new List<TreeNode>()).Where(x => x != null && x.NodeType == "anchor"))
				{
					var typeRaw = gqlAnchor.ParentNameRaw ?? typeGroup.Name ?? "";
					var fieldGroupId = MakeGroupId("gqlField", gqlAnchor.Filepath, typeRaw, gqlAnchor.Name);
					var fieldGroup = new TreeNode
					{
						Id = fieldGroupId,
						Name = gqlAnchor.Name,
						NodeType = "group",
						Children = new List<TreeNode>(),
					};
					fieldGroup.Children.Add(gqlAnchor);

					var ts = TakeTsMatch(gqlAnchor);
					if (ts != null)
						fieldGroup.Children.Add(ts);

					nextChildren.Add(fieldGroup);
				}
				typeGroup.Children = nextChildren;
			}

			// Орфаны TS
			if (unusedTs.Count > 0)
			{
				var orphanRoot = new TreeNode
				{
					Id = MakeGroupId("tsOrphans", currentFolderPath ?? ""),
					Name = "TypeScript (unmatched)",
					NodeType = "group",
					Children = new List<TreeNode>(),
				};

				foreach (var grp in unusedTs.GroupBy(x => x.ParentNameRaw ?? "(root)"))
				{
					var g = new TreeNode
					{
						Id = MakeGroupId("tsClass", grp.Key, currentFolderPath ?? ""),
						Name = grp.Key,
						NodeType = "group",
						Children = grp.ToList(),
					};
					orphanRoot.Children.Add(g);
				}

				gqlRoots.Add(orphanRoot);
			}

			return gqlRoots;
		}

		private void Debug(string msg)
		{
			Console.Error.WriteLine(msg);
		}

		private IEnumerable<string> GetAllFiles(string folder, string ext)
		{
			folder = Path.GetFullPath(folder);
			return Directory.EnumerateFiles(folder, $"*.{ext}", SearchOption.AllDirectories)
				.Select(Path.GetFullPath)
				.Where(x =>
				{
					var norm = x.Replace('\\', '/').ToLowerInvariant();
					// базовые исключения, чтобы не парсить зависимости
					return !norm.Contains("/vendor/") && !norm.Contains("/node_modules/") && !norm.Contains("/dist/");
				});
		}

	}
}
