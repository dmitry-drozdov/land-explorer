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

		private static string Slice(Node n, string text)
		{
			if (n == null || text == null || n.Location == null)
				return "";
			var start = Math.Max(0, n.Location.Start.Offset);
			var end = Math.Min(text.Length, n.Location.End.Offset);
			if (end < start) end = start;
			return text.Substring(start, end - start);
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

		private static IEnumerable<Node> GetGqlFieldNodes(Node typeDef)
		{
			if (typeDef?.Children == null)
				yield break;

			foreach (var child in typeDef.Children)
			{
				if (child?.ToString() == "func_line")
					yield return child;
			}
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

		private TreeNode GetTreeNodeFromGqlFieldNode(Node n, string filepath, string parentTypeName, string gqlTypeKind)
		{
			filepath = Path.GetFullPath(filepath);
			var name = n.Children.First().ToString().Replace("id: ", "", StringComparison.OrdinalIgnoreCase);

			var args = n.Children.Skip(1)
				.TakeWhile(x => x.Type == "func_arg")
				.Select(x => new Tuple<string, string>(
					x.Children[0].Children[1].Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "", StringComparison.OrdinalIgnoreCase),
					x.Children[0].Children[0].ToString().Replace("id: ", "", StringComparison.OrdinalIgnoreCase)
				));

			var returnType = n.Children.Last().Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "", StringComparison.OrdinalIgnoreCase);
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
						.Select(x => GetTreeNodeFromGqlFieldNode(x, gqlFile, typeName, gqlTypeKind))
						.ToList();

					for (var i = 0; i < fieldAnchors.Count; i++)
					{
						var fieldAnchor = fieldAnchors[i];
						fieldAnchor.OrdinalInParent = i;
						fieldAnchor.NeighborBag = BuildFieldNeighborBag(fieldAnchors, i);
						gqlAnchors.Add(fieldAnchor);
						nodesById[fieldAnchor.Id] = fieldAnchor;
						count++;
					}
				}
			}
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

					groupNode.Children.Add(fieldGroup);
				}

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
					orphanRoot.Children.Add(new TreeNode
					{
						Id = MakeGroupId("tsClass", grp.Key, currentFolderPath ?? ""),
						Name = grp.Key,
						NodeType = "group",
						Children = grp.Select(CloneAnchor).ToList(),
					});
				}

				result.Add(orphanRoot);
			}

			return result;
		}

		private void RebuildMarkupRootsUnsafe()
		{
			if (currentMarkup == null)
				currentMarkup = new PersistedMarkup();
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

			var gqlFiles = GetAllFiles(currentFolderPath, "graphql");
			using (var scope = Tracing.Tracer.BuildSpan("ProcessGqlFiles").StartActive())
				ParseGqlFiles(gqlFiles, gqlAnchors);

			var tsFiles = GetAllFiles(currentFolderPath, "ts");
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
	}
}
