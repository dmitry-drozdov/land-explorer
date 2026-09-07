using Land.Core.Parsing.Tree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		private sealed class GqlMemberSketch
		{
			public string NameRaw { get; set; }
			public string SignatureNorm { get; set; }
		}

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
			return $"{kind}:{Guid.NewGuid():N}";
		}

		private static string MakeGroupId(string kind, params string[] parts)
		{
			var key = kind + "|" + string.Join("|", parts.Select(p => p ?? ""));
			return $"{kind}:{Sha256Hex(key)}";
		}

		private static void AddBagWeight(Dictionary<string, double> bag, string key, double weight)
		{
			if (bag == null || string.IsNullOrWhiteSpace(key) || weight <= 0)
				return;

			if (bag.TryGetValue(key, out var cur))
				bag[key] = cur + weight;
			else
				bag[key] = weight;
		}

		private static string InferGqlTypeKind(Node typeDef)
		{
			if (typeDef == null)
				return "type";

			foreach (var child in typeDef.Children ?? new List<Node>())
			{
				var text = (child?.ToString() ?? "").Trim().ToLowerInvariant();
				if (text == "type" || text == "input" || text == "interface")
					return text;
			}

			return "type";
		}

		private static string InferGqlAnchorKind(string gqlTypeKind)
		{
			return (gqlTypeKind ?? "").Trim().ToLowerInvariant() switch
			{
				"input" => AnchorKindGqlInput,
				"interface" => AnchorKindGqlInterface,
				_ => AnchorKindGqlType,
			};
		}

		private static bool IsGqlAnchorKind(string anchorKind)
		{
			return string.Equals(anchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlType, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInput, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInterface, StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsGqlTypeContainerKind(string anchorKind)
		{
			return string.Equals(anchorKind, AnchorKindGqlType, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInput, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInterface, StringComparison.OrdinalIgnoreCase);
		}

		private static string GetDisplayNameForTypeAnchor(string gqlTypeKind, string typeName)
		{
			if (string.IsNullOrWhiteSpace(typeName))
				return gqlTypeKind ?? "type";
			return $"{(string.IsNullOrWhiteSpace(gqlTypeKind) ? "type" : gqlTypeKind)} {typeName}";
		}

		private static string NormalizeGraphQlSnippet(string s)
		{
			if (string.IsNullOrWhiteSpace(s))
				return "";

			s = s.Trim();
			s = Regex.Replace(s, @"\s+", " ");
			s = s.Replace(" :", ":").Replace(": ", ":");
			s = s.Replace(" ,", ",").Replace(", ", ",");
			s = s.Replace(" (", "(").Replace(" )", ")");
			s = s.Replace(" [", "[").Replace(" ]", "]");
			return s.Trim().Trim(',');
		}

		/// <summary>
		/// Текст узла по его Location. В LanD Location.End.Offset указывает на ПОСЛЕДНИЙ символ
		/// узла (включительно), поэтому длина среза = end - start + 1. Прежняя реализация
		/// считала конец исключительным и теряла последний символ ("Deal!" → "Deal", "Deal" → "Dea").
		/// </summary>
		private static string Slice(Node n, string text)
		{
			if (n == null || text == null || n.Location == null)
				return "";
			// Смещения LanD — в кодовых точках Unicode (ANTLR CodePointCharStream), а строка C# индексируется в UTF-16.
			// После символов вне BMP (эмодзи в описаниях схемы) индексы расходятся, и срез сдвигается: пересчитываем
			// кодовые точки в индексы UTF-16 по таблице начал кодовых точек (для текста без таких символов таблица тождественна).
			var starts = CodePointStarts(text);
			var start = CpToUtf16(starts, Math.Max(0, n.Location.Start.Offset));
			var endExclusive = Math.Min(text.Length, CpToUtf16(starts, n.Location.End.Offset + 1));
			if (endExclusive <= start) return "";
			return text.Substring(start, endExclusive - start);
		}

		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, int[]> CodePointStartsCache =
			new System.Runtime.CompilerServices.ConditionalWeakTable<string, int[]>();

		/// <summary>Индекс UTF-16 начала каждой кодовой точки текста; последний элемент — text.Length.</summary>
		private static int[] CodePointStarts(string text)
		{
			return CodePointStartsCache.GetValue(text, t =>
			{
				var list = new List<int>(t.Length + 1);
				for (int i = 0; i < t.Length; i++)
				{
					list.Add(i);
					if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
						i++;
				}
				list.Add(t.Length);
				return list.ToArray();
			});
		}

		private static int CpToUtf16(int[] starts, int codePoint)
		{
			if (codePoint <= 0) return 0;
			if (codePoint >= starts.Length) return starts[starts.Length - 1];
			return starts[codePoint];
		}

		private static List<Node> GetGqlTypeDefs(Node root)
		{
			var res = new List<Node>();
			if (root?.Children == null)
				return res;

			foreach (var child in root.Children)
			{
				if (child?.ToString() == "type_def")
					res.Add(child);
			}
			return res;
		}

		private static string GetGqlTypeName(Node typeDef)
		{
			if (typeDef?.Children == null)
				return "";

			foreach (var child in typeDef.Children)
			{
				var s = child?.ToString() ?? "";
				if (s.StartsWith("id: ", StringComparison.OrdinalIgnoreCase))
					return s.Replace("id: ", "", StringComparison.OrdinalIgnoreCase);
			}

			return typeDef.Children.Count > 1
				? (typeDef.Children[1]?.ToString() ?? "").Replace("id: ", "", StringComparison.OrdinalIgnoreCase)
				: "";
		}

		/// <summary>
		/// Какие поля GraphQL становятся якорями. По умолчанию — все поля типа (func_line и type_line):
		/// точка привязки = поле схемы, как в экспериментальном корпусе. Прежнее поведение
		/// (только поля с аргументами, func_line) включается переменной окружения LAND_GQL_FIELDS=callable.
		/// </summary>
		internal static bool GqlFieldAnchorsIncludePlain =
			!string.Equals(Environment.GetEnvironmentVariable("LAND_GQL_FIELDS"), "callable", StringComparison.OrdinalIgnoreCase);

		private static bool IsGqlFieldLine(Node n)
		{
			var t = n?.ToString();
			return t == "func_line" || (GqlFieldAnchorsIncludePlain && t == "type_line");
		}

		private static IEnumerable<Node> GetGqlFieldNodes(Node typeDef)
		{
			if (typeDef?.Children == null)
				yield break;

			foreach (var child in typeDef.Children)
			{
				if (IsGqlFieldLine(child))
					yield return child;
			}
		}

		/// <summary>
		/// Текст типа GraphQL как в исходнике (например, "[Deal!]!"). Раньше брался ToString()
		/// первого потомка узла type, что для составных типов давало литерал "type".
		/// </summary>
		private static string GetGqlTypeText(Node typeNode, string sourceText)
		{
			if (typeNode == null)
				return "";
			var s = Slice(typeNode, sourceText);
			if (!string.IsNullOrWhiteSpace(s))
				return s.Trim();
			// запасной путь без исходного текста
			var first = typeNode.Children?.FirstOrDefault(y => y != null && y.ToString() != "LSB: [");
			return (first?.ToString() ?? "").Replace("id: ", "", StringComparison.OrdinalIgnoreCase);
		}

		private static List<GqlMemberSketch> ExtractGqlMembers(Node typeDef, string text)
		{
			var res = new List<GqlMemberSketch>();
			if (typeDef?.Children == null)
				return res;

			foreach (var child in typeDef.Children)
			{
				var kind = child?.ToString();
				if (kind != "type_line" && kind != "func_line")
					continue;

				var nameRaw = child.Children != null && child.Children.Count > 0
					? (child.Children[0]?.ToString() ?? "").Replace("id: ", "", StringComparison.OrdinalIgnoreCase)
					: "";
				var signatureNorm = NormalizeGraphQlSnippet(Slice(child, text));
				if (string.IsNullOrWhiteSpace(signatureNorm))
					continue;

				res.Add(new GqlMemberSketch
				{
					NameRaw = nameRaw,
					SignatureNorm = signatureNorm,
				});
			}

			return res;
		}

		private static Dictionary<string, double> BuildNeighborBag(IReadOnlyList<TreeNode> anchors)
		{
			var bag = new Dictionary<string, double>(StringComparer.Ordinal);
			if (anchors == null)
				return bag;

			for (var i = 0; i < anchors.Count; i++)
			{
				var neighbor = anchors[i];
				if (neighbor == null)
					continue;

				var key = neighbor.MethodNameNorm ?? "";
				if (string.IsNullOrWhiteSpace(key))
					continue;

				AddBagWeight(bag, key, 1.0 / (1.0 + i));
			}

			return bag;
		}

		private static Dictionary<string, double> BuildFieldNeighborBag(IReadOnlyList<TreeNode> siblings, int index)
		{
			var bag = new Dictionary<string, double>(StringComparer.Ordinal);
			if (siblings == null || index < 0 || index >= siblings.Count)
				return bag;

			for (var i = 0; i < siblings.Count; i++)
			{
				if (i == index)
					continue;

				var neighbor = siblings[i];
				if (neighbor == null)
					continue;

				var key = neighbor.MethodNameNorm ?? "";
				if (string.IsNullOrWhiteSpace(key))
					continue;

				AddBagWeight(bag, key, 1.0 / (1.0 + Math.Abs(i - index)));
			}

			return bag;
		}

		private TreeNode CloneAnchor(TreeNode n)
		{
			if (n == null)
				return null;

			return new TreeNode
			{
				Id = n.Id,
				Name = n.Name,
				NodeType = n.NodeType,
				IsManual = n.IsManual,
				Filepath = n.Filepath,
				StartOffset = n.StartOffset,
				EndOffset = n.EndOffset,
				Language = n.Language,
				AnchorKind = n.AnchorKind,
				AnchorFamily = n.AnchorFamily,
				GqlTypeKind = n.GqlTypeKind,
				MethodNameNorm = n.MethodNameNorm,
				ParentNameNorm = n.ParentNameNorm,
				ParentNameRaw = n.ParentNameRaw,
				ReturnTypeNorm = n.ReturnTypeNorm,
				Args = n.Args?.Select(x => new Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
				OrdinalInParent = n.OrdinalInParent,
				NeighborBag = n.NeighborBag != null ? new Dictionary<string, double>(n.NeighborBag, StringComparer.Ordinal) : null,
				SystemKind = n.SystemKind,
				Comment = n.Comment,
			};
		}


		private string GetTypeGroupId(TreeNode node)
		{
			if (node == null)
				return null;
			var typeName = node.AnchorKind == AnchorKindGqlField ? (node.ParentNameRaw ?? "") : (node.MethodNameNorm ?? "");
			var typeDisplayName = node.AnchorKind == AnchorKindGqlField ? (node.ParentNameRaw ?? "") : (node.Name ?? "");
			var rawTypeName = node.AnchorKind == AnchorKindGqlField ? (node.ParentNameRaw ?? "") : ExtractRawTypeNameFromDisplay(node.Name, node.MethodNameNorm);
			return MakeGroupId("gqlType", node.Filepath ?? "", node.GqlTypeKind ?? "type", rawTypeName);
		}

		private string GetTypeGroupName(TreeNode node)
		{
			if (node == null)
				return null;
			if (node.AnchorKind == AnchorKindGqlField)
				return node.ParentNameRaw ?? node.ParentNameNorm ?? "";
			return ExtractRawTypeNameFromDisplay(node.Name, node.MethodNameNorm);
		}

		private string GetFieldGroupId(TreeNode node)
		{
			if (node == null || !string.Equals(node.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
				return null;
			return MakeGroupId("gqlField", node.Filepath ?? "", node.GqlTypeKind ?? "type", node.ParentNameRaw ?? "", node.Name ?? "");
		}

		private static string GetFieldGroupName(TreeNode node)
			=> node?.Name;

		private static string ExtractRawTypeNameFromDisplay(string displayName, string normalizedFallback)
		{
			if (string.IsNullOrWhiteSpace(displayName))
				return normalizedFallback ?? "";

			var firstSpace = displayName.IndexOf(' ');
			if (firstSpace > 0 && firstSpace + 1 < displayName.Length)
				return displayName[(firstSpace + 1)..];

			return displayName;
		}


		private sealed class GqlBindableCandidate
		{
			public Node Node { get; set; }
			public string ParentTypeName { get; set; }
			public string GqlTypeKind { get; set; }
			public int Specificity { get; set; }
		}

		private sealed class TsBindableCandidate
		{
			public Node Node { get; set; }
			public string ParentName { get; set; }
			public int Specificity { get; set; }
		}

		private static bool ContainsOffset(Node n, int offset)
		{
			if (n?.Location == null)
				return false;

			return n.Location.Start.Offset <= offset && offset <= n.Location.End.Offset;
		}

		private TreeNode FindEquivalentAnchorInMarkupUnsafe(TreeNode candidate)
		{
			if (candidate == null)
				return null;

			foreach (var existing in currentMarkup?.Anchors ?? new List<TreeNode>())
			{
				if (existing == null || !string.Equals(existing.NodeType, "anchor", StringComparison.OrdinalIgnoreCase))
					continue;

				if (!string.Equals(existing.Language, candidate.Language, StringComparison.OrdinalIgnoreCase))
					continue;
				if (!string.Equals(existing.AnchorKind, candidate.AnchorKind, StringComparison.OrdinalIgnoreCase))
					continue;
				if (!string.Equals(Path.GetFullPath(existing.Filepath ?? ""), Path.GetFullPath(candidate.Filepath ?? ""), StringComparison.OrdinalIgnoreCase))
					continue;

				if ((existing.StartOffset ?? -1) == (candidate.StartOffset ?? -2) && (existing.EndOffset ?? -1) == (candidate.EndOffset ?? -2))
					return existing;

				if (string.Equals(existing.MethodNameNorm ?? "", candidate.MethodNameNorm ?? "", StringComparison.Ordinal)
					&& string.Equals(existing.ParentNameNorm ?? "", candidate.ParentNameNorm ?? "", StringComparison.Ordinal))
					return existing;
			}

			return null;
		}

		private void RebuildRelationsUnsafe()
		{
			currentMarkup ??= new PersistedMarkup();
			currentMarkup.Anchors ??= new List<TreeNode>();

			var gqlAnchors = currentMarkup.Anchors
				.Where(x => x != null && string.Equals(x.Language, LangGql, StringComparison.OrdinalIgnoreCase))
				.Select(CloneAnchor)
				.ToList();
			var tsAnchors = currentMarkup.Anchors
				.Where(x => x != null && string.Equals(x.Language, LangTs, StringComparison.OrdinalIgnoreCase))
				.Select(CloneAnchor)
				.ToList();

			currentMarkup.Relations = BuildRelations(gqlAnchors, tsAnchors);
		}

		private TreeNode TryCreateManualAnchorAtOffset(string filePath, int offset, out string message)
		{
			message = null;
			filePath = Path.GetFullPath(filePath ?? "");
			if (!File.Exists(filePath))
			{
				message = $"Файл не найден: {filePath}";
				return null;
			}

			var ext = Path.GetExtension(filePath) ?? "";
			if (string.Equals(ext, ".graphql", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".gql", StringComparison.OrdinalIgnoreCase))
				return TryCreateBindableGqlAnchorAtOffset(filePath, offset, out message);
			if (string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase))
				return TryCreateBindableTsAnchorAtOffset(filePath, offset, out message);

			message = "Ручное добавление точки поддерживается только для GraphQL и TypeScript (*.ts) файлов.";
			return null;
		}

		private TreeNode TryCreateBindableGqlAnchorAtOffset(string filePath, int offset, out string message)
		{
			message = null;

			var txt = File.ReadAllText(filePath);
			var root = graphqlParser.Parse(txt).Item1;
			if (root == null)
			{
				message = "Не удалось распарсить файл GraphQL.";
				return null;
			}

			var candidates = new List<GqlBindableCandidate>();
			foreach (var typeDef in GetGqlTypeDefs(root))
			{
				if (!ContainsOffset(typeDef, offset))
					continue;

				var gqlTypeKind = InferGqlTypeKind(typeDef);
				var typeName = GetGqlTypeName(typeDef);

				candidates.Add(new GqlBindableCandidate
				{
					Node = typeDef,
					ParentTypeName = typeName,
					GqlTypeKind = gqlTypeKind,
					Specificity = 1,
				});

				foreach (var fieldNode in GetGqlFieldNodes(typeDef))
				{
					if (!ContainsOffset(fieldNode, offset))
						continue;

					candidates.Add(new GqlBindableCandidate
					{
						Node = fieldNode,
						ParentTypeName = typeName,
						GqlTypeKind = gqlTypeKind,
						Specificity = 2,
					});
				}
			}

			var best = candidates
				.Where(x => x?.Node?.Location != null)
				.OrderBy(x => (x.Node.Location.End.Offset - x.Node.Location.Start.Offset))
				.ThenByDescending(x => x.Specificity)
				.FirstOrDefault();

			if (best == null)
			{
				message = "В позиции курсора не найдена bindable-сущность GraphQL (поддерживаются type_def и поля типа).";
				return null;
			}

			if (IsGqlFieldLine(best.Node))
				return GetTreeNodeFromGqlFieldNode(best.Node, filePath, best.ParentTypeName, best.GqlTypeKind, txt);

			return GetTreeNodeFromGqlTypeNode(best.Node, filePath, best.GqlTypeKind, txt);
		}

		private TreeNode TryCreateBindableTsAnchorAtOffset(string filePath, int offset, out string message)
		{
			message = null;

			var root = typescriptParser.Parse(File.ReadAllText(filePath)).Item1;
			if (root == null)
			{
				message = "Не удалось распарсить файл TypeScript.";
				return null;
			}

			var candidates = new List<TsBindableCandidate>();
			CollectTsBindableCandidatesAtOffset(root, offset, candidates);

			var best = candidates
				.Where(x => x?.Node?.Location != null)
				.OrderBy(x => (x.Node.Location.End.Offset - x.Node.Location.Start.Offset))
				.ThenByDescending(x => x.Specificity)
				.FirstOrDefault();

			if (best == null)
			{
				message = "В позиции курсора не найдена bindable-сущность TypeScript (поддерживаются func, sub_field_func_impl и sub_field_any).";
				return null;
			}

			return GetTreeNodeFromTsNode(best.Node, filePath, best.ParentName);
		}

		private void LoadRebindingForestsFromAnchors(IEnumerable<TreeNode> anchors)
		{
			currentContextsByProfileKey.Clear();
			currentNodesByProfileKey.Clear();
			currentTreesByProfileKey.Clear();

			var allAnchors = (anchors ?? Enumerable.Empty<TreeNode>())
				.Where(CanParticipateInRebinding)
				.Select(CloneAnchor)
				.ToList();

			foreach (var grp in allAnchors.GroupBy(GetProfileKey, StringComparer.OrdinalIgnoreCase))
			{
				var nodes = grp.Select(CloneAnchor).ToList();
				var contexts = nodes.Select(BuildAnchorContext).Where(x => x != null).ToList();
				currentNodesByProfileKey[grp.Key] = nodes;
				currentContextsByProfileKey[grp.Key] = contexts;
				if (contexts.Count == 0)
					continue;

				var weights = GetWeightsForProfileKey(grp.Key);
				var tree = new VPTree<AnchorContext>(
					contexts,
					(a, b) => AnchorContextDistance(a, b, weights),
					42,
					Tracing.Tracer);

				currentTreesByProfileKey[grp.Key] = tree;
				Debug($"[vptree] built profile={grp.Key}, size={contexts.Count}, depth={tree.BuildDepth}");
			}
		}

		private TreeNode RebindAnchorAgainstCurrentForests(TreeNode oldNode)
		{
			return RebindAnchorAgainstCurrentForests(oldNode, out _);
		}

		/// <summary>
		/// Перепривязка якоря к текущему лесу профиля с правилом приёма (tau, margin).
		/// Возвращает новый узел только при статусе Accepted; при Ambiguous/Lost — null,
		/// подробности в <paramref name="decision"/>.
		/// </summary>
		private TreeNode RebindAnchorAgainstCurrentForests(TreeNode oldNode, out RebindDecision decision)
		{
			decision = new RebindDecision { Status = RebindStatus.Lost, Reason = "no index" };
			if (oldNode == null)
				return null;

			EnsureAnchorFamily(oldNode);
			var profileKey = GetProfileKey(oldNode);
			if (string.IsNullOrWhiteSpace(profileKey))
				return null;

			if (!currentTreesByProfileKey.TryGetValue(profileKey, out var tree)
				|| !currentContextsByProfileKey.TryGetValue(profileKey, out var contexts)
				|| !currentNodesByProfileKey.TryGetValue(profileKey, out var nodes)
				|| contexts.Count == 0)
				return null;

			var query = BuildAnchorContext(oldNode);
			if (query == null)
				return null;

			var settings = RebindSettings.Default;
			var cands = tree.KNearest(query, Math.Max(2, settings.K));
			decision = DecideFor(cands, settings.Tau, settings.MinRelativeMargin);
			Debug($"[rebind] '{oldNode.Name}' profile={profileKey} status={decision.Status} {decision.Reason}");
			if (decision.Status != RebindStatus.Accepted)
				return null;

			var rebound = CloneAnchor(nodes[decision.BestIndex]);
			rebound.Id = oldNode.Id;
			rebound.IsManual = oldNode.IsManual;
			rebound.Comment = oldNode.Comment;
			rebound.AnchorFamily = EnsureAnchorFamily(rebound);
			return rebound;
		}

		private TreeNode GetTreeNodeFromTsNode(Node n, string filepath, string parentName)
		{
			filepath = Path.GetFullPath(filepath);
			var name = n.Children[0].ToString().Replace("ID: ", "").Replace("id: ", "");
			var args = n.Children.Count > 1 && n.Children[1]?.Children != null
				? n.Children[1].Children.Select(x => new Tuple<string, string>(
					x.ToString().Replace("arg", ""),
					""))
					.Where(x => x.Item1 != "")
					.ToList()
				: new List<Tuple<string, string>>();

			var start = n.Location.Start.Offset;
			var end = n.Location.End.Offset;

			return new TreeNode
			{
				Id = NewAnchorId("ts"),
				Name = name,
				NodeType = "anchor",
				Language = LangTs,
				AnchorKind = AnchorKindTs,
				AnchorFamily = AnchorFamilyCallable,
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

		private TreeNode GetTreeNodeFromGqlFieldNode(Node n, string filepath, string parentTypeName, string gqlTypeKind, string sourceText)
		{
			filepath = Path.GetFullPath(filepath);
			var name = n.Children.First().ToString().Replace("id: ", "", StringComparison.OrdinalIgnoreCase);

			// func_line = id '(' func_arg* ')' ':' type ...;  type_line = id ':' type default_value? ...
			// func_arg = type_line → [id, type, default_value?]
			var args = n.Children
				.Where(x => x != null && x.Type == "func_arg")
				.Select(x =>
				{
					var line = x.Children?.FirstOrDefault();
					var argName = (line?.Children?.FirstOrDefault()?.ToString() ?? "").Replace("id: ", "", StringComparison.OrdinalIgnoreCase);
					var typeNode = line?.Children?.FirstOrDefault(c => c != null && c.ToString() == "type");
					return new Tuple<string, string>(GetGqlTypeText(typeNode, sourceText), argName);
				})
				.ToList();

			var retNode = n.Children.LastOrDefault(c => c != null && c.ToString() == "type");
			var returnType = GetGqlTypeText(retNode, sourceText);
			var start = n.Location.Start.Offset;
			var end = n.Location.End.Offset;

			return new TreeNode
			{
				Id = NewAnchorId("gql"),
				Name = name,
				NodeType = "anchor",
				Language = LangGql,
				AnchorKind = AnchorKindGqlField,
				AnchorFamily = AnchorFamilyCallable,
				GqlTypeKind = gqlTypeKind,
				Filepath = filepath,
				StartOffset = start,
				EndOffset = end,
				MethodNameNorm = NormalizeName(name),
				ParentNameNorm = NormalizeName(parentTypeName),
				ParentNameRaw = parentTypeName,
				ReturnTypeNorm = NormalizeReturnTypes(new List<string> { returnType }),
				Args = NormalizeArgs(args),
			};
		}

		private TreeNode GetTreeNodeFromGqlTypeNode(Node n, string filepath, string gqlTypeKind, string sourceText)
		{
			filepath = Path.GetFullPath(filepath);
			var typeName = GetGqlTypeName(n);
			var members = ExtractGqlMembers(n, sourceText);
			var signatureParts = members
				.Select(x => x.SignatureNorm)
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.OrderBy(x => x, StringComparer.Ordinal)
				.ToList();
			var bag = new Dictionary<string, double>(StringComparer.Ordinal);
			foreach (var member in members)
			{
				AddBagWeight(bag, "name:" + NormalizeName(member.NameRaw), 1.0);
				AddBagWeight(bag, "sig:" + member.SignatureNorm, 1.0);
			}

			return new TreeNode
			{
				Id = NewAnchorId("gql"),
				Name = GetDisplayNameForTypeAnchor(gqlTypeKind, typeName),
				NodeType = "anchor",
				Language = LangGql,
				AnchorKind = InferGqlAnchorKind(gqlTypeKind),
				AnchorFamily = AnchorFamilyRecord,
				GqlTypeKind = gqlTypeKind,
				Filepath = filepath,
				StartOffset = n.Location.Start.Offset,
				EndOffset = n.Location.End.Offset,
				MethodNameNorm = NormalizeName(typeName),
				ParentNameNorm = NormalizeName(gqlTypeKind),
				ParentNameRaw = gqlTypeKind,
				ReturnTypeNorm = string.Join(" | ", signatureParts),
				Args = new List<Arg>(),
				NeighborBag = bag,
			};
		}

		public static List<Arg> NormalizeArgs(IEnumerable<Tuple<string, string>> args)
		{
			var res = new List<Arg>();
			foreach (var t in args ?? Enumerable.Empty<Tuple<string, string>>())
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
			var withSpaces = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
			withSpaces = withSpaces.Replace('_', ' ');
			var tokens = withSpaces.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			return string.Join(" ", tokens);
		}

		public static string NormalizeTypeName(string t)
		{
			if (string.IsNullOrWhiteSpace(t)) return "";
			var s = t.Trim();
			s = Regex.Replace(s, @"\s+", " ");
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

		private int ParseGqlFiles(IEnumerable<string> gqlFiles, List<TreeNode> gqlAnchors)
		{
			var count = 0;
			foreach (var gqlFile1 in gqlFiles)
			{
				var gqlFile = Path.GetFullPath(gqlFile1);
				var txt = File.ReadAllText(gqlFile);
				Node root;
				using (Tracing.Tracer.BuildSpan("ParseGql").StartActive())
					root = graphqlParser.Parse(txt).Item1;

				foreach (var typeDef in GetGqlTypeDefs(root))
				{
					var gqlTypeKind = InferGqlTypeKind(typeDef);
					var typeName = GetGqlTypeName(typeDef);

					var typeAnchor = GetTreeNodeFromGqlTypeNode(typeDef, gqlFile, gqlTypeKind, txt);
					gqlAnchors.Add(typeAnchor);
					nodesById[typeAnchor.Id] = typeAnchor;
					count++;

					var fieldAnchors = GetGqlFieldNodes(typeDef)
						.Select(x => GetTreeNodeFromGqlFieldNode(x, gqlFile, typeName, gqlTypeKind, txt))
						.ToList();

					for (var i = 0; i < fieldAnchors.Count; i++)
					{
						var fieldAnchor = fieldAnchors[i];
						fieldAnchor.OrdinalInParent = i;
						gqlAnchors.Add(fieldAnchor);
						nodesById[fieldAnchor.Id] = fieldAnchor;
						count++;
					}
				}
			}

			// Мешки соседей — единой реализацией VPTreeLib по всем полям проекта
			// (группировка по файлу и родителю, IDF по всему набору).
			AssignNeighborBags(gqlAnchors
				.Where(x => string.Equals(x.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
				.ToList());
			return count;
		}


		private static string CleanTsId(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			return s.Replace("ID: ", "").Replace("id: ", "").Trim();
		}

		private static string ExtractTsNodeId(Node node)
		{
			if (node == null)
				return "";

			if (node.Children != null)
			{
				foreach (var child in node.Children)
				{
					var raw = CleanTsId(child?.ToString());
					if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, node.ToString(), StringComparison.OrdinalIgnoreCase))
						return raw;
				}
			}

			return CleanTsId(node.ToString());
		}

		private static string InferResolverParentFromContainerName(string rawName)
		{
			var s = CleanTsId(rawName);
			if (string.IsNullOrWhiteSpace(s))
				return "";

			s = Regex.Replace(s, @"(?i)(Resolvers?|Resolver|ResolverMap|RootResolvers?)$", "");
			s = s.Trim('_', '-', ' ');
			if (string.IsNullOrWhiteSpace(s))
				return CleanTsId(rawName);

			return s;
		}

		private static bool IsTsCallableNode(string nodeName)
			=> nodeName == "func" || nodeName == "sub_field_func_impl" || nodeName == "sub_field_any";

		private static void AddTsNodeMatch(Dictionary<string, List<Node>> res, string parentName, Node node)
		{
			if (node == null)
				return;

			parentName = string.IsNullOrWhiteSpace(parentName) ? "(root)" : parentName;
			if (!res.TryGetValue(parentName, out var existing))
				res[parentName] = existing = new List<Node>();
			existing.Add(node);
		}

		private void CollectTsNodes(Node root, Dictionary<string, List<Node>> res, string resolverParent = null, string lexicalContainer = null)
		{
			if (root == null)
				return;

			var nodeName = root.ToString();
			var nextResolverParent = resolverParent;
			var nextLexicalContainer = lexicalContainer;

			if (nodeName == "field_block")
			{
				var blockName = ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(blockName))
				{
					nextResolverParent = blockName;
					nextLexicalContainer = blockName;
				}
			}
			else if (nodeName == "class")
			{
				var className = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(className))
				{
					nextResolverParent = className;
					nextLexicalContainer = className;
				}
			}
			else if (nodeName == "struct" || nodeName == "lamda_struct")
			{
				var containerName = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(containerName))
					nextLexicalContainer = containerName;
			}
			else if (nodeName == "namespace")
			{
				var namespaceName = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(namespaceName) && string.IsNullOrWhiteSpace(nextLexicalContainer))
					nextLexicalContainer = namespaceName;
			}

			if (IsTsCallableNode(nodeName))
			{
				var effectiveParent = nextResolverParent;
				if (string.IsNullOrWhiteSpace(effectiveParent))
					effectiveParent = InferResolverParentFromContainerName(nextLexicalContainer);
				AddTsNodeMatch(res, effectiveParent, root);
				return;
			}

			foreach (var child in root.Children ?? new List<Node>())
				CollectTsNodes(child, res, nextResolverParent, nextLexicalContainer);
		}

		private void CollectTsBindableCandidatesAtOffset(Node root, int offset, List<TsBindableCandidate> res, string resolverParent = null, string lexicalContainer = null)
		{
			if (root == null || res == null || !ContainsOffset(root, offset))
				return;

			var nodeName = root.ToString();
			var nextResolverParent = resolverParent;
			var nextLexicalContainer = lexicalContainer;

			if (nodeName == "field_block")
			{
				var blockName = ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(blockName))
				{
					nextResolverParent = blockName;
					nextLexicalContainer = blockName;
				}
			}
			else if (nodeName == "class")
			{
				var className = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(className))
				{
					nextResolverParent = className;
					nextLexicalContainer = className;
				}
			}
			else if (nodeName == "struct" || nodeName == "lamda_struct")
			{
				var containerName = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(containerName))
					nextLexicalContainer = containerName;
			}
			else if (nodeName == "namespace")
			{
				var namespaceName = root.Children != null && root.Children.Count > 1
					? ExtractTsNodeId(root.Children[1])
					: ExtractTsNodeId(root);
				if (!string.IsNullOrWhiteSpace(namespaceName) && string.IsNullOrWhiteSpace(nextLexicalContainer))
					nextLexicalContainer = namespaceName;
			}

			if (IsTsCallableNode(nodeName))
			{
				var effectiveParent = nextResolverParent;
				if (string.IsNullOrWhiteSpace(effectiveParent))
					effectiveParent = InferResolverParentFromContainerName(nextLexicalContainer);

				res.Add(new TsBindableCandidate
				{
					Node = root,
					ParentName = effectiveParent,
					Specificity = 1,
				});
				return;
			}

			foreach (var child in root.Children ?? new List<Node>())
				CollectTsBindableCandidatesAtOffset(child, offset, res, nextResolverParent, nextLexicalContainer);
		}

		private Dictionary<string, List<Node>> GetTsNodes(Node root)
		{
			var res = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
			CollectTsNodes(root, res);
			return res;
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
			if (tsParentNorm.Contains(gqlParentNorm) || gqlParentNorm.Contains(tsParentNorm)) return true;
			return TokenOverlap(gqlParentNorm, tsParentNorm) >= 0.8;
		}

		private double ScoreTsToGql(TreeNode gqlAnchor, TreeNode ts)
		{
			if (gqlAnchor == null || ts == null)
				return double.NegativeInfinity;
			if (!string.Equals(gqlAnchor.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
				return double.NegativeInfinity;
			if ((gqlAnchor.MethodNameNorm ?? "") != (ts.MethodNameNorm ?? ""))
				return double.NegativeInfinity;

			var score = 0.0;
			var gqlParent = gqlAnchor.ParentNameNorm ?? "";
			var tsParent = ts.ParentNameNorm ?? "";
			if (tsParent == gqlParent) score += 10.0;
			else if (ParentMatchLoose(gqlParent, tsParent)) score += 5.0;
			else score += TokenOverlap(gqlParent, tsParent);

			var fp = (ts.Filepath ?? "").Replace('\\', '/').ToLowerInvariant();
			if (fp.Contains("resolver")) score += 0.2;
			return score;
		}

		private List<PersistedRelation> BuildRelations(List<TreeNode> gqlAnchors, List<TreeNode> tsAnchors)
		{
			var relations = new Dictionary<string, PersistedRelation>(StringComparer.OrdinalIgnoreCase);
			var gqlFieldsByMethod = gqlAnchors
				.Where(x => x != null && string.Equals(x.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
				.GroupBy(x => x.MethodNameNorm ?? "")
				.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

			foreach (var ts in tsAnchors.Where(x => x != null))
			{
				if (!gqlFieldsByMethod.TryGetValue(ts.MethodNameNorm ?? "", out var gqlCandidates) || gqlCandidates.Count == 0)
					continue;

				TreeNode best = null;
				double bestScore = double.NegativeInfinity;
				foreach (var gql in gqlCandidates)
				{
					var score = ScoreTsToGql(gql, ts);
					if (score > bestScore)
					{
						bestScore = score;
						best = gql;
					}
				}

				if (best == null || bestScore < 1.0)
					continue;

				if (!relations.TryGetValue(best.Id, out var rel))
				{
					relations[best.Id] = rel = new PersistedRelation
					{
						SourceAnchorId = best.Id,
						TargetAnchorIds = new List<string>(),
					};
				}

				if (!rel.TargetAnchorIds.Contains(ts.Id, StringComparer.OrdinalIgnoreCase))
					rel.TargetAnchorIds.Add(ts.Id);
			}

			return relations.Values.ToList();
		}

		private List<TreeNode> BuildRootsFromMarkup(PersistedMarkup markup)
		{
			var result = new List<TreeNode>();
			if (markup?.Anchors == null)
				return result;

			var allAnchors = markup.Anchors.Where(x => x != null && string.Equals(x.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)).ToList();
			foreach (var anchor in allAnchors)
				EnsureAnchorFamily(anchor);

			// Индексы для дополнения групп shadow-узлами из Memberships.
			var anchorsById = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
			foreach (var a in allAnchors)
				if (!string.IsNullOrWhiteSpace(a?.Id))
					anchorsById[a.Id] = a;

			// Виртуальные якоря из _Lost-bucket-а: добавляем в anchorsById, чтобы
			// shadow-теневые узлы в обычных user/auto-группах могли отрисоваться
			// (с маркером SystemKind="lostAnchor"). Это даёт пользователю видимость
			// "точка была здесь, но пропала" прямо в его рабочих папках.
			foreach (var lost in markup.LostAnchors ?? new List<LostAnchor>())
			{
				if (lost == null || string.IsNullOrWhiteSpace(lost.AnchorId))
					continue;
				if (anchorsById.ContainsKey(lost.AnchorId))
					continue;

				anchorsById[lost.AnchorId] = new TreeNode
				{
					Id = lost.AnchorId,
					Name = lost.Name,
					NodeType = "anchor",
					Language = lost.Language,
					AnchorKind = lost.AnchorKind,
					AnchorFamily = lost.AnchorFamily,
					ParentNameRaw = lost.ParentNameRaw,
					MethodNameNorm = lost.MethodNameNorm,
					GqlTypeKind = lost.GqlTypeKind,
					Filepath = lost.Filepath,
					StartOffset = null,
					EndOffset = null,
					SystemKind = "lostAnchor",
					Comment = lost.Comment,
				};
			}

			var membershipsByGroup = new Dictionary<string, List<UserGroupMembership>>(StringComparer.OrdinalIgnoreCase);
			foreach (var m in markup.Memberships ?? new List<UserGroupMembership>())
			{
				if (m == null || string.IsNullOrWhiteSpace(m.GroupId) || string.IsNullOrWhiteSpace(m.AnchorId))
					continue;
				if (!membershipsByGroup.TryGetValue(m.GroupId, out var bucket))
					membershipsByGroup[m.GroupId] = bucket = new List<UserGroupMembership>();
				bucket.Add(m);
			}

			// _Lost-bucket рендерится первым (сверху), если есть утерянные якоря
			// со связями. Это привлекает внимание пользователя к необходимости
			// recovery, и не теряется среди user-групп.
			var lostRoot = BuildLostBucketRoot(markup);
			if (lostRoot != null)
				result.Add(lostRoot);

			// User-группы рендерятся перед auto-группами, чтобы пользовательская
			// организация была сразу видна сверху. Якоря в user-группах рендерятся
			// как shadow-узлы (Id = memberOf:groupId::anchorId, RealAnchorId = anchorId)
			// — это нужно для корректной работы treeView.reveal() при multi-membership.
			var userRoots = BuildUserGroupRoots(markup, anchorsById, membershipsByGroup);
			if (userRoots.Count > 0)
				result.AddRange(userRoots);

			var gqlAnchors = allAnchors.Where(x => string.Equals(x.Language, LangGql, StringComparison.OrdinalIgnoreCase)).ToList();
			var tsAnchors = allAnchors.Where(x => string.Equals(x.Language, LangTs, StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);

			var relationMap = new Dictionary<string, List<TreeNode>>(StringComparer.OrdinalIgnoreCase);
			var usedTsIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var rel in markup.Relations ?? new List<PersistedRelation>())
			{
				if (string.IsNullOrWhiteSpace(rel?.SourceAnchorId))
					continue;

				var targets = new List<TreeNode>();
				foreach (var targetId in rel.TargetAnchorIds ?? new List<string>())
				{
					if (tsAnchors.TryGetValue(targetId, out var ts))
					{
						targets.Add(CloneAnchor(ts));
						usedTsIds.Add(targetId);
					}
				}

				if (targets.Count > 0)
					relationMap[rel.SourceAnchorId] = targets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}

			var gqlGroups = gqlAnchors
				.GroupBy(x => new
				{
					Id = MakeGroupId("gqlType", x.Filepath ?? "", x.GqlTypeKind ?? "type", x.AnchorKind == AnchorKindGqlField ? (x.ParentNameRaw ?? "") : ExtractRawTypeNameFromDisplay(x.Name, x.MethodNameNorm)),
					Name = x.AnchorKind == AnchorKindGqlField ? (x.ParentNameRaw ?? x.ParentNameNorm ?? "") : ExtractRawTypeNameFromDisplay(x.Name, x.MethodNameNorm),
					Filepath = x.Filepath,
					TypeKind = x.GqlTypeKind ?? "type",
				});

			foreach (var gqlGroup in gqlGroups.OrderBy(x => x.Key.Name, StringComparer.OrdinalIgnoreCase))
			{
				var groupNode = new TreeNode
				{
					Id = gqlGroup.Key.Id,
					Name = gqlGroup.Key.Name,
					NodeType = "group",
					Children = new List<TreeNode>(),
				};

				var typeAnchors = gqlGroup.Where(x => IsGqlTypeContainerKind(x.AnchorKind)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
				foreach (var typeAnchor in typeAnchors)
					groupNode.Children.Add(CloneAnchor(typeAnchor));

				var fieldAnchors = gqlGroup.Where(x => string.Equals(x.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
					.OrderBy(x => x.OrdinalInParent ?? int.MaxValue)
					.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
					.ToList();

				foreach (var fieldAnchor in fieldAnchors)
				{
					var fieldGroup = new TreeNode
					{
						Id = GetFieldGroupId(fieldAnchor),
						Name = fieldAnchor.Name,
						NodeType = "group",
						Children = new List<TreeNode> { CloneAnchor(fieldAnchor) },
					};

					if (relationMap.TryGetValue(fieldAnchor.Id, out var tsRelated))
						fieldGroup.Children.AddRange(tsRelated.Select(CloneAnchor));

					// Дополнение пользовательскими memberships в этой gqlField-группе.
					AppendMembershipShadows(fieldGroup, anchorsById, membershipsByGroup);

					groupNode.Children.Add(fieldGroup);
				}

				// Дополнение пользовательскими memberships на уровне gqlType-группы.
				AppendMembershipShadows(groupNode, anchorsById, membershipsByGroup);

				result.Add(groupNode);
			}

			var orphanTs = allAnchors
				.Where(x => string.Equals(x.Language, LangTs, StringComparison.OrdinalIgnoreCase) && !usedTsIds.Contains(x.Id))
				.OrderBy(x => x.ParentNameRaw ?? "", StringComparer.OrdinalIgnoreCase)
				.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (orphanTs.Count > 0)
			{
				var orphanRoot = new TreeNode
				{
					Id = MakeGroupId("tsOrphans", currentFolderPath ?? ""),
					Name = "TypeScript (unmatched)",
					NodeType = "group",
					Children = new List<TreeNode>(),
				};

				foreach (var grp in orphanTs.GroupBy(x => x.ParentNameRaw ?? "(root)").OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
				{
					var classNode = new TreeNode
					{
						Id = MakeGroupId("tsClass", grp.Key, currentFolderPath ?? ""),
						Name = grp.Key,
						NodeType = "group",
						Children = grp.Select(CloneAnchor).ToList(),
					};
					AppendMembershipShadows(classNode, anchorsById, membershipsByGroup);
					orphanRoot.Children.Add(classNode);
				}

				AppendMembershipShadows(orphanRoot, anchorsById, membershipsByGroup);
				result.Add(orphanRoot);
			}

			return result;
		}

		/// <summary>
		/// Добавляет в Children группы shadow-узлы для всех memberships, у которых GroupId == groupNode.Id.
		/// Дедуп: если канонический anchor с этим Id уже присутствует среди children — пропускаем.
		/// </summary>
		private void AppendMembershipShadows(
			TreeNode groupNode,
			Dictionary<string, TreeNode> anchorsById,
			Dictionary<string, List<UserGroupMembership>> membershipsByGroup)
		{
			if (groupNode == null || string.IsNullOrWhiteSpace(groupNode.Id))
				return;
			if (!membershipsByGroup.TryGetValue(groupNode.Id, out var members) || members.Count == 0)
				return;

			groupNode.Children ??= new List<TreeNode>();

			// Анкоры-каноны (без RealAnchorId), которые уже в группе — для дедупа.
			var existingCanonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in groupNode.Children)
			{
				if (c == null) continue;
				if (string.Equals(c.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
					&& string.IsNullOrEmpty(c.RealAnchorId)
					&& !string.IsNullOrWhiteSpace(c.Id))
				{
					existingCanonicalIds.Add(c.Id);
				}
			}

			var orderedMembers = members
				.OrderBy(m => m.Order ?? int.MaxValue)
				.ThenBy(m => anchorsById.TryGetValue(m.AnchorId, out var a) ? (a.Name ?? "") : "", StringComparer.OrdinalIgnoreCase)
				.ToList();

			foreach (var m in orderedMembers)
			{
				if (existingCanonicalIds.Contains(m.AnchorId))
					continue; // уже есть канонически
				if (!anchorsById.TryGetValue(m.AnchorId, out var canonical))
					continue; // висячий — пропускаем

				var shadow = CloneAnchor(canonical);
				shadow.Id = MakeShadowAnchorId(groupNode.Id, canonical.Id);
				shadow.RealAnchorId = canonical.Id;
				groupNode.Children.Add(shadow);
			}
		}

		private static string MakeShadowAnchorId(string groupId, string anchorId)
			=> $"memberOf:{groupId}::{anchorId}";

		/// <summary>
		/// Построить TreeNode-узлы для всех пользовательских групп, со shadow-якорями
		/// для каждого membership. Группы возвращаются в порядке (Order asc, Name asc).
		/// Якоря внутри группы — в порядке (Order asc, Name asc).
		/// Висячие memberships (anchorId не существует в Anchors) пропускаются молча
		/// — это страховка; основная чистка делается в ListAnchors после сшивки и в
		/// DeleteAnchor. Группы без members всё равно показываются (пустыми).
		/// </summary>
		private List<TreeNode> BuildUserGroupRoots(
			PersistedMarkup markup,
			Dictionary<string, TreeNode> anchorsById,
			Dictionary<string, List<UserGroupMembership>> membershipsByGroup)
		{
			var result = new List<TreeNode>();
			var groups = markup?.UserGroups;
			if (groups == null || groups.Count == 0)
				return result;

			var orderedGroups = groups
				.Where(g => g != null && !string.IsNullOrWhiteSpace(g.Id))
				.OrderBy(g => g.Order ?? int.MaxValue)
				.ThenBy(g => g.Name ?? "", StringComparer.OrdinalIgnoreCase)
				.ToList();

			foreach (var g in orderedGroups)
			{
				var groupNode = new TreeNode
				{
					Id = g.Id,
					Name = string.IsNullOrWhiteSpace(g.Name) ? "(unnamed)" : g.Name,
					NodeType = "group",
					GroupKind = "user",
					Children = new List<TreeNode>(),
					Comment = g.Comment,
				};

				AppendMembershipShadows(groupNode, anchorsById, membershipsByGroup);
				result.Add(groupNode);
			}

			return result;
		}

		/// <summary>
		/// Системная корневая группа "_Lost (N)" со всеми утерянными якорями
		/// (которые при сшивке Id потеряли связь с текущим состоянием кода,
		/// но имели memberships или links — поэтому полностью отбросить нельзя).
		/// Возвращает null, если LostAnchors пуст.
		/// </summary>
		private TreeNode BuildLostBucketRoot(PersistedMarkup markup)
		{
			var lost = (markup?.LostAnchors ?? new List<LostAnchor>())
				.Where(l => l != null && !string.IsNullOrWhiteSpace(l.AnchorId))
				.OrderBy(l => l.Name ?? "", StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (lost.Count == 0)
				return null;

			var root = new TreeNode
			{
				Id = "system:lost",
				Name = $"_Lost ({lost.Count})",
				NodeType = "group",
				// GroupKind не ставим — _Lost не является "пользовательской" группой;
				// узнаётся по SystemKind="lost" (sys="lost" на клиенте).
				SystemKind = "lost",
				Children = new List<TreeNode>(),
			};

			foreach (var l in lost)
			{
				var displayName = string.IsNullOrWhiteSpace(l.Name) ? "(unnamed)" : l.Name;
				if (l.WasInGroupNames != null && l.WasInGroupNames.Count > 0)
					displayName = $"{displayName} (was in: {string.Join(", ", l.WasInGroupNames)})";

				root.Children.Add(new TreeNode
				{
					Id = l.AnchorId,
					Name = displayName,
					NodeType = "anchor",
					Language = l.Language,
					AnchorKind = l.AnchorKind,
					AnchorFamily = l.AnchorFamily,
					ParentNameRaw = l.ParentNameRaw,
					MethodNameNorm = l.MethodNameNorm,
					GqlTypeKind = l.GqlTypeKind,
					Filepath = l.Filepath,
					StartOffset = null,
					EndOffset = null,
					SystemKind = "lostAnchor",
					Comment = l.Comment,
				});
			}

			return root;
		}

		private void RebuildMarkupRootsUnsafe()
		{
			if (currentMarkup == null)
				currentMarkup = new PersistedMarkup();
			// Сначала синтез auto-pair (на основе текущих Relations),
			// потом индексы (включая paired set), потом сборка дерева.
			SynthesizeAutoPairLinksUnsafe();
			RebuildLinkCountIndexUnsafe();
			currentMarkup.Roots = BuildRootsFromMarkup(currentMarkup);
		}

		private void ReloadNodesByIdUnsafe()
		{
			nodesById.Clear();
			foreach (var anchor in currentMarkup?.Anchors ?? new List<TreeNode>())
			{
				if (anchor?.NodeType == "anchor" && !string.IsNullOrWhiteSpace(anchor.Id))
				{
					EnsureAnchorFamily(anchor);
					nodesById[anchor.Id] = anchor;
				}
			}
		}

		private void ReplaceAnchorInMarkupUnsafe(TreeNode updated)
		{
			if (updated == null)
				return;
			currentMarkup ??= new PersistedMarkup();
			currentMarkup.Anchors ??= new List<TreeNode>();

			updated.AnchorFamily = EnsureAnchorFamily(updated);
			for (var i = 0; i < currentMarkup.Anchors.Count; i++)
			{
				if (string.Equals(currentMarkup.Anchors[i]?.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
				{
					currentMarkup.Anchors[i] = CloneAnchor(updated);
					return;
				}
			}

			currentMarkup.Anchors.Add(CloneAnchor(updated));
		}

		private void BuildSemanticMarkupFromDisk(out List<TreeNode> gqlAnchors, out List<TreeNode> tsAnchors)
		{
			gqlAnchors = new List<TreeNode>();
			tsAnchors = new List<TreeNode>();

			nodesById.Clear();
			Tracing.Init();

			// Детерминизм: порядок обхода файловой системы не должен влиять на результат,
			// поэтому списки файлов сортируются. Схемы: *.graphql, *.gql, *.graphqls.
			var gqlFiles = new[] { "graphql", "gql", "graphqls" }
				.SelectMany(ext => GetAllFiles(currentFolderPath, ext))
				.Where(x => x.EndsWith(".graphql", StringComparison.OrdinalIgnoreCase)
					|| x.EndsWith(".gql", StringComparison.OrdinalIgnoreCase)
					|| x.EndsWith(".graphqls", StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(x => x, StringComparer.Ordinal)
				.ToList();
			using (var scope = Tracing.Tracer.BuildSpan("ProcessGqlFiles").StartActive())
				ParseGqlFiles(gqlFiles, gqlAnchors);

			var tsFiles = GetAllFiles(currentFolderPath, "ts")
				.Where(x => x.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
				.OrderBy(x => x, StringComparer.Ordinal)
				.ToList();
			using (var scope = Tracing.Tracer.BuildSpan("ProcessTsFiles").StartActive())
			{
				foreach (var tsFile in tsFiles)
				{
					var txt = File.ReadAllText(tsFile);
					var root = typescriptParser.Parse(txt).Item1;
					var nodesPerClass = GetTsNodes(root);
					foreach (var nodes in nodesPerClass)
					{
						foreach (var node in nodes.Value)
						{
							var treeNode = GetTreeNodeFromTsNode(node, tsFile, nodes.Key);
							nodesById[treeNode.Id] = treeNode;
							tsAnchors.Add(treeNode);
						}
					}
				}

				// Раньше у TS-якорей NeighborBag не строился вовсе (аудит 2026-08, §3.4).
				AssignNeighborBags(tsAnchors);
			}
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
					return !norm.Contains("/vendor/") && !norm.Contains("/node_modules/") && !norm.Contains("/dist/");
				});
		}

		// =================================================================
		// Сшивка Id у auto-якорей при пересканировании.
		//
		// Цель: между двумя последовательными `listAnchors` сохранять Id
		// у тех auto-якорей, чья семантическая идентичность не изменилась.
		// Это предусловие для будущих user-групп / пользовательских ссылок,
		// которые будут привязываться к стабильному `AnchorId`.
		//
		// Алгоритм двухслойный:
		//   1) Точный матч по ключу (lang|kind|filepath|parent|method).
		//      Покрывает рескан без правок и большинство мелких правок,
		//      где имя/родитель/файл не поменялись (отступы, тело метода и т.п.).
		//   2) VP-tree KNN на остатке. На MVP пороги по умолчанию = 0,
		//      т.е. слой 2 фактически выключен. Параметры порогов
		//      вынесены в `StitchSettings` и могут быть подняты позже.
		// =================================================================

		internal sealed class StitchSettings
		{
			/// <summary>
			/// Порог дистанции для callable-профилей (gqlField, tsMember). 0 = слой 2 выключен.
			/// До сентября 2026 был 0.0 (слой отключён). Калибровка по risk–coverage на tune-части
			/// корпуса реальной истории (experiments/e05): tau 0.30 + margin 0.30 → покрытие 98.4 %,
			/// ошибочных автопривязок 0.08 %.
			/// </summary>
			public double MaxDistanceCallable = RebindSettings.ReadEnvDouble("LAND_STITCH_TAU", 0.30);
			/// <summary>
			/// Порог для record-профилей (gqlTypeDef, gqlInputDef, gqlInterfaceDef). Веса record-профиля
			/// не калибровались; значение предварительное.
			/// </summary>
			public double MaxDistanceRecord = RebindSettings.ReadEnvDouble("LAND_STITCH_TAU_RECORD", 0.45);
			/// <summary>Минимальный относительный отрыв второго кандидата (d2 - d1) / d2.</summary>
			public double MinRelativeMargin = RebindSettings.ReadEnvDouble("LAND_STITCH_MARGIN", 0.30);

			public static StitchSettings Default => new StitchSettings();

			public double GetThreshold(string profileKey)
			{
				if (string.IsNullOrEmpty(profileKey)) return 0.0;
				return profileKey.Contains($":{AnchorFamilyCallable}:", StringComparison.OrdinalIgnoreCase)
					? MaxDistanceCallable
					: MaxDistanceRecord;
			}
		}

		internal sealed class StitchResult
		{
			public List<(string PrevId, string FreshIdReplaced)> MatchedExact { get; } = new();
			public List<(string PrevId, string FreshIdReplaced, double Dist)> MatchedFuzzy { get; } = new();
			public List<string> Lost { get; } = new();
			public List<string> Fresh { get; } = new();

			public int TotalMatched => MatchedExact.Count + MatchedFuzzy.Count;
		}

		private static string NormalizeStitchFilePath(string filePath)
		{
			var normalized = Path.GetFullPath(filePath ?? string.Empty).Replace('\\', '/');
			return normalized.ToLowerInvariant();
		}

		private static string BuildStitchExactKey(TreeNode auto)
		{
			if (auto == null) return null;
			if (!string.Equals(auto.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)) return null;

			var lang = (auto.Language ?? string.Empty).Trim().ToLowerInvariant();
			var kind = (auto.AnchorKind ?? string.Empty).Trim();
			var pathNorm = NormalizeStitchFilePath(auto.Filepath);
			var method = auto.MethodNameNorm ?? string.Empty;
			var parentRaw = auto.ParentNameRaw ?? string.Empty;

			if (string.IsNullOrEmpty(lang) || string.IsNullOrEmpty(kind))
				return null;

			return $"{lang}|{kind}|{pathNorm}|{parentRaw}|{method}";
		}

		/// <summary>
		/// Переписывает Id у новых auto-якорей так, чтобы они унаследовали Id
		/// у соответствующих "старых" auto-якорей предыдущего snapshot-а.
		/// Модифицирует <paramref name="freshAuto"/> на месте; возвращает статистику.
		///
		/// Manual-якоря не трогаем — у них своя ветка восстановления через
		/// previousManualAnchors в <see cref="ListAnchorsAsync"/>.
		/// </summary>
		private StitchResult StitchAutoAnchorIdsFromPrevious(
			IReadOnlyCollection<TreeNode> previousAuto,
			IReadOnlyCollection<TreeNode> freshAuto,
			StitchSettings settings = null)
		{
			settings ??= StitchSettings.Default;
			var result = new StitchResult();

			var prevList = (previousAuto ?? Array.Empty<TreeNode>())
				.Where(x => x != null
					&& string.Equals(x.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
					&& !x.IsManual
					&& !string.IsNullOrWhiteSpace(x.Id))
				.ToList();
			var freshList = (freshAuto ?? Array.Empty<TreeNode>())
				.Where(x => x != null
					&& string.Equals(x.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
					&& !x.IsManual
					&& !string.IsNullOrWhiteSpace(x.Id))
				.ToList();

			if (prevList.Count == 0)
			{
				foreach (var f in freshList) result.Fresh.Add(f.Id);
				return result;
			}

			if (freshList.Count == 0)
			{
				foreach (var p in prevList) result.Lost.Add(p.Id);
				return result;
			}

			// Чтобы корректно отслеживать "занятые" fresh-узлы после переименования Id —
			// используем object identity, а не Id (Id меняется на месте).
			var claimedFresh = new HashSet<TreeNode>();

			// ---- Слой 1: точное совпадение ключа ----
			var freshByKey = new Dictionary<string, List<TreeNode>>(StringComparer.Ordinal);
			foreach (var f in freshList)
			{
				var key = BuildStitchExactKey(f);
				if (key == null) continue;
				if (!freshByKey.TryGetValue(key, out var bucket))
					freshByKey[key] = bucket = new List<TreeNode>();
				bucket.Add(f);
			}

			var unclaimedPrev = new List<TreeNode>();
			foreach (var p in prevList)
			{
				var key = BuildStitchExactKey(p);
				if (key == null || !freshByKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
				{
					unclaimedPrev.Add(p);
					continue;
				}

				TreeNode chosen = null;
				var bestOffsetDelta = int.MaxValue;
				foreach (var c in candidates)
				{
					if (claimedFresh.Contains(c)) continue;
					var pStart = p.StartOffset ?? 0;
					var cStart = c.StartOffset ?? 0;
					var delta = Math.Abs(pStart - cStart);
					if (delta < bestOffsetDelta)
					{
						bestOffsetDelta = delta;
						chosen = c;
					}
				}

				if (chosen == null)
				{
					unclaimedPrev.Add(p);
					continue;
				}

				var replacedId = chosen.Id;
				chosen.Id = p.Id;
				// Перенос пользовательского комментария на новый якорь:
				// поле принадлежит стабильной идентичности, не позиции в коде.
				chosen.Comment = p.Comment;
				claimedFresh.Add(chosen);
				result.MatchedExact.Add((p.Id, replacedId));
			}

			// ---- Слой 2: VP-tree KNN на остатке ----
			var unclaimedFresh = freshList.Where(x => !claimedFresh.Contains(x)).ToList();
			var canFuzzyMatch = unclaimedPrev.Count > 0 && unclaimedFresh.Count > 0;

			if (canFuzzyMatch)
			{
				// Леса по profileKey — каждый лес содержит только не занятые fresh-узлы.
				var nodesByProfile = new Dictionary<string, List<TreeNode>>(StringComparer.OrdinalIgnoreCase);
				var contextsByProfile = new Dictionary<string, List<AnchorContext>>(StringComparer.OrdinalIgnoreCase);

				foreach (var f in unclaimedFresh)
				{
					if (!CanParticipateInRebinding(f)) continue;
					var pk = GetProfileKey(f);
					var ctx = BuildAnchorContext(f);
					if (string.IsNullOrEmpty(pk) || ctx == null) continue;

					if (!nodesByProfile.TryGetValue(pk, out var nl))
					{
						nodesByProfile[pk] = nl = new List<TreeNode>();
						contextsByProfile[pk] = new List<AnchorContext>();
					}
					nl.Add(f);
					contextsByProfile[pk].Add(ctx);
				}

				var treesByProfile = new Dictionary<string, VPTree<AnchorContext>>(StringComparer.OrdinalIgnoreCase);
				foreach (var kv in contextsByProfile)
				{
					if (kv.Value.Count == 0) continue;
					var weights = GetWeightsForProfileKey(kv.Key);
					treesByProfile[kv.Key] = new VPTree<AnchorContext>(
						kv.Value,
						(a, b) => AnchorContextDistance(a, b, weights),
						42,
						Tracing.Tracer);
				}

				// Сначала решения для всех несопоставленных старых якорей (tau + margin),
				// затем назначение один-к-одному в порядке возрастания дистанции: самые
				// очевидные соответствия занимают кандидатов первыми (как в RebindEngine).
				var proposals = new List<(TreeNode Prev, TreeNode Fresh, double Dist)>();
				foreach (var p in unclaimedPrev)
				{
					if (!CanParticipateInRebinding(p))
					{
						result.Lost.Add(p.Id);
						continue;
					}

					var pk = GetProfileKey(p);
					if (string.IsNullOrEmpty(pk)
						|| !treesByProfile.TryGetValue(pk, out var tree)
						|| !nodesByProfile.TryGetValue(pk, out var profileNodes))
					{
						result.Lost.Add(p.Id);
						continue;
					}

					var threshold = settings.GetThreshold(pk);
					if (threshold <= 0.0)
					{
						// слой 2 отключён для этого профиля
						result.Lost.Add(p.Id);
						continue;
					}

					var query = BuildAnchorContext(p);
					if (query == null)
					{
						result.Lost.Add(p.Id);
						continue;
					}

					var cands = tree.KNearest(query, 2);
					var decision = DecideFor(cands, threshold, settings.MinRelativeMargin);
					if (decision.Status != RebindStatus.Accepted
						|| decision.BestIndex < 0 || decision.BestIndex >= profileNodes.Count)
					{
						Debug($"[stitch] '{p.Name}' {decision.Status}: {decision.Reason}");
						result.Lost.Add(p.Id);
						continue;
					}

					proposals.Add((p, profileNodes[decision.BestIndex], decision.BestDist));
				}

				foreach (var (p, freshNode, dist) in proposals.OrderBy(x => x.Dist).ThenBy(x => x.Prev.Id, StringComparer.Ordinal))
				{
					if (claimedFresh.Contains(freshNode))
					{
						// Кандидат уже занят более близким старым якорем — этот теряется.
						Debug($"[stitch] '{p.Name}' Lost: candidate '{freshNode.Name}' already claimed");
						result.Lost.Add(p.Id);
						continue;
					}

					var replacedId = freshNode.Id;
					freshNode.Id = p.Id;
					freshNode.Comment = p.Comment;
					claimedFresh.Add(freshNode);
					result.MatchedFuzzy.Add((p.Id, replacedId, dist));
				}
			}
			else
			{
				foreach (var p in unclaimedPrev)
					result.Lost.Add(p.Id);
			}

			foreach (var f in freshList)
				if (!claimedFresh.Contains(f))
					result.Fresh.Add(f.Id);

			return result;
		}
	}
}
