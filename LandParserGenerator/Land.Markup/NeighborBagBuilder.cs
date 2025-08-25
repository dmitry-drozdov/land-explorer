using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	public static class NeighborBagBuilder
	{
		/// <summary>
		/// Строит NeighborBag для якорей, упорядоченных по объявлению внутри одной группы (родителя).
		/// Вызывать по группам (родительским типам/классам/файлам).
		/// </summary>
		public static void BuildBagsInOrder(List<MethodAnchor> anchorsInOrder, int window = 3)
		{
			if (anchorsInOrder == null || anchorsInOrder.Count == 0) return;

			for (int i = 0; i < anchorsInOrder.Count; i++)
			{
				var a = anchorsInOrder[i];
				if (a.NeighborBag == null)
					a.NeighborBag = new Dictionary<string, double>(StringComparer.Ordinal);
				else
					a.NeighborBag.Clear();

				for (int d = 1; d <= window; d++)
				{
					double w = 1.0 / (1.0 + d); // убывающий вес
					int j1 = i - d, j2 = i + d;

					if (j1 >= 0)
					{
						var key = Dist.NeighborSigKey(anchorsInOrder[j1]);
						a.NeighborBag.TryGetValue(key, out var cur);
						a.NeighborBag[key] = cur + w;
					}
					if (j2 < anchorsInOrder.Count)
					{
						var key = Dist.NeighborSigKey(anchorsInOrder[j2]);
						a.NeighborBag.TryGetValue(key, out var cur);
						a.NeighborBag[key] = cur + w;
					}
				}
			}
		}
	}
}
