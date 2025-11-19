using Land.Core.Parsing.Tree;
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
							var treeNode = GetTreeNodeFromGqlNode(func, gqlFile);
							nodesById[treeNode.Id] = treeNode;

							gqlAnchors.Add(new MethodAnchor
							{
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
							var treeNode = GetTreeNodeFromTsNode(node, tsFile, nodes.Key);
							nodesById[treeNode.Id] = treeNode;
							tsAnchors++;
							roots.Add(treeNode);
						}
					}
				}

			//AnchorsIO.SaveJson(@"e:\phd\anchors.json", gqlAnchors);

			Debug($"gqlAnchors={gqlAnchorsCnt}, tsAnchors={tsAnchors}");

			return Task.FromResult(new ListTreeResult { Roots = roots });
		}
	}
}
