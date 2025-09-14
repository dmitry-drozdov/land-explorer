using OpenTracing;
using OpenTracing.Util;
using System;
using System.Collections.Generic;
using System.IO;
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

		public Rebinder(IEnumerable<MethodAnchor> anchors, Dist.Weights weights, GlobalTracer tracer = null)
		{
			_anchors = (anchors ?? new List<MethodAnchor>()).ToList();
			_w = weights ?? new Dist.Weights();
			_tree = new VPTree<MethodAnchor>(_anchors, (a, b) => Dist.AnchorDistance(a, b, _w), 42, tracer);
		}

		public List<VPTree<MethodAnchor>.KNNResult> Query(MethodAnchor query, int k)
		{
			return _tree.KNearest(query, k);
		}

		public void ShapShot()
		{
			var snap = _tree.ToSnapshot(a => a.Id, notes: "neighbors:window=3,decay=1/(1+d)");
			VpTreeStorage.SaveJson("vp_index.json", snap);
		}
	}
}
