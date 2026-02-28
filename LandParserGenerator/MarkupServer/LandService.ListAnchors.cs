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
			if (p == null || string.IsNullOrWhiteSpace(p.folderPath))
				throw new ArgumentException("folderPath is required");

			currentFolderPath = Path.GetFullPath(p.folderPath);

			// Сбрасываем состояние (важно при работе с несколькими файлами и при повторном reload()).
			nodesById.Clear();
			currentGqlAnchors.Clear();
			currentTree = null;

			var roots = new List<TreeNode> { };
			Tracing.Init();


			IEnumerable<string> gqlFiles = GetAllFiles(currentFolderPath, "graphql");

			var gqlAnchorsCnt = 0;
			using (var scope = Tracing.Tracer.BuildSpan("ProcessGqlFiles").StartActive())
				foreach (var gqlFile in gqlFiles)
				{
					gqlAnchorsCnt += ParseGqlFile(gqlFile, roots, currentGqlAnchors);
				}


			VPTree<MethodAnchor> _tree;
			using (var scope = Tracing.Tracer.BuildSpan("BuildTreeListAnchors").StartActive())
				_tree = new VPTree<MethodAnchor>(currentGqlAnchors, (a, b) => Dist.AnchorDistance(a, b, currentWeights), 42, Tracing.Tracer);

			currentTree = _tree;


			/*using (var scope = Tracing.Tracer.BuildSpan("SaveTree").StartActive())
			{
				var snapshot = _tree.ToSnapshot(a => a.Id);
				VpTreeStorage.SaveJson("e:\\phd\\vp_tree.json", snapshot);
			}

			using (var scope = Tracing.Tracer.BuildSpan("LoadTree").StartActive())
				VpTreeStorage.LoadJson("e:\\phd\\vp_tree.json");*/

			/*for (int i = 0; i < 10; i++)
				using (var scope = Tracing.Tracer.BuildSpan("FindPoint").StartActive())
				{
					var res = _tree.KNearest(gqlAnchors[i],2);
					Debug($"{res[0].Dist} {res[1].Dist}");

				}*/

			IEnumerable<string> tsFiles = GetAllFiles(currentFolderPath, "ts");

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
