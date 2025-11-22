using Land.Core.Parsing.Tree;
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
		private TreeNode GetTreeNodeFromTsNode(Node n, string filepath, string parentName)
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

		private TreeNode GetTreeNodeFromGqlNode(Node n, string filepath)
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

		private int ParseGqlFile(string gqlFile, List<TreeNode> roots, List<MethodAnchor> gqlAnchors)
		{
			var gqlAnchorsCnt = 0;
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
					var subgroup = new TreeNode
					{
						Name = treeNode.Name,
						NodeType = "group",
					};
					treeNode.Name = "graphql";
					subgroup.Children.Add(treeNode);
					group.Children.Add(subgroup);
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

		private void Debug(string msg)
		{
			Console.Error.WriteLine(msg);
		}

		private IEnumerable<string> GetAllFiles(string folder, string ext)
		{
			return Directory.EnumerateFiles(folder, $"*.{ext}", SearchOption.AllDirectories).
				Where(x => !x.Contains(@"\vendor\"));
		}

	}
}
