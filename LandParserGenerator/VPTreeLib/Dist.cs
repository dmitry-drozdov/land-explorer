using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;

namespace VPTree
{
	// =======================
	// Distances (metrics)
	// =======================
	public static class Dist
	{
		// -----------------------
		// Lightweight profiling (aggregated, thread-safe)
		// -----------------------
		public static class Prof
		{
			private static long _ticksTotal;
			private static long _ticksName;
			private static long _ticksArgs;
			private static long _ticksRet;
			private static long _ticksRecv;
			private static long _ticksNeigh;
			private static long _calls;
			private static long _callsLev;
			private static long _callsArgs;
			private static long _callsJacc;
			private static long _charsLev;
			private static long _argsPairs;
			private static long _neighMinIter;
			private static long _argsNMax;
			private static long _argsNGt10;
			// buckets 0..12 (12 = "12+")
			private static readonly long[] _argsNHist = new long[13];

			public static double TickToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

			public static void Reset()
			{
				Interlocked.Exchange(ref _ticksTotal, 0);
				Interlocked.Exchange(ref _ticksName, 0);
				Interlocked.Exchange(ref _ticksArgs, 0);
				Interlocked.Exchange(ref _ticksRet, 0);
				Interlocked.Exchange(ref _ticksRecv, 0);
				Interlocked.Exchange(ref _ticksNeigh, 0);
				Interlocked.Exchange(ref _calls, 0);
				Interlocked.Exchange(ref _callsLev, 0);
				Interlocked.Exchange(ref _callsArgs, 0);
				Interlocked.Exchange(ref _callsJacc, 0);
				Interlocked.Exchange(ref _charsLev, 0);
				Interlocked.Exchange(ref _argsPairs, 0);
				Interlocked.Exchange(ref _neighMinIter, 0);
				Interlocked.Exchange(ref _argsNMax, 0);
				Interlocked.Exchange(ref _argsNGt10, 0);
				for (int i = 0; i < _argsNHist.Length; i++)
					Interlocked.Exchange(ref _argsNHist[i], 0);
			}

			public static (long ticksTotal, long ticksName, long ticksArgs, long ticksRet, long ticksRecv, long ticksNeigh,
				long calls, long callsLev, long callsArgs, long callsJacc, long charsLev, long argsPairs, long neighMinIter,
				long argsNMax, long argsNGt10) Snapshot()
			{
				return (
					Interlocked.Read(ref _ticksTotal),
					Interlocked.Read(ref _ticksName),
					Interlocked.Read(ref _ticksArgs),
					Interlocked.Read(ref _ticksRet),
					Interlocked.Read(ref _ticksRecv),
					Interlocked.Read(ref _ticksNeigh),
					Interlocked.Read(ref _calls),
					Interlocked.Read(ref _callsLev),
					Interlocked.Read(ref _callsArgs),
					Interlocked.Read(ref _callsJacc),
					Interlocked.Read(ref _charsLev),
					Interlocked.Read(ref _argsPairs),
					Interlocked.Read(ref _neighMinIter),
					Interlocked.Read(ref _argsNMax),
					Interlocked.Read(ref _argsNGt10)
				);
			}

			public static string GetArgsNHistogramString()
			{
				// build once at the end of VP.Build; OK to allocate here
				var sb = new StringBuilder(128);
				for (int i = 0; i < _argsNHist.Length; i++)
				{
					var v = Interlocked.Read(ref _argsNHist[i]);
					if (v == 0) continue;
					if (sb.Length > 0) sb.Append(',');
					sb.Append(i == 12 ? "12+" : i.ToString());
					sb.Append(':');
					sb.Append(v);
				}
				return sb.Length == 0 ? "" : sb.ToString();
			}

