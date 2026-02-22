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


		public static class LevStats
		{
			private static readonly object _lock = new object();

			private static volatile bool _enabled;
			private static int _sampleModPow2 = 4096; // power-of-two sampling modulus
			private static long _counter;

			// We keep separate stats for common scales (8 and 16); others go to "other".
			private sealed class ScaleStats
			{
				public readonly int Scale;
				public readonly long[] RawBins;          // Levenshtein raw distance bins (integers)
				public readonly long[] ClampedNormBins;  // min(d,scale)/scale binned to [0..1] with step 0.01
				public readonly long[] UnclampedRatioBins; // (d/scale) binned to [0..4] with step 0.05 (+overflow)
				public long Sampled;
				public long Clamped; // count where d > scale (sampled)
				public long Zero;    // count where d == 0 (sampled)

				public ScaleStats(int scale)
				{
					Scale = scale;
					RawBins = new long[129];              // 0..127, 128=overflow
					ClampedNormBins = new long[101];      // 0..100 (0.00..1.00)
					UnclampedRatioBins = new long[82];    // 0..80 (0.00..4.00), 81=overflow
				}

				public void Reset()
				{
					Array.Clear(RawBins, 0, RawBins.Length);
					Array.Clear(ClampedNormBins, 0, ClampedNormBins.Length);
					Array.Clear(UnclampedRatioBins, 0, UnclampedRatioBins.Length);
					Sampled = 0;
					Clamped = 0;
					Zero = 0;
				}
			}

			private static readonly ScaleStats _s8 = new ScaleStats(8);
			private static readonly ScaleStats _s16 = new ScaleStats(16);
			private static readonly ScaleStats _sOther = new ScaleStats(0);

			public sealed class Snapshot
			{
				public int SampleModPow2;
				public bool ClampDisabled;
				public ScaleSnapshot Scale8;
				public ScaleSnapshot Scale16;
				public ScaleSnapshot Other;

				public string ToJson()
				{
					var opts = new System.Text.Json.JsonSerializerOptions { IncludeFields = true }; return System.Text.Json.JsonSerializer.Serialize(this, opts);
				}
			}

			public sealed class ScaleSnapshot
			{
				public int Scale;
				public long Sampled;
				public long Clamped;
				public long Zero;
				public long[] RawBins;
				public long[] ClampedNormBins;
				public long[] UnclampedRatioBins;
			}

			/// <summary>
			/// Enables sampling-based collection for Levenshtein statistics.
			/// Important: this is intended for build diagnostics only (keep it off during search benchmarks).
			/// </summary>
			public static void BeginCollect(int sampleModPow2 = 4096)
			{
				lock (_lock)
				{
					_sampleModPow2 = NormalizePow2(sampleModPow2);
					_counter = 0;
					_s8.Reset();
					_s16.Reset();
					_sOther.Reset();
					_enabled = true;
				}
			}

			/// <summary>
			/// Takes a snapshot and disables collection (also resets internal counters).
			/// </summary>
			public static Snapshot EndCollect()
			{
				lock (_lock)
				{
					var snap = new Snapshot
					{
						SampleModPow2 = _sampleModPow2,
						ClampDisabled = DisableLevClamp,
						Scale8 = Copy(_s8),
						Scale16 = Copy(_s16),
						Other = Copy(_sOther),
					};

					_enabled = false;
					_counter = 0;
					_s8.Reset();
					_s16.Reset();
					_sOther.Reset();
					return snap;
				}
			}

			private static ScaleSnapshot Copy(ScaleStats s)
			{
				return new ScaleSnapshot
				{
					Scale = s.Scale,
					Sampled = s.Sampled,
					Clamped = s.Clamped,
					Zero = s.Zero,
					RawBins = (long[])s.RawBins.Clone(),
					ClampedNormBins = (long[])s.ClampedNormBins.Clone(),
					UnclampedRatioBins = (long[])s.UnclampedRatioBins.Clone(),
				};
			}

			private static int NormalizePow2(int x)
			{
				if (x <= 1) return 1;
				// round up to power of two
				int p = 1;
				while (p < x) p <<= 1;
				return p;
			}

			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			private static ScaleStats GetScaleStats(int scale)
			{
				if (scale == 8) return _s8;
				if (scale == 16) return _s16;
				return _sOther;
			}

			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			internal static void RecordSample(int dRaw, int scale, int dClamped)
			{
				if (!_enabled) return;

				long c = System.Threading.Interlocked.Increment(ref _counter);
				if ((c & (_sampleModPow2 - 1)) != 0) return;

				var s = GetScaleStats(scale);
				System.Threading.Interlocked.Increment(ref s.Sampled);

				if (dRaw == 0) System.Threading.Interlocked.Increment(ref s.Zero);
				if (dRaw > scale) System.Threading.Interlocked.Increment(ref s.Clamped);

				// raw distance bin
				int rawBin = dRaw;
				if (rawBin < 0) rawBin = 0;
				if (rawBin >= 128) rawBin = 128;
				System.Threading.Interlocked.Increment(ref s.RawBins[rawBin]);

				// clamped normalized in [0..1]
				double clampedNorm = (double)dClamped / (double)scale;
				int cnBin = (int)Math.Round(clampedNorm * 100.0);
				if (cnBin < 0) cnBin = 0;
				if (cnBin > 100) cnBin = 100;
				System.Threading.Interlocked.Increment(ref s.ClampedNormBins[cnBin]);

				// unclamped ratio in [0..4] step 0.05 (+overflow)
				double ratio = (double)dRaw / (double)scale;
				int rBin = (int)Math.Round(ratio / 0.05);
				if (rBin < 0) rBin = 0;
				if (rBin > 81) rBin = 81;
				System.Threading.Interlocked.Increment(ref s.UnclampedRatioBins[rBin]);
			}
		}


		public static class AnchorStats
		{
			private static readonly object _lock = new object();
			private static readonly object _sigLock = new object();

			private static volatile bool _enabled;
			private static int _sampleModPow2 = 4096; // power-of-two sampling modulus
			private static long _counter;

			// Histograms: bins for [0..1] with step 0.01
			private static readonly long[] _name = new long[101];
			private static readonly long[] _args = new long[101];
			private static readonly long[] _ret = new long[101];
			private static readonly long[] _recv = new long[101];
			private static readonly long[] _neigh = new long[101];
			private static readonly long[] _total = new long[101];

			// Weighted contribution histograms in absolute [0..1] space: round((w*comp)*100)
			private static readonly long[] _nameW = new long[101];
			private static readonly long[] _argsW = new long[101];
			private static readonly long[] _retW = new long[101];
			private static readonly long[] _recvW = new long[101];
			private static readonly long[] _neighW = new long[101];

			private static long _sampled;
			private static WeightsSnapshot _weightsSnap;


			// Top repeated "signatures" (bins) among sampled pairs
			// Key packs 5 bins (0..100) into 35 bits (7 bits each).
			private static readonly Dictionary<long, int> _sig = new Dictionary<long, int>(capacity: 256);

			public sealed class Snapshot
			{
				public int SampleModPow2;
				public long Sampled;

				public WeightsSnapshot Weights;

				public long[] NameBins;
				public long[] ArgsBins;
				public long[] RetBins;
				public long[] RecvBins;
				public long[] NeighBins;
				public long[] TotalBins;

				public long[] NameWBins;
				public long[] ArgsWBins;
				public long[] RetWBins;
				public long[] RecvWBins;
				public long[] NeighWBins;

				public SignatureEntry[] TopSignatures;

				public string ToJson()
				{
					var opts = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
					return System.Text.Json.JsonSerializer.Serialize(this, opts);
				}
			}

			public sealed class WeightsSnapshot
			{
				public double NameW, ArgsW, ReturnsW, ParentW, NeighW;
				public int NameScale, ReturnsScale, ReceiverScale, TypeScale, ArgNameScale;
			}

			public sealed class SignatureEntry
			{
				public int Name, Args, Ret, Recv, Neigh;
				public int Total;
				public int Count;
			}

			public static void BeginCollect(int sampleModPow2 = 4096)
			{
				lock (_lock)
				{
					_sampleModPow2 = NormalizePow2(sampleModPow2);
					_counter = 0;
					_sampled = 0;
					_weightsSnap = null;
					Array.Clear(_name, 0, _name.Length);
					Array.Clear(_args, 0, _args.Length);
					Array.Clear(_ret, 0, _ret.Length);
					Array.Clear(_recv, 0, _recv.Length);
					Array.Clear(_neigh, 0, _neigh.Length);
					Array.Clear(_total, 0, _total.Length);

					Array.Clear(_nameW, 0, _nameW.Length);
					Array.Clear(_argsW, 0, _argsW.Length);
					Array.Clear(_retW, 0, _retW.Length);
					Array.Clear(_recvW, 0, _recvW.Length);
					Array.Clear(_neighW, 0, _neighW.Length);

					lock (_sigLock) _sig.Clear();

					_enabled = true;
				}
			}

			public static Snapshot EndCollect()
			{
				lock (_lock)
				{
					var top = BuildTopSignatures();

					var snap = new Snapshot
					{
						SampleModPow2 = _sampleModPow2,
						Sampled = _sampled,
						Weights = _weightsSnap,

						NameBins = (long[])_name.Clone(),
						ArgsBins = (long[])_args.Clone(),
						RetBins = (long[])_ret.Clone(),
						RecvBins = (long[])_recv.Clone(),
						NeighBins = (long[])_neigh.Clone(),
						TotalBins = (long[])_total.Clone(),

						NameWBins = (long[])_nameW.Clone(),
						ArgsWBins = (long[])_argsW.Clone(),
						RetWBins = (long[])_retW.Clone(),
						RecvWBins = (long[])_recvW.Clone(),
						NeighWBins = (long[])_neighW.Clone(),

						TopSignatures = top,
					};

					_enabled = false;
					_counter = 0;
					_sampled = 0;
					_weightsSnap = null;
					Array.Clear(_name, 0, _name.Length);
					Array.Clear(_args, 0, _args.Length);
					Array.Clear(_ret, 0, _ret.Length);
					Array.Clear(_recv, 0, _recv.Length);
					Array.Clear(_neigh, 0, _neigh.Length);
					Array.Clear(_total, 0, _total.Length);

					Array.Clear(_nameW, 0, _nameW.Length);
					Array.Clear(_argsW, 0, _argsW.Length);
					Array.Clear(_retW, 0, _retW.Length);
					Array.Clear(_recvW, 0, _recvW.Length);
					Array.Clear(_neighW, 0, _neighW.Length);

					lock (_sigLock) _sig.Clear();

					return snap;
				}
			}


			/// <summary>
			/// Returns the last captured weights snapshot as a concrete Weights instance.
			/// Useful for build-time diagnostics inside VP-tree nodes (e.g. when eq_ratio is huge).
			/// Note: returns false if no sampled AnchorDistance call has happened yet.
			/// </summary>
			public static bool TryGetCapturedWeights(out Weights w)
			{
				var ws = _weightsSnap;
				if (ws == null)
				{
					w = null;
					return false;
				}

				w = new Weights
				{
					NameW = ws.NameW,
					ArgsW = ws.ArgsW,
					ReturnsW = ws.ReturnsW,
					ParentW = ws.ParentW,
					NeighW = ws.NeighW,

					NameScale = ws.NameScale,
					ReturnsScale = ws.ReturnsScale,
					ReceiverScale = ws.ReceiverScale,
					TypeScale = ws.TypeScale,
					ArgNameScale = ws.ArgNameScale,
				};

				return true;
			}


			private static SignatureEntry[] BuildTopSignatures()
			{
				KeyValuePair<long, int>[] entries;
				lock (_sigLock)
				{
					entries = _sig.ToArray();
				}
				if (entries.Length == 0) return Array.Empty<SignatureEntry>();

				Array.Sort(entries, (a, b) => b.Value.CompareTo(a.Value));

				int take = entries.Length < 20 ? entries.Length : 20;
				var res = new SignatureEntry[take];
				for (int i = 0; i < take; i++)
				{
					long key = entries[i].Key;
					int name = (int)(key & 0x7F); key >>= 7;
					int args = (int)(key & 0x7F); key >>= 7;
					int ret = (int)(key & 0x7F); key >>= 7;
					int recv = (int)(key & 0x7F); key >>= 7;
					int neigh = (int)(key & 0x7F); key >>= 7;
					int total = (int)(key & 0x7F);

					res[i] = new SignatureEntry
					{
						Name = name,
						Args = args,
						Ret = ret,
						Recv = recv,
						Neigh = neigh,
						Total = total,
						Count = entries[i].Value
					};
				}
				return res;
			}

			private static int NormalizePow2(int x)
			{
				if (x <= 1) return 1;
				int p = 1;
				while (p < x) p <<= 1;
				return p;
			}

			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			private static int Bin01(double x)
			{
				if (double.IsNaN(x) || x < 0) return 0;
				if (x > 1) return 100;
				int b = (int)Math.Round(x * 100.0);
				if (b < 0) return 0;
				if (b > 100) return 100;
				return b;
			}

			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			private static void Inc(long[] bins, int b)
			{
				System.Threading.Interlocked.Increment(ref bins[b]);
			}

			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			internal static void RecordSample(double dName, double dArgs, double dRet, double dRecv, double dNeigh, double total, Weights w)
			{
				if (!_enabled) return;

				long c = System.Threading.Interlocked.Increment(ref _counter);
				if ((c & (_sampleModPow2 - 1)) != 0) return;

				System.Threading.Interlocked.Increment(ref _sampled);

				int bName = Bin01(dName);
				int bArgs = Bin01(dArgs);
				int bRet = Bin01(dRet);
				int bRecv = Bin01(dRecv);
				int bNeigh = Bin01(dNeigh);
				int bTotal = Bin01(total);

				Inc(_name, bName);
				Inc(_args, bArgs);
				Inc(_ret, bRet);
				Inc(_recv, bRecv);
				Inc(_neigh, bNeigh);
				Inc(_total, bTotal);

				if (w != null)
				{
					if (_weightsSnap == null)
					{
						lock (_lock)
						{
							if (_weightsSnap == null)
							{
								_weightsSnap = new WeightsSnapshot
								{
									NameW = w.NameW,
									ArgsW = w.ArgsW,
									ReturnsW = w.ReturnsW,
									ParentW = w.ParentW,
									NeighW = w.NeighW,
									NameScale = w.NameScale,
									ReturnsScale = w.ReturnsScale,
									ReceiverScale = w.ReceiverScale,
									TypeScale = w.TypeScale,
									ArgNameScale = w.ArgNameScale,
								};
							}
						}
					}

					Inc(_nameW, Bin01(w.NameW * dName));
					Inc(_argsW, Bin01(w.ArgsW * dArgs));
					Inc(_retW, Bin01(w.ReturnsW * dRet));
					Inc(_recvW, Bin01(w.ParentW * dRecv));
					Inc(_neighW, Bin01(w.NeighW * dNeigh));
				}

				// Update signature counts (binned components)
				long key = (long)bName
					 | ((long)bArgs << 7)
					 | ((long)bRet << 14)
					 | ((long)bRecv << 21)
					 | ((long)bNeigh << 28)
						 | ((long)bTotal << 35);

				lock (_sigLock)
				{
					if (_sig.TryGetValue(key, out int v)) _sig[key] = v + 1;
					else _sig[key] = 1;
				}
			}
		}

		/// <summary>
		/// Debug knob: if true, LevScaled will NOT clamp by "scale" (it will return d/scale, which may exceed 1).
		/// WARNING: this changes the meaning/range of distances and may require re-tuning weights (especially ArgsDistance/Hungarian).
		/// </summary>
		public static volatile bool DisableLevClamp = false;

		/// <summary>
		/// If true, LevScaled uses a multi-scale soft clamp:
		///  - "fine" cap = scale
		///  - "coarse" cap = scale * LevCoarseMul
		/// and returns: LevFineWeight * fine + (1-LevFineWeight) * coarse.
		/// This keeps the output in [0..1] and preserves metric properties (as a convex combination of capped metrics),
		/// but drastically reduces tie frequency when raw edit distance often exceeds the fine cap.
		/// </summary>
		public static volatile bool UseLevMultiScale = true;

		/// <summary>Weight of the fine-capped part in multi-scale mode (0..1).</summary>
		public static double LevFineWeight = 0.90;

		/// <summary>Multiplier for the coarse cap in multi-scale mode (coarseCap = scale * LevCoarseMul).</summary>
		public static volatile int LevCoarseMul = 4;

		// Truncated + scaled Levenshtein keeps metric properties
		public static double LevScaled(string a, string b, int scale)
		{
			if (scale <= 0) scale = 1;

			int dRaw = Lev(a, b);

			// Legacy debug knob: returns d/scale (may exceed 1). Keep this for debugging only.
			if (DisableLevClamp)
			{
				LevStats.RecordSample(dRaw, scale, dRaw);
				return (double)dRaw / (double)scale;
			}

			int dFine = dRaw;
			if (dFine > scale) dFine = scale;

			LevStats.RecordSample(dRaw, scale, dFine);

			if (!UseLevMultiScale)
				return (double)dFine / (double)scale;

			int mul = LevCoarseMul;
			if (mul < 2) mul = 2;
			int coarseCap = scale * mul;
			if (coarseCap < scale) coarseCap = scale; // overflow safety

			int dCoarse = dRaw;
			if (dCoarse > coarseCap) dCoarse = coarseCap;

			double fine = (double)dFine / (double)scale;
			double coarse = (double)dCoarse / (double)coarseCap;

			double aW = LevFineWeight;
			if (double.IsNaN(aW)) aW = 0.90;
			if (aW < 0.0) aW = 0.0;
			if (aW > 1.0) aW = 1.0;

			return aW * fine + (1.0 - aW) * coarse;
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
			// Backward-compatible: use the *fine* key by default.
			return NeighborSigKeyFine(m);
		}

		public static string NeighborSigKeyFine(MethodAnchor m)
		{
			// More discriminative key for contextual neighbor bag:
			//  - normalized method name
			//  - return type
			//  - multiset of argument types (sorted)
			var sb = new StringBuilder(256);

			sb.Append(m.MethodNameNorm ?? string.Empty);
			sb.Append('|').Append(m.ReturnTypeNorm ?? string.Empty).Append('|');

			int argc = m.Args?.Count ?? 0;
			if (argc > 0)
			{
				var types = new List<string>(argc);
				for (int i = 0; i < argc; i++) types.Add(m.Args[i].TypeNorm ?? string.Empty);
				types.Sort(StringComparer.Ordinal);
				for (int i = 0; i < types.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(types[i]);
				}
			}

			return sb.ToString();
		}

		public static string NeighborSigKeyCoarse(MethodAnchor m)
		{
			// Coarser key (keeps overlap across different method names):
			//  - return type
			//  - multiset of argument types (sorted)
			var sb = new StringBuilder(192);

			sb.Append(m.ReturnTypeNorm ?? string.Empty).Append('|');

			int argc = m.Args?.Count ?? 0;
			if (argc > 0)
			{
				var types = new List<string>(argc);
				for (int i = 0; i < argc; i++) types.Add(m.Args[i].TypeNorm ?? string.Empty);
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


		/// <summary>
		/// Same components as AnchorDistance, but returns the parts via out-params.
		/// Does NOT record into AnchorStats (so you can use it for node-local diagnostics without skewing global stats).
		/// </summary>
		public static void AnchorDistanceComponents(
			MethodAnchor a,
			MethodAnchor b,
			Weights w,
			out double dName,
			out double dArgs,
			out double dRet,
			out double dRecv,
			out double dNeigh,
			out double total)
		{
			dName = LevScaled(a?.MethodNameNorm ?? "", b?.MethodNameNorm ?? "", w.NameScale);
			dArgs = ArgsDistance(a?.Args, b?.Args, w);
			dRet = LevScaled(a?.ReturnTypeNorm ?? "", b?.ReturnTypeNorm ?? "", w.ReturnsScale);
			dRecv = LevScaled(a?.ParentNameNorm ?? "", b?.ParentNameNorm ?? "", w.ReceiverScale);
			dNeigh = WeightedJaccard(a?.NeighborBag, b?.NeighborBag);

			total =
			     w.NameW * dName
			     + w.ArgsW * dArgs
			     + w.ReturnsW * dRet
			     + w.ParentW * dRecv
			     + w.NeighW * dNeigh;
		}


		public static double AnchorDistance(MethodAnchor a, MethodAnchor b, Weights w)
		{
			double dName = 0, dArgs = 0, dRet = 0, dRecv = 0, dNeigh = 0;


			dName = LevScaled(a?.MethodNameNorm ?? "", b?.MethodNameNorm ?? "", w.NameScale);
			dArgs = ArgsDistance(a?.Args, b?.Args, w);
			dRet = LevScaled(a?.ReturnTypeNorm ?? "", b?.ReturnTypeNorm ?? "", w.ReturnsScale);
			dRecv = LevScaled(a?.ParentNameNorm ?? "", b?.ParentNameNorm ?? "", w.ReceiverScale);
			dNeigh = WeightedJaccard(a?.NeighborBag, b?.NeighborBag);


			double total =
			     w.NameW * dName
			     + w.ArgsW * dArgs
			     + w.ReturnsW * dRet
			     + w.ParentW * dRecv
			     + w.NeighW * dNeigh;

			AnchorStats.RecordSample(dName, dArgs, dRet, dRecv, dNeigh, total, w);

			return total;
		}
	}
}
