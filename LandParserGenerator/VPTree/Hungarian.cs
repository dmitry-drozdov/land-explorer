using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	// =======================
	// Hungarian (min-cost)
	// =======================
	public static class Hungarian
	{
		// Classic O(n^3) Hungarian on doubles; input must be square NxN (N>=1), costs >= 0
		public static double MinCost(double[,] cost)
		{
			int n = cost.GetLength(0);
			var u = new double[n + 1];
			var v = new double[n + 1];
			var p = new int[n + 1];
			var way = new int[n + 1];

			for (int i = 1; i <= n; i++)
			{
				p[0] = i;
				int j0 = 0;
				var minv = new double[n + 1];
				var used = new bool[n + 1];
				for (int j = 0; j <= n; j++) { minv[j] = double.PositiveInfinity; used[j] = false; }

				do
				{
					used[j0] = true;
					int i0 = p[j0], j1 = 0;
					double delta = double.PositiveInfinity;
					for (int j = 1; j <= n; j++)
					{
						if (used[j]) continue;
						double cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
						if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
						if (minv[j] < delta) { delta = minv[j]; j1 = j; }
					}
					for (int j = 0; j <= n; j++)
					{
						if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
						else { minv[j] -= delta; }
					}
					j0 = j1;
				} while (p[j0] != 0);

				do
				{
					int j1 = way[j0];
					p[j0] = p[j1];
					j0 = j1;
				} while (j0 != 0);
			}
			return -v[0];
		}
	}
}
