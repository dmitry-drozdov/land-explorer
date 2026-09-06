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

		/// <summary>
		/// Строит индекс по якорям. Список используется как есть (в том порядке, в котором подан);
		/// для воспроизводимого результата подавайте CanonicalOrder.Sort(anchors).
		/// Если у якорей нет NeighborBag и buildNeighborBags = true, мешки соседей строятся
		/// явно через AnchorsIO.BuildNeighborBags (раньше это делалось скрыто внутри VPTree).
		/// </summary>
		public Rebinder(IEnumerable<MethodAnchor> anchors, Dist.Weights weights, GlobalTracer tracer = null, bool buildNeighborBags = false, int neighborWindow = 4)
		{
			_anchors = (anchors ?? new List<MethodAnchor>()).ToList();
			_w = weights ?? new Dist.Weights();
			if (buildNeighborBags)
				AnchorsIO.BuildNeighborBags(_anchors, neighborWindow);
			_tree = new VPTree<MethodAnchor>(_anchors, (a, b) => Dist.AnchorDistance(a, b, _w), 42, tracer);
		}

		public IReadOnlyList<MethodAnchor> Anchors => _anchors;

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
