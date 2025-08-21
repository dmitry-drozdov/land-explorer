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
			public double NameW = 0.25;
			public double ArgsW = 0.40;
			public double ReturnsW = 0.20;
			public double ParentW = 0.20;

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

		public static double AnchorDistance(MethodAnchor a, MethodAnchor b, Weights w)
		{
			double dName = 0, dArgs = 0, dRet = 0, dRecv = 0;

			Parallel.Invoke(
			    () => dName = LevScaled(a?.MethodNameNorm ?? "", b?.MethodNameNorm ?? "", w.NameScale),
			    () => dArgs = ArgsDistance(a?.Args, b?.Args, w),
			    () => dRet = LevScaled(a?.ReturnTypeNorm ?? "", b?.ReturnTypeNorm ?? "", w.ReturnsScale),
			    () => dRecv = LevScaled(a?.ParentNameNorm ?? "", b?.ParentNameNorm ?? "", w.ReceiverScale)
			);

			return w.NameW * dName
			     + w.ArgsW * dArgs
			     + w.ReturnsW * dRet
			     + w.ParentW * dRecv;
		}
	}
}
