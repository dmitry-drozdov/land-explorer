using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	// =======================
	// Distances (metrics)
	// =======================
	public static class Dist
	{
		public sealed class Weights
		{
			public double NameW = 0.20;
			public double ArgsW = 0.40;
			public double ReturnsW = 0.20;
			public double ParentW = 0.10;
			public double NeighW = 0.10;

			public int NameScale = 8;
			public int TypeScale = 16;
			public int ArgNameScale = 8;
			public int ReturnsScale = 16;
			public int ReceiverScale = 8;
		}

		public static int Lev(string a, string b)
		{
			if (a == null) a = "";
			if (b == null) b = "";
			if (a == (object)b) return 0;
			int n = a.Length, m = b.Length;
			if (n == 0) return m;
			if (m == 0) return n;

			var dp = new int[m + 1];
			for (int j = 0; j <= m; j++) dp[j] = j;

			for (int i = 1; i <= n; i++)
			{
				int prev = dp[0];
				dp[0] = i;
				for (int j = 1; j <= m; j++)
				{
					int tmp = dp[j];
					int cost = a[i - 1] == b[j - 1] ? 0 : 1;
					int del = dp[j] + 1;
					int ins = dp[j - 1] + 1;
					int sub = prev + cost;
					int v = del < ins ? del : ins;
					dp[j] = v < sub ? v : sub;
					prev = tmp;
				}
			}
			return dp[m];
		}

		// Truncated + scaled Levenshtein keeps metric properties
		public static double LevScaled(string a, string b, int scale)
		{
			int d = Lev(a, b);
			if (d > scale) d = scale;
			return (double)d / (double)scale;
		}

		public static double ArgPairCost(MethodAnchor.Arg a, MethodAnchor.Arg b, Weights w, double wType, double wName)
		{
			double dt = LevScaled(a != null ? a.TypeNorm : "", b != null ? b.TypeNorm : "", w.TypeScale);
			double dn = LevScaled(a != null ? a.NameNorm : "", b != null ? b.NameNorm : "", w.ArgNameScale);
			return wType * dt + wName * dn; // in [0, wType+wName]
		}

		public static double ArgNullCost(double wType, double wName) { return wType + wName; }

		// Hungarian-based permutation-invariant distance
		public static double ArgsDistance(List<MethodAnchor.Arg> A, List<MethodAnchor.Arg> B, Weights w)
		{
			if (A == null) A = new List<MethodAnchor.Arg>();
			if (B == null) B = new List<MethodAnchor.Arg>();
			int n = A.Count > B.Count ? A.Count : B.Count;
			if (n == 0) return 0.0;

			double wt = 1.0, wn = 1.0;
			double nullCost = ArgNullCost(wt, 0.0); // только тип, имена не штрафуем

			var C = new double[n, n];
			for (int i = 0; i < n; i++)
				for (int j = 0; j < n; j++)
				{
					bool ai = i < A.Count, bj = j < B.Count;
					if (ai && bj)
						C[i, j] = ArgPairCost(A[i], B[j], w, wt, wn);
					else
						C[i, j] = nullCost; // match to dummy
				}

			double total = Hungarian.MinCost(C);
			return total / (double)(n * (wt + wn));
		}

		public static string NeighborSigKey(MethodAnchor m)
		{
			var sb = new StringBuilder(128);
			sb.Append(m.ReturnTypeNorm).Append('|');
			if (m.Args != null && m.Args.Count > 0)
			{
				var types = new List<string>(m.Args.Count);
				for (int i = 0; i < m.Args.Count; i++) types.Add(m.Args[i].TypeNorm);
				types.Sort(StringComparer.Ordinal);
				for (int i = 0; i < types.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(types[i]);
				}
			}
			return sb.ToString();
		}

		// Взвешенная Jaccard-дистанция для мешков соседей (метрика)
		public static double WeightedJaccard(IDictionary<string, double> A, IDictionary<string, double> B)
		{
			int ac = (A != null) ? A.Count : 0;
			int bc = (B != null) ? B.Count : 0;
			if (ac == 0 && bc == 0) return 0.0;

			double minSum = 0.0, maxSum = 0.0;

			if (ac < bc)
			{
				var keys = new HashSet<string>(B.Keys);
				if (A != null) foreach (var kv in A)
					{
						double a = kv.Value, b;
						if (B.TryGetValue(kv.Key, out b))
						{
							minSum += (a < b ? a : b);
							maxSum += (a > b ? a : b);
							keys.Remove(kv.Key);
						}
						else maxSum += a;
					}
				foreach (var k in keys) maxSum += B[k];
			}
			else
			{
				var keys = new HashSet<string>(A != null ? A.Keys : new string[0]);
				if (B != null) foreach (var kv in B)
					{
						double b = kv.Value;
						if (A != null && A.TryGetValue(kv.Key, out double a))
						{
							minSum += (a < b ? a : b);
							maxSum += (a > b ? a : b);
							keys.Remove(kv.Key);
						}
						else maxSum += b;
					}
				foreach (var k in keys) maxSum += A[k];
			}
			if (maxSum <= 0) return 0.0;
			return 1.0 - (minSum / maxSum);
		}

		public static double AnchorDistance(MethodAnchor a, MethodAnchor b, Weights w)
		{
			double dName = 0, dArgs = 0, dRet = 0, dRecv = 0, dNeigh = 0;


			dName = LevScaled(a?.MethodNameNorm ?? "", b?.MethodNameNorm ?? "", w.NameScale);
			dArgs = ArgsDistance(a?.Args, b?.Args, w);
			dRet = LevScaled(a?.ReturnTypeNorm ?? "", b?.ReturnTypeNorm ?? "", w.ReturnsScale);
			dRecv = LevScaled(a?.ParentNameNorm ?? "", b?.ParentNameNorm ?? "", w.ReceiverScale);
			dNeigh = WeightedJaccard(a?.NeighborBag, b?.NeighborBag);


			return w.NameW * dName
			     + w.ArgsW * dArgs
			     + w.ReturnsW * dRet
			     + w.ParentW * dRecv
			     + w.NeighW * dNeigh;
		}
	}
}
