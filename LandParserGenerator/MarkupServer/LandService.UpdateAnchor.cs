using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		[JsonRpcMethod("land/updateAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResult> UpdateAnchorAsync(UpdateAnchorParams p)
		{
			if (!nodesById.ContainsKey(p.anchorId))
			{
				Debug($"not found anchor by id [{p.anchorId}]");
				return Task.FromResult(new UpdateAnchorResult { });
			}
			var node = nodesById[p.anchorId];


			var roots = new List<TreeNode> { };
			Tracing.Init();

			IEnumerable<string> gqlFiles = GetAllFiles("e:\\phd\\ts\\test", "graphql");
			List<MethodAnchor> gqlAnchors = new List<MethodAnchor>();

			var gqlAnchorsCnt = 0;
			using (var scope = Tracing.Tracer.BuildSpan("ProcessGqlFiles").StartActive())
				foreach (var gqlFile in gqlFiles)
				{
					gqlAnchorsCnt += ParseGqlFile(gqlFile, roots, gqlAnchors);
				}


			var _w = new Dist.Weights();
			VPTree<MethodAnchor> _tree;
			using (var scope = Tracing.Tracer.BuildSpan("BuildTree").StartActive())
				_tree = new VPTree<MethodAnchor>(gqlAnchors, (a, b) => Dist.AnchorDistance(a, b, _w), 42);

			var cands = _tree.KNearest(new MethodAnchor
			{
				Id = node.Id,
				ParentNameNorm = node.ParentNameNorm,
				ReturnTypeNorm = node.ReturnTypeNorm,
				MethodNameNorm = node.MethodNameNorm,
				Args = node.Args.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
			}, 1);

			Debug($"{cands[0].Dist}");

			var res = gqlAnchors[cands[0].Index];

			return Task.FromResult(new UpdateAnchorResult
			{
				updatedNode = new TreeNode
				{
					Id = p.anchorId,
					ParentNameNorm = res.ParentNameNorm,
					ReturnTypeNorm = res.ReturnTypeNorm,
					MethodNameNorm = res.MethodNameNorm,
					Name = res.MethodNameNorm,
					NodeType = "anchor",
					StartOffset = res.StartOffset,
					EndOffset = res.EndOffset,
				},
			});
		}
	}
}
