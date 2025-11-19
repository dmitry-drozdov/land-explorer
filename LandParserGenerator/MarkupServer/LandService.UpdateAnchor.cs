using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
			// TODO update with VP TREE
			node.StartOffset += 12;
			return Task.FromResult(new UpdateAnchorResult
			{
				updatedNode = node,
			});
		}
	}
}
