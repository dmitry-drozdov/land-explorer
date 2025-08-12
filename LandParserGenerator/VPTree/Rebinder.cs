using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	// =======================
	// Rebinder helper
	// =======================
	public sealed class Rebinder
	{
		private readonly List<MethodAnchor> _anchors;
		private readonly VPTree<MethodAnchor> _tree;
		private readonly Dist.Weights _w;

		public Rebinder(IEnumerable<MethodAnchor> anchors, Dist.Weights weights)
		{
			_anchors = (anchors ?? new List<MethodAnchor>()).ToList();
			_w = weights ?? new Dist.Weights();
			_tree = new VPTree<MethodAnchor>(_anchors, (a, b) => Dist.AnchorDistance(a, b, _w), 42);
		}

		public List<VPTree<MethodAnchor>.KNNResult> Query(MethodAnchor query, int k)
		{
			return _tree.KNearest(query, k);
		}
	}
}