			internal static void AddCall() => Interlocked.Increment(ref _calls);
			internal static void AddLev(int aLen, int bLen)
			{
				Interlocked.Increment(ref _callsLev);
				Interlocked.Add(ref _charsLev, aLen + bLen);
			}
			internal static void AddArgsPairs(int pairs)
			{
				Interlocked.Increment(ref _callsArgs);
				Interlocked.Add(ref _argsPairs, pairs);
			}
			internal static void AddArgsN(int n)
			{
				// max
				long cur;
				do
				{
					cur = Interlocked.Read(ref _argsNMax);
					if (n <= cur) break;
				} while (Interlocked.CompareExchange(ref _argsNMax, n, cur) != cur);

				if (n > 10) Interlocked.Increment(ref _argsNGt10);
				int b = n;
				if (b < 0) b = 0;
				if (b > 12) b = 12;
				Interlocked.Increment(ref _argsNHist[b]);
			}
			internal static void AddJaccMinIter(int iters)
			{
				Interlocked.Increment(ref _callsJacc);
				Interlocked.Add(ref _neighMinIter, iters);
			}
			internal static void AddTicksTotal(long t) => Interlocked.Add(ref _ticksTotal, t);
			internal static void AddTicksName(long t) => Interlocked.Add(ref _ticksName, t);
			internal static void AddTicksArgs(long t) => Interlocked.Add(ref _ticksArgs, t);
			internal static void AddTicksRet(long t) => Interlocked.Add(ref _ticksRet, t);
			internal static void AddTicksRecv(long t) => Interlocked.Add(ref _ticksRecv, t);
			internal static void AddTicksNeigh(long t) => Interlocked.Add(ref _ticksNeigh, t);
		}
		// NOTE: This file is on the hot path for VP-tree build.
		// The build trace shows we spend ~99% time inside distance().
		// Optimizations here focus on:
		//  - avoiding per-call allocations (Levenshtein DP arrays, HashSet)
		//  - early abandoning Levenshtein when the result is truncated by 'scale'
		//  - reducing overhead for small-arity argument matching
		private const int SmallArgsMaxN = 8;

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

			// Single-row DP (allocates). Prefer LevLimit/LevScaled in hot code.
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

		/// <summary>
		/// Levenshtein distance with early-abandon at <paramref name="limit"/>.
		/// Returns any value &gt; limit if the true distance exceeds limit.
		/// Uses a banded DP (Ukkonen-style) to avoid O(n*m) when limit is small.
		/// </summary>
		public static int LevLimit(string a, string b, int limit)
		{
			if (limit <= 0) return (a == b || a == (object)b) ? 0 : 1;
			if (a == null) a = "";
			if (b == null) b = "";
			if (a == (object)b) return 0;

			int n = a.Length, m = b.Length;
			if (n == 0) return m;
			if (m == 0) return n;

			// Quick lower bound.
			int lenDiff = n - m;
			if (lenDiff < 0) lenDiff = -lenDiff;
			if (lenDiff > limit) return limit + 1;

			// Ensure b is the shorter dimension for smaller arrays.
			if (m > n)
			{
				var tmpS = a; a = b; b = tmpS;
				int tmp = n; n = m; m = tmp;
			}

			// We only need the last row; use ArrayPool to avoid per-call allocations.
			var pool = System.Buffers.ArrayPool<int>.Shared;
			int[] dp = pool.Rent(m + 1);
			try
			{
				// Initialize dp[j] = j.
				for (int j = 0; j <= m; j++) dp[j] = j;

				// Banded DP: only compute j in [i-limit, i+limit].
				// Cells outside the band are treated as 'infinite' (limit+1).
				int inf = limit + 1;
				for (int i = 1; i <= n; i++)
				{
					int prevDiag = dp[0];
					dp[0] = i;

					int jStart = i - limit;
					if (jStart < 1) jStart = 1;
					int jEnd = i + limit;
					if (jEnd > m) jEnd = m;

					// If the band does not intersect this row, distance > limit.
					if (jStart > jEnd) return limit + 1;

					// Set dp[jStart-1] to inf to prevent using values outside band.
					if (jStart > 1) dp[jStart - 1] = inf;

					int rowMin = inf;
					char ca = a[i - 1];
					for (int j = jStart; j <= jEnd; j++)
					{
						int tmp = dp[j];
						int cost = (ca == b[j - 1]) ? 0 : 1;
						int del = dp[j] + 1;
						int ins = dp[j - 1] + 1;
						int sub = prevDiag + cost;
						int v = del < ins ? del : ins;
						v = (v < sub) ? v : sub;
						dp[j] = v;
						prevDiag = tmp;
						if (v < rowMin) rowMin = v;
					}

					// Early abandon: even the best cell in this row exceeds limit.
					if (rowMin > limit) return limit + 1;
				}
				return dp[m];
			}
			finally
			{
				pool.Return(dp, clearArray: false);
			}
		}

		// Truncated + scaled Levenshtein keeps metric properties
		public static double LevScaled(string a, string b, int scale)
		{
			// We truncate at 'scale', so do a bounded edit distance.
			int d = LevLimit(a, b, scale);
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
			// Hot path during VP-tree build: keep this allocation-free.
			int aCount = A != null ? A.Count : 0;
			int bCount = B != null ? B.Count : 0;
			int n = aCount > bCount ? aCount : bCount;
			if (n == 0) return 0.0;

			double wt = 1.0, wn = 1.0;
			double nullCost = ArgNullCost(wt, 0.0); // only type, do not penalize names

			// For our typical argument counts (n is usually small), a bitmask DP assignment
			// is faster than Hungarian and avoids a 2D array allocation.
			if (n <= 10)
			{
				// cost[i*n + j]
				var pool = System.Buffers.ArrayPool<double>.Shared;
				double[] cost = pool.Rent(n * n);
				double[] dp = pool.Rent(1 << n);
				try
				{
					// Fill costs
					int idx = 0;
					for (int i = 0; i < n; i++)
					{
						bool ai = i < aCount;
						for (int j = 0; j < n; j++, idx++)
						{
							bool bj = j < bCount;
							if (ai && bj)
								cost[idx] = ArgPairCost(A[i], B[j], w, wt, wn);
							else
								cost[idx] = nullCost; // match to dummy
						}
					}

					int mskN = 1 << n;
					// Init dp with +inf
					for (int k = 0; k < mskN; k++) dp[k] = double.PositiveInfinity;
					dp[0] = 0.0;

					// Small popcount helper (n<=10 => masks<=1024)
					// Precompute popcount table once.
					// (We keep it local-static to not add extra types/fields.)
					int[] pop = PopcountTable10;

					for (int mask = 0; mask < mskN; mask++)
					{
						int i = pop[mask]; // which row we are assigning now
						if (i >= n) continue;

						double baseCost = dp[mask];
						if (double.IsPositiveInfinity(baseCost)) continue;

						int rowBase = i * n;
						int free = (~mask) & (mskN - 1);
						while (free != 0)
						{
							int lsb = free & -free;
							int j = TrailingIndex(lsb);
							int next = mask | lsb;

							double v = baseCost + cost[rowBase + j];
							if (v < dp[next]) dp[next] = v;

							free ^= lsb;
						}
					}

					double total = dp[mskN - 1];
					return total / (double)(n * (wt + wn));
				}
				finally
				{
					pool.Return(cost, clearArray: false);
					pool.Return(dp, clearArray: false);
				}
			}

			// Fallback for larger n (rare for method signatures).
			var C = new double[n, n];
			for (int i = 0; i < n; i++)
				for (int j = 0; j < n; j++)
				{
					bool ai = i < aCount, bj = j < bCount;
					if (ai && bj)
						C[i, j] = ArgPairCost(A[i], B[j], w, wt, wn);
					else
						C[i, j] = nullCost; // match to dummy
				}

			double hung = Hungarian.MinCost(C);
			return hung / (double)(n * (wt + wn));
		}

		// Popcount for masks up to 2^10 (1024).
		// Stored as int[] so it is JIT-friendly on .NET Framework.
		private static readonly int[] PopcountTable10 = BuildPopcountTable10();

		private static int[] BuildPopcountTable10()
		{
			int[] t = new int[1 << 10];
			for (int i = 1; i < t.Length; i++)
				t[i] = t[i >> 1] + (i & 1);
			return t;
		}

		// lsb is power-of-two, return its index [0..30]
		private static int TrailingIndex(int lsb)
		{
			// De Bruijn sequence method (works for 32-bit ints)
			unchecked
			{
				uint v = (uint)lsb;
				// Isolate lowest set bit
				v = (uint)(v & (uint)-(int)v);
				// De Bruijn
				uint idx = (v * 0x077CB531U) >> 27;
				return _debruijnIdx32[idx];
			}
		}

		private static readonly int[] _debruijnIdx32 = new int[32]
		{
    0, 1, 28, 2, 29, 14, 24, 3,
    30, 22, 20, 15, 25, 17, 4, 8,
    31, 27, 13, 23, 21, 19, 16, 7,
    26, 12, 18, 6, 11, 5, 10, 9
		};

		// NOTE: A previous archive accidentally contained duplicated leftover code below this point.
		// It has been removed to keep the file compiling on .NET Framework 4.8.

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

			// Avoid HashSet allocations.
			// For non-negative weights: sum(max) = sumA + sumB - sum(min over intersection)
			double sumA = 0.0, sumB = 0.0;
			if (A != null) foreach (var kv in A) sumA += kv.Value;
			if (B != null) foreach (var kv in B) sumB += kv.Value;

			double minSum = 0.0;
			if (A != null && B != null)
			{
				// Iterate smaller dict for fewer hash lookups.
				if (ac <= bc)
				{
					foreach (var kv in A)
					{
						if (B.TryGetValue(kv.Key, out double bv))
						{
							double av = kv.Value;
							minSum += (av < bv ? av : bv);
						}
					}
				}
				else
				{
					foreach (var kv in B)
					{
						if (A.TryGetValue(kv.Key, out double av))
						{
							double bv = kv.Value;
							minSum += (av < bv ? av : bv);
						}
					}
				}
			}

			double maxSum = sumA + sumB - minSum;
			if (maxSum <= 0) return 0.0;
			return 1.0 - (minSum / maxSum);
		}

		public static double AnchorDistance(MethodAnchor a, MethodAnchor b, Weights w)
		{
			Prof.AddCall();
			var t0 = Stopwatch.GetTimestamp();
			double dName = 0, dArgs = 0, dRet = 0, dRecv = 0, dNeigh = 0;

			// Name
			{
				var s = a?.MethodNameNorm ?? "";
				var t = b?.MethodNameNorm ?? "";
				var ts = Stopwatch.GetTimestamp();
				Prof.AddLev(s.Length, t.Length);
				dName = LevScaled(s, t, w.NameScale);
				Prof.AddTicksName(Stopwatch.GetTimestamp() - ts);
			}

			// Args
			{
				var ts = Stopwatch.GetTimestamp();
				var aa = a?.Args;
				var bb = b?.Args;
				Prof.AddArgsN(Math.Max(aa?.Count ?? 0, bb?.Count ?? 0));
				Prof.AddArgsPairs((aa?.Count ?? 0) * (bb?.Count ?? 0));
				dArgs = ArgsDistance(aa, bb, w);
				Prof.AddTicksArgs(Stopwatch.GetTimestamp() - ts);
			}

			// Return
			{
				var s = a?.ReturnTypeNorm ?? "";
				var t = b?.ReturnTypeNorm ?? "";
				var ts = Stopwatch.GetTimestamp();
				Prof.AddLev(s.Length, t.Length);
				dRet = LevScaled(s, t, w.ReturnsScale);
				Prof.AddTicksRet(Stopwatch.GetTimestamp() - ts);
			}

			// Receiver
			{
				var s = a?.ParentNameNorm ?? "";
				var t = b?.ParentNameNorm ?? "";
				var ts = Stopwatch.GetTimestamp();
				Prof.AddLev(s.Length, t.Length);
				dRecv = LevScaled(s, t, w.ReceiverScale);
				Prof.AddTicksRecv(Stopwatch.GetTimestamp() - ts);
			}

			// Neighbors
			{
				var ts = Stopwatch.GetTimestamp();
				var bagA = a?.NeighborBag;
				var bagB = b?.NeighborBag;
				Prof.AddJaccMinIter(Math.Min(bagA?.Count ?? 0, bagB?.Count ?? 0));
				dNeigh = WeightedJaccard(bagA, bagB);
				Prof.AddTicksNeigh(Stopwatch.GetTimestamp() - ts);
			}

			var res = w.NameW * dName
				+ w.ArgsW * dArgs
				+ w.ReturnsW * dRet
				+ w.ParentW * dRecv
				+ w.NeighW * dNeigh;

			Prof.AddTicksTotal(Stopwatch.GetTimestamp() - t0);
			return res;
		}
	}
}
