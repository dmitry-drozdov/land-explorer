using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenTracing;
using OpenTracing.Util;


namespace VPTree
{
	// =======================
	// VP-Tree (exact kNN)
	// =======================
	public sealed partial class VPTree<T>
	{
		private sealed class Node
		{
			public int Index;
			public double Threshold;
			public Node Left;
			public Node Right;
		}

		private readonly List<T> _items;
		private readonly Func<T, T, double> _dist;
		private readonly Node _root;
		private readonly Random _rng;

		// Query-time normalization: if a query MethodAnchor has an empty NeighborBag,
		// try to copy the bag from an identical signature already present in the index.
		// This prevents a systematic +NeighW penalty (e.g., +0.1) for unchanged points.
		private Dictionary<string, MethodAnchor> _sigIndex;
		private int _sigIndexDuplicates;
		private int _queryNeighborInjected;
		private int _queryNeighborMiss;

		public VPTree(IList<T> items, Func<T, T, double> distance, int? seed = null, ITracer tracer = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			var idxs = Enumerable.Range(0, _items.Count).ToList();
			using (var scope = tracer?.BuildSpan("Build").StartActive())
			{
				Dist.LevStats.BeginCollect(sampleModPow2: 4096);
				Dist.AnchorStats.BeginCollect(sampleModPow2: 4096);
				MaybeBuildNeighborBags(tracer);
				EnsureSigIndexBuilt();
				_root = Build(idxs, tracer);
				var levSnap = Dist.LevStats.EndCollect();
				var anchorSnap = Dist.AnchorStats.EndCollect();
				if (scope != null)
				{
					scope.Span.SetTag("vptree.lev.sample_mod", levSnap.SampleModPow2);
					scope.Span.SetTag("vptree.lev.clamp_disabled", levSnap.ClampDisabled);
					scope.Span.SetTag("vptree.lev.sampled.scale8", levSnap.Scale8?.Sampled ?? 0);
					scope.Span.SetTag("vptree.lev.sampled.scale16", levSnap.Scale16?.Sampled ?? 0);
					// Put full histograms into logs (strings), so tags don't get truncated.
					scope.Span.Log(new[]
					{
						new KeyValuePair<string, object>("event", "levstats"),
						new KeyValuePair<string, object>("levstats.json", levSnap.ToJson()),
					});


					// AnchorDistance component stats (histograms + top signatures)
					scope.Span.SetTag("vptree.anchor.sample_mod", anchorSnap.SampleModPow2);
					scope.Span.SetTag("vptree.anchor.sampled", anchorSnap.Sampled);
					scope.Span.Log(new[]
					{
						new KeyValuePair<string, object>("event", "anchordiststats"),
						new KeyValuePair<string, object>("anchordiststats.json", anchorSnap.ToJson()),
					});

					// Signature index stats (used to normalize queries without NeighborBag)
					scope.Span.SetTag("vptree.sigindex.size", _sigIndex?.Count ?? 0);
					scope.Span.SetTag("vptree.sigindex.duplicates", _sigIndexDuplicates);

					// Overall build stats
					scope.Span.SetTag("vptree.n", _items.Count);
					scope.Span.SetTag("vptree.build.nodes", _buildNodes);
					scope.Span.SetTag("vptree.build.oneside", _buildOneSideEmpty);
					scope.Span.SetTag("vptree.build.large_eq", _buildLargeEq);
					scope.Span.SetTag("vptree.build.max_eq_ratio", _buildMaxEqRatio);
					scope.Span.SetTag("vptree.build.max_eq_n", _buildMaxEqN);
					scope.Span.SetTag("vptree.build.max_eq_depth", _buildMaxEqDepth);
					scope.Span.SetTag("vptree.build.max_imbalance_ratio", _buildMaxImbalanceRatio);
					scope.Span.SetTag("vptree.build.max_imbalance_n", _buildMaxImbalanceN);
					scope.Span.SetTag("vptree.build.max_imbalance_depth", _buildMaxImbalanceDepth);
					scope.Span.SetTag("vptree.eqdiag.used", _eqDiagUsed);

					scope.Span.SetTag("vptree.build.eq_total", _buildEqTotal);
					scope.Span.SetTag("vptree.build.compared_total", _buildComparedTotal);
					scope.Span.SetTag("vptree.build.eq_total_ratio", _buildComparedTotal > 0 ? (double)_buildEqTotal / (double)_buildComparedTotal : 0.0);
					scope.Span.SetTag("vptree.build.max_equal", _buildMaxEqual);
					scope.Span.SetTag("vptree.build.max_equal_n", _buildMaxEqualN);
					scope.Span.SetTag("vptree.build.max_equal_depth", _buildMaxEqualDepth);



					int depth = ComputeDepth(_root);
					scope.Span.SetTag("vptree.build.depth", depth);
				}
			}
		}

		private readonly object _rngLock = new object();

		// Build diagnostics (cheap counters to understand balance issues)
		private readonly object _buildStatsLock = new object();
		private int _dbgSpanUsed;
		private const int _dbgSpanBudget = 64;

// Node-local diagnostics for huge tie buckets (eq_ratio close to 1.0)
private int _eqDiagUsed;
private const int _eqDiagBudget = 24; // keep small: each diag computes component distances


		private long _buildNodes;
		private long _buildOneSideEmpty;
		private long _buildLargeEq;
		private double _buildMaxEqRatio;
		private int _buildMaxEqN;
		private int _buildMaxEqDepth;
		private double _buildMaxImbalanceRatio;
		private int _buildMaxImbalanceN;
		private int _buildMaxImbalanceDepth;

		private long _buildEqTotal;            // sum of 'equal' over all partitions
		private long _buildComparedTotal;      // sum of 'n' over all partitions
		private int _buildMaxEqual;            // max 'equal' observed (filtered)
		private int _buildMaxEqualN;           // corresponding n
		private int _buildMaxEqualDepth;       // corresponding depth



		/// <summary>
		/// If the tree is built over MethodAnchor and NeighborBag is mostly empty, build simple neighbor bags
		/// so the Neigh component can participate in the metric. This is a no-op if bags look already built.
		/// </summary>
		private void MaybeBuildNeighborBags(ITracer tracer)
		{
			// Only meaningful if T is MethodAnchor (or subtype).
			int inspected = 0;
			int preNonEmpty = 0;

			for (int i = 0; i < _items.Count; i++)
			{
				if (!(_items[i] is MethodAnchor ma)) continue;
				inspected++;
				if (ma.NeighborBag != null && ma.NeighborBag.Count > 0) preNonEmpty++;
			}

			if (inspected == 0) return;

			// If >=10% are already non-empty, assume caller already built bags.
			if (preNonEmpty * 10 >= inspected) return;

			// Build bags for all anchors we can see.
			var anchors = new List<MethodAnchor>(inspected);
			for (int i = 0; i < _items.Count; i++)
			{
				if (_items[i] is MethodAnchor ma) anchors.Add(ma);
			}

			using (var scope = tracer?.BuildSpan("Anchors.BuildNeighborBags").StartActive())
			{
				AnchorsIO.BuildNeighborBags(anchors, window: 4);

				// Post stats
				int postNonEmpty = 0;
				int totalSize = 0;
				int maxSize = 0;
				for (int i = 0; i < anchors.Count; i++)
				{
					var bag = anchors[i].NeighborBag;
					int sz = bag?.Count ?? 0;
					if (sz > 0) postNonEmpty++;
					totalSize += sz;
					if (sz > maxSize) maxSize = sz;
				}

				if (scope != null)
				{
					scope.Span.SetTag("neighbors.inspected", inspected);
					scope.Span.SetTag("neighbors.pre_nonempty", preNonEmpty);
					scope.Span.SetTag("neighbors.post_nonempty", postNonEmpty);
					scope.Span.SetTag("neighbors.avg_size", anchors.Count == 0 ? 0.0 : (double)totalSize / anchors.Count);
					scope.Span.SetTag("neighbors.max_size", maxSize);
				}
			}
			}

		/// <summary>
		/// Builds a lookup from method "signature" (receiver + name + return + args) to an existing anchor.
		/// Used to normalize query anchors that do not carry NeighborBag (otherwise Neigh component adds a constant penalty).
		/// </summary>
		private void EnsureSigIndexBuilt()
		{
			if (_sigIndex != null) return;

			var dict = new Dictionary<string, MethodAnchor>(StringComparer.Ordinal);
			int dups = 0;
			for (int i = 0; i < _items.Count; i++)
			{
				if (!(_items[i] is MethodAnchor ma)) continue;
				string key = SigKey(ma);
				if (dict.TryGetValue(key, out var existing))
				{
					dups++;
					// Prefer a non-empty NeighborBag exemplar if we have one.
					bool exEmpty = existing?.NeighborBag == null || existing.NeighborBag.Count == 0;
					bool maEmpty = ma?.NeighborBag == null || ma.NeighborBag.Count == 0;
					if (exEmpty && !maEmpty) dict[key] = ma;
				}
				else
				{
					dict[key] = ma;
				}
			}

			_sigIndex = dict;
			_sigIndexDuplicates = dups;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void PrepareQueryIfNeeded(T query)
		{
			if (!(query is MethodAnchor q)) return;
			if (q.NeighborBag != null && q.NeighborBag.Count > 0) return;

			EnsureSigIndexBuilt();
			if (_sigIndex == null) return;

			string key = SigKey(q);
			if (_sigIndex.TryGetValue(key, out var src) && src != null && src.NeighborBag != null && src.NeighborBag.Count > 0)
			{
				// Copy, so external code can't accidentally mutate the tree's exemplar bag.
				q.NeighborBag = new Dictionary<string, double>(src.NeighborBag);
				System.Threading.Interlocked.Increment(ref _queryNeighborInjected);
			}
			else
			{
				System.Threading.Interlocked.Increment(ref _queryNeighborMiss);
			}
		}

			private Node Build(List<int> idxs, ITracer tracer = null)
		{
			return BuildInternal(idxs, parallelDepth: 0, tracer: tracer);
		}

		private static int ComputeDepth(Node node)
		{
			if (node == null) return 0;
			int ld = ComputeDepth(node.Left);
			int rd = ComputeDepth(node.Right);
			return 1 + (ld > rd ? ld : rd);
		}

		
private static int Bin01(double v)
{
	if (double.IsNaN(v) || v <= 0) return 0;
	if (v >= 1) return 100;
	return (int)Math.Round(v * 100.0);
}

private static List<BinCount> CompressBins(int[] bins)
{
	var res = new List<BinCount>();
	for (int i = 0; i < bins.Length; i++)
	{
		int c = bins[i];
		if (c != 0) res.Add(new BinCount { Bin = i, Count = c });
	}
	return res;
}

private sealed class BinCount { public int Bin; public int Count; }


// --- helpers for node-local diagnostics ---
private static string ArgsKey(MethodAnchor m)
{
	if (m == null || m.Args == null || m.Args.Count == 0) return "";
	// keep order; names are already normalized
	var sb = new StringBuilder(m.Args.Count * 16);
	for (int i = 0; i < m.Args.Count; i++)
	{
		var a = m.Args[i];
		if (i > 0) sb.Append('|');
		sb.Append(a?.TypeNorm ?? "").Append(':').Append(a?.NameNorm ?? "");
	}
	return sb.ToString();
}

private static string SigKey(MethodAnchor m)
{
	if (m == null) return "";
	// Receiver + name + return + args: should uniquely identify a method in most codebases
	return (m.ParentNameNorm ?? "") + "|" + (m.MethodNameNorm ?? "") + "|" + (m.ReturnTypeNorm ?? "") + "|" + ArgsKey(m);
}


private static bool NeighborBagIsEmpty(MethodAnchor m)
{
	return m?.NeighborBag == null || m.NeighborBag.Count == 0;
}

private static bool NeighborBagEquals(MethodAnchor a, MethodAnchor b)
{
	if (NeighborBagIsEmpty(a) && NeighborBagIsEmpty(b)) return true;
	if (a?.NeighborBag == null || b?.NeighborBag == null) return false;
	if (a.NeighborBag.Count != b.NeighborBag.Count) return false;
	foreach (var kv in a.NeighborBag)
	{
		if (!b.NeighborBag.TryGetValue(kv.Key, out var bv)) return false;
		// exact compare is fine for diagnostics; if you use floats, consider epsilon later
		if (kv.Value != bv) return false;
	}
	return true;
}

private static int MedianInt(List<int> xs)
{
	if (xs == null || xs.Count == 0) return 0;
	xs.Sort();
	return xs[xs.Count / 2];
}
private sealed class SigEntry
{
	public int Name, Args, Ret, Recv, Neigh, Total;
	public int Count;
}


private sealed class VpFeatureSnapshot
{
	public int NameLen;
	public int ParentLen;
	public int ReturnLen;
	public int ArgsCount;
	public int NeighCount;

	public bool NameEmpty;
	public bool ParentEmpty;
	public bool ReturnEmpty;
	public bool NeighEmpty;
}

private sealed class EqFeatureSnapshot
{
	public int Sampled;

	public int ParentEmpty;
	public int ParentEqVp;
	public int ParentRawGteScale;

	public int ReturnEmpty;
	public int ReturnEqVp;
	public int ReturnRawGteScale;

	public int ArgsBothEmpty;
	public int ArgsEqVp;

	public int NeighBothEmpty;
	public int NeighEqVp;

	public int NameRawMin;
	public int NameRawMed;
	public int NameRawMax;
	public int NameRawGteScale;
}
private sealed class NodeEqDiagSnapshot
{
	public int Depth;
	public int N;
	public int VpIndex;
	public double Mu;

	public int Less, Equal, Greater;
	public int Left, Right;
	public double EqRatio;
	public double ImbalanceRatio;

	public int EqBucketSize;
	public int EqSampled;

	
	public VpFeatureSnapshot Vp;
	public EqFeatureSnapshot Eq;
// Non-zero bins only (0..100)
	public List<BinCount> Name;
	public List<BinCount> Args;
	public List<BinCount> Ret;
	public List<BinCount> Recv;
	public List<BinCount> Neigh;
	public List<BinCount> Total;

	public List<BinCount> NameW;
	public List<BinCount> ArgsW;
	public List<BinCount> RetW;
	public List<BinCount> RecvW;
	public List<BinCount> NeighW;

	public SigEntry[] TopSignatures;
}

private Node BuildInternal(List<int> idxs, int parallelDepth, ITracer tracer)
		{
			// Wrapper kept to match the signature you care about
			return BuildInternal(idxs, parallelDepth, tracer, depth: 0);
		}

		private Node BuildInternal(List<int> idxs, int parallelDepth, ITracer tracer, int depth)
		{
			if (idxs.Count == 0)
				return null;

			Interlocked.Increment(ref _buildNodes);

			var node = new Node();

			int vpPos;
			lock (_rngLock)                       // Random не потокобезопасен
				vpPos = _rng.Next(idxs.Count);

			int vpIndex = idxs[vpPos];
			node.Index = vpIndex;

			if (idxs.Count == 1)
			{
				node.Left = null;
				node.Right = null;
				node.Threshold = 0;
				return node;
			}

			// move vp to end
			int last = idxs.Count - 1;
			int tmp = idxs[vpPos]; idxs[vpPos] = idxs[last]; idxs[last] = tmp;

			int n = idxs.Count - 1;
			var dists = new double[n];

			var opts = new ParallelOptions
			{
				MaxDegreeOfParallelism = 14 // стартовое значение для 16 логических
			};

			const int chunkSize = 16; // попробуй 64 и 128, выбери быстрее

			var vp = _items[vpIndex];
			var items = _items;
			var dist = _dist;
			Parallel.ForEach(Partitioner.Create(0, n, chunkSize), opts, range =>
			{
				for (int i = range.Item1; i < range.Item2; i++)
					dists[i] = dist(vp, items[idxs[i]]);
			});

			// median via quickselect (average O(n))
			double mu = QuickSelectMedian(dists);

			// Safety: NaN would completely break the tree/search.
			if (double.IsNaN(mu))
				mu = 0.0;

			node.Threshold = mu;

			// -----------------------------
			// IMPORTANT FIX (balance):
			// -----------------------------
			// Previous code used:
			//    if (dists[i] <= mu) left else right
			// With a quantized distance (many ties), "mu" is often the most frequent value,
			// and "<= mu" sends almost all points to LEFT, making RIGHT empty -> depth ~O(n).
			//
			// New code keeps threshold=mu but SPLITS the "== mu" bucket to keep ~n/2 on each side.
			var left = new List<int>(n / 2 + 1);
			var right = new List<int>(n / 2 + 1);
			List<int> eq = null;

			int less = 0, equal = 0, greater = 0;

			for (int i = 0; i < n; i++)
			{
				double d = dists[i];
				int id = idxs[i];

				if (d < mu) { left.Add(id); less++; }
				else if (d > mu) { right.Add(id); greater++; }
				else
				{
					(eq ??= new List<int>()).Add(id);
					equal++;
				}
			}

			int desiredLeft = n / 2;
			if (eq != null && eq.Count > 0)
			{
				int need = desiredLeft - left.Count; // how many from "==" we must move to left
				if (need < 0) need = 0;
				if (need > eq.Count) need = eq.Count;

				// First 'need' ==mu go to left, rest to right
				for (int i = 0; i < need; i++) left.Add(eq[i]);
				for (int i = need; i < eq.Count; i++) right.Add(eq[i]);
			}

			// Build-time balance diagnostics (cheap)
			int leftCount = left.Count;
			int rightCount = right.Count;

			if (leftCount == 0 || rightCount == 0)
				Interlocked.Increment(ref _buildOneSideEmpty);

			double eqRatio = n > 0 ? (double)equal / (double)n : 0.0;
			double imbalanceRatio = n > 0 ? (double)(leftCount > rightCount ? leftCount : rightCount) / (double)n : 0.0;

			// Track total amount of ties (d == mu)
			Interlocked.Add(ref _buildEqTotal, equal);
			Interlocked.Add(ref _buildComparedTotal, n);


			if (eqRatio >= 0.50)
				Interlocked.Increment(ref _buildLargeEq);

			// Track worst cases (for Jaeger tags on the "Build" span)
			lock (_buildStatsLock)
			{
				// Track biggest absolute tie bucket (ignore tiny n to avoid leaf noise)
				if (n >= 16 && equal > _buildMaxEqual)
				{
					_buildMaxEqual = equal;
					_buildMaxEqualN = n;
					_buildMaxEqualDepth = depth;
				}

				if (eqRatio > _buildMaxEqRatio)
				{
					_buildMaxEqRatio = eqRatio;
					_buildMaxEqN = n;
					_buildMaxEqDepth = depth;
				}

				if (imbalanceRatio > _buildMaxImbalanceRatio)
				{
					_buildMaxImbalanceRatio = imbalanceRatio;
					_buildMaxImbalanceN = n;
					_buildMaxImbalanceDepth = depth;
				}
			}

			
// ---------------------------------------------
// Node-local diagnostics for HUGE tie buckets
// ---------------------------------------------
// When eq_ratio is very high, it usually means our distance function is highly quantized
// (e.g., LevScaled with small scales + weighted sum), so many points land at exactly the same distance.
//
// To understand *why* ties happen, we decompose AnchorDistance into components
// ONLY for a small number of worst nodes (budgeted) and ONLY sampling from the "== mu" bucket.
bool shouldEqDiag =
	tracer != null &&
	eq != null &&
	eq.Count >= 64 &&
	eqRatio >= 0.90 &&
	Interlocked.Increment(ref _eqDiagUsed) <= _eqDiagBudget;

if (shouldEqDiag)
{
	// This diagnostic is specific to MethodAnchor + Dist.AnchorDistance.
	// If you're using a different metric for this VPTree instance, it will simply do nothing.
if (vp is MethodAnchor vpAnchor)
{
	// Prefer the exact weights used by AnchorDistance if they were captured by sampling.
	// Fallback to defaults to avoid missing diagnostics when sampling is too sparse.
	if (!Dist.AnchorStats.TryGetCapturedWeights(out var w) || w == null)
		w = new Dist.Weights();

		int eqBucketSize = eq.Count;
		int sampleCount = eqBucketSize < 128 ? eqBucketSize : 128;
		int step = eqBucketSize / sampleCount;
		if (step <= 0) step = 1;

		var nameBins = new int[101];
		var argsBins = new int[101];
		var retBins = new int[101];
		var recvBins = new int[101];
		var neighBins = new int[101];
		var totalBins = new int[101];

		var nameWBins = new int[101];
		var argsWBins = new int[101];
		var retWBins = new int[101];
		var recvWBins = new int[101];
		var neighWBins = new int[101];

		// Signature: 6 bins packed into 42 bits (6*7); key = name | (args<<7) | ... | (total<<35)
		var sig = new Dictionary<long, int>(capacity: 64);

			string vpArgsKey = ArgsKey(vpAnchor);
			bool vpNeighEmpty = NeighborBagIsEmpty(vpAnchor);
			int vpNeighCount = vpAnchor.NeighborBag != null ? vpAnchor.NeighborBag.Count : 0;

			var eqFeat = new EqFeatureSnapshot();
			var nameRaw = new List<int>(sampleCount);


		int taken = 0;
		for (int s = 0; s < eqBucketSize && taken < sampleCount; s += step)
			{
				int id = eq[s];
				if (!(items[id] is MethodAnchor otherAnchor))
				{
					continue;
				}

				// --- feature counters to understand why components collapse to 0 or 1 ---
			eqFeat.Sampled++;

			// Parent/receiver
			bool otherParentEmpty = string.IsNullOrEmpty(otherAnchor.ParentNameNorm);
			if (otherParentEmpty) eqFeat.ParentEmpty++;
			if ((vpAnchor.ParentNameNorm ?? "") == (otherAnchor.ParentNameNorm ?? "")) eqFeat.ParentEqVp++;
			// raw lev for parent (only if non-empty; empty-empty is trivially 0)
			if (!string.IsNullOrEmpty(vpAnchor.ParentNameNorm) || !otherParentEmpty)
			{
				int pr = Dist.Lev(vpAnchor.ParentNameNorm ?? "", otherAnchor.ParentNameNorm ?? "");
				if (pr >= w.ReceiverScale) eqFeat.ParentRawGteScale++;
			}

			// Return type
			bool otherRetEmpty = string.IsNullOrEmpty(otherAnchor.ReturnTypeNorm);
			if (otherRetEmpty) eqFeat.ReturnEmpty++;
			if ((vpAnchor.ReturnTypeNorm ?? "") == (otherAnchor.ReturnTypeNorm ?? "")) eqFeat.ReturnEqVp++;
			if (!string.IsNullOrEmpty(vpAnchor.ReturnTypeNorm) || !otherRetEmpty)
			{
				int rr = Dist.Lev(vpAnchor.ReturnTypeNorm ?? "", otherAnchor.ReturnTypeNorm ?? "");
				if (rr >= w.ReturnsScale) eqFeat.ReturnRawGteScale++;
			}

			// Args
			bool vpArgsEmpty = vpAnchor.Args == null || vpAnchor.Args.Count == 0;
			bool otherArgsEmpty = otherAnchor.Args == null || otherAnchor.Args.Count == 0;
			if (vpArgsEmpty && otherArgsEmpty) eqFeat.ArgsBothEmpty++;
			if (vpArgsKey == ArgsKey(otherAnchor)) eqFeat.ArgsEqVp++;

			// Neighbors
			bool otherNeighEmpty = NeighborBagIsEmpty(otherAnchor);
			if (vpNeighEmpty && otherNeighEmpty) eqFeat.NeighBothEmpty++;
			if (NeighborBagEquals(vpAnchor, otherAnchor)) eqFeat.NeighEqVp++;

			// Raw name lev
			int nr = Dist.Lev(vpAnchor.MethodNameNorm ?? "", otherAnchor.MethodNameNorm ?? "");
			nameRaw.Add(nr);
			if (nr >= w.NameScale) eqFeat.NameRawGteScale++;

			Dist.AnchorDistanceComponents(vpAnchor, otherAnchor, w,
				out double dName, out double dArgs, out double dRet, out double dRecv, out double dNeigh, out double total);

			int bn = Bin01(dName);
			int ba = Bin01(dArgs);
			int br = Bin01(dRet);
			int brecv = Bin01(dRecv);
			int bne = Bin01(dNeigh);
			int bt = Bin01(total);

			nameBins[bn]++; argsBins[ba]++; retBins[br]++; recvBins[brecv]++; neighBins[bne]++; totalBins[bt]++;

			nameWBins[Bin01(w.NameW * dName)]++;
			argsWBins[Bin01(w.ArgsW * dArgs)]++;
			retWBins[Bin01(w.ReturnsW * dRet)]++;
			recvWBins[Bin01(w.ParentW * dRecv)]++;
			neighWBins[Bin01(w.NeighW * dNeigh)]++;

			long key = (long)bn
				| ((long)ba << 7)
				| ((long)br << 14)
				| ((long)brecv << 21)
				| ((long)bne << 28)
				| ((long)bt << 35);

			sig.TryGetValue(key, out int c);
			sig[key] = c + 1;

			taken++;
		}

			if (nameRaw.Count > 0)
			{
				nameRaw.Sort();
				eqFeat.NameRawMin = nameRaw[0];
				eqFeat.NameRawMed = nameRaw[nameRaw.Count / 2];
				eqFeat.NameRawMax = nameRaw[nameRaw.Count - 1];
			}

		// Build top signatures (up to 12)
		var sigArr = sig.ToArray();
		Array.Sort(sigArr, (a, b) => b.Value.CompareTo(a.Value));
		int sigTake = sigArr.Length < 12 ? sigArr.Length : 12;
		var top = new SigEntry[sigTake];
		for (int i = 0; i < sigTake; i++)
		{
			long key = sigArr[i].Key;
			int bn = (int)(key & 0x7F); key >>= 7;
			int ba = (int)(key & 0x7F); key >>= 7;
			int br = (int)(key & 0x7F); key >>= 7;
			int brecv = (int)(key & 0x7F); key >>= 7;
			int bne = (int)(key & 0x7F); key >>= 7;
			int bt = (int)(key & 0x7F);

			top[i] = new SigEntry { Name = bn, Args = ba, Ret = br, Recv = brecv, Neigh = bne, Total = bt, Count = sigArr[i].Value };
		}

		
			var vpFeat = new VpFeatureSnapshot
			{
				NameLen = (vpAnchor.MethodNameNorm ?? "").Length,
				ParentLen = (vpAnchor.ParentNameNorm ?? "").Length,
				ReturnLen = (vpAnchor.ReturnTypeNorm ?? "").Length,
				ArgsCount = vpAnchor.Args != null ? vpAnchor.Args.Count : 0,
				NeighCount = vpNeighCount,
				NameEmpty = string.IsNullOrEmpty(vpAnchor.MethodNameNorm),
				ParentEmpty = string.IsNullOrEmpty(vpAnchor.ParentNameNorm),
				ReturnEmpty = string.IsNullOrEmpty(vpAnchor.ReturnTypeNorm),
				NeighEmpty = vpNeighEmpty,
			};
var snap = new NodeEqDiagSnapshot
		{
			Depth = depth,
			N = n,
			VpIndex = vpIndex,
			Mu = mu,
			Less = less,
			Equal = equal,
			Greater = greater,
			Left = leftCount,
			Right = rightCount,
			EqRatio = eqRatio,
			ImbalanceRatio = imbalanceRatio,
			EqBucketSize = eqBucketSize,
			EqSampled = taken,


			Vp = vpFeat,
			Eq = eqFeat,

			Name = CompressBins(nameBins),
			Args = CompressBins(argsBins),
			Ret = CompressBins(retBins),
			Recv = CompressBins(recvBins),
			Neigh = CompressBins(neighBins),
			Total = CompressBins(totalBins),

			NameW = CompressBins(nameWBins),
			ArgsW = CompressBins(argsWBins),
			RetW = CompressBins(retWBins),
			RecvW = CompressBins(recvWBins),
			NeighW = CompressBins(neighWBins),

			TopSignatures = top,
		};

		string json = System.Text.Json.JsonSerializer.Serialize(
			snap,
			new System.Text.Json.JsonSerializerOptions { IncludeFields = true });

		using (var scope = tracer.BuildSpan("VP.NodeEqDiag").StartActive())
		{
			scope.Span.SetTag("depth", depth);
			scope.Span.SetTag("n", n);
			scope.Span.SetTag("vpIndex", vpIndex);
			scope.Span.SetTag("mu", mu);
			scope.Span.SetTag("equal", equal);
			scope.Span.SetTag("eq_ratio", eqRatio);
			scope.Span.SetTag("eq_bucket", eqBucketSize);
			scope.Span.SetTag("sampled", taken);

			scope.Span.SetTag("vp.parent.empty", vpFeat.ParentEmpty);
			scope.Span.SetTag("vp.neigh.empty", vpFeat.NeighEmpty);
			scope.Span.SetTag("eq.parent.eq_vp", eqFeat.ParentEqVp);
			scope.Span.SetTag("eq.return.eq_vp", eqFeat.ReturnEqVp);
			scope.Span.SetTag("eq.args.eq_vp", eqFeat.ArgsEqVp);
			scope.Span.SetTag("eq.neigh.eq_vp", eqFeat.NeighEqVp);
			scope.Span.SetTag("eq.name.raw_gte_scale", eqFeat.NameRawGteScale);

			scope.Span.SetTag("eq.name.raw_min", eqFeat.NameRawMin);
			scope.Span.SetTag("eq.name.raw_med", eqFeat.NameRawMed);
			scope.Span.SetTag("eq.name.raw_max", eqFeat.NameRawMax);


			scope.Span.Log(new[]
			{
				new KeyValuePair<string, object>("event", "node_eq_diag"),
				new KeyValuePair<string, object>("node_eq_diag.json", json),
			});
		}
	}
}

// Optional per-node debug span (budgeted)
			bool shouldTraceNode =
				tracer != null &&
				(depth <= 2 || (imbalanceRatio >= 0.90) || (eqRatio >= 0.70) || leftCount == 0 || rightCount == 0);

			if (shouldTraceNode && Interlocked.Increment(ref _dbgSpanUsed) <= _dbgSpanBudget)
			{
				using (var scope = tracer.BuildSpan("VP.BuildNode").StartActive())
				{
					scope.Span.SetTag("depth", depth);
					scope.Span.SetTag("n", n);
					scope.Span.SetTag("vpIndex", vpIndex);
					scope.Span.SetTag("mu", mu);

					scope.Span.SetTag("less", less);
					scope.Span.SetTag("equal", equal);
					scope.Span.SetTag("greater", greater);

					scope.Span.SetTag("left", leftCount);
					scope.Span.SetTag("right", rightCount);

					scope.Span.SetTag("eq_ratio", eqRatio);
					scope.Span.SetTag("imbalance_ratio", imbalanceRatio);
				}
			}

			// Pараллелим ТОЛЬКО первые два уровня (parallelDepth > 0).
			if (parallelDepth > 0 && left.Count > 0 && right.Count > 0)
			{
				Node leftNode = null, rightNode = null;

				Parallel.Invoke(
				    () => leftNode = BuildInternal(left, parallelDepth - 1, tracer, depth + 1),
				    () => rightNode = BuildInternal(right, parallelDepth - 1, tracer, depth + 1)
				);

				node.Left = leftNode;
				node.Right = rightNode;
			}
			else
			{
				// Дальше – строго последовательно
				node.Left = left.Count > 0 ? BuildInternal(left, 0, tracer, depth + 1) : null;
				node.Right = right.Count > 0 ? BuildInternal(right, 0, tracer, depth + 1) : null;
			}

			return node;
		}

		private double QuickSelectMedian(double[] arr)
		{
			int n = arr.Length;
			if (n == 0) return 0.0;
			int k = n / 2;
			var a = new double[n];
			Array.Copy(arr, a, n);
			return QuickSelect(a, 0, n - 1, k);
		}

		private double QuickSelect(double[] a, int left, int right, int k)
		{
			while (true)
			{
				if (left == right) return a[left];

				double pivot = a[(left + right) >>> 1];

				int i = left, j = right;
				while (i <= j)
				{
					while (a[i] < pivot) i++;
					while (a[j] > pivot) j--;
					if (i <= j)
					{
						double t = a[i]; a[i] = a[j]; a[j] = t;
						i++; j--;
					}
				}

				if (k <= j) right = j;
				else if (k >= i) left = i;
				else return a[k];
			}
		}

		public sealed class KNNResult
		{
			public int Index;
			public double Dist;
			public KNNResult(int index, double dist) { Index = index; Dist = dist; }
		}

		public List<KNNResult> KNearest(T query, int k)
		{
			if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
			PrepareQueryIfNeeded(query);
			var heap = new MaxHeap(initialCapacity: k);
			double tau = double.PositiveInfinity;

			SearchKNN(_root, query, k, heap, ref tau);

			int n = heap.Count;
			var arr = new KNNResult[n];
			for (int i = n - 1; i >= 0; --i)
			{
				heap.Pop(out int idx, out double d);
				arr[i] = new KNNResult(idx, d); // заполняем с конца — уже по возрастанию
			}
			return new List<KNNResult>(arr);
		}

		private void SearchKNN(Node node, T query, int k, MaxHeap heap, ref double tau)
		{
			if (node == null) return;

			// локальные ссылки быстрее, чем поля класса в глубокой рекурсии
			var items = _items;
			var distFn = _dist;

			T vp = items[node.Index];
			double dist = distFn(query, vp);

			if (heap.Count < k)
			{
				heap.Push(node.Index, dist);
				if (heap.Count == k) tau = heap.PeekDist();
			}
			else if (dist < tau)
			{
				heap.ReplaceTop(node.Index, dist);
				tau = heap.PeekDist();
			}

			double mu = node.Threshold;

			// Выбираем «ближайшую» ветку (near) и «дальнюю» (far)
			Node near, far;
			if (dist < mu) { near = node.Left; far = node.Right; }
			else { near = node.Right; far = node.Left; }

			// Сначала обходим "near" — быстрее сузим tau
			if (near != null) SearchKNN(near, query, k, heap, ref tau);

			// Для "far" используем веточные условия (без Abs)
			if (far != null)
			{
				if (dist < mu)
				{
					// Был слева от порога: условие |dist - mu| <= tau эквивалентно dist + tau >= mu
					if (dist + tau >= mu) SearchKNN(far, query, k, heap, ref tau);
				}
				else
				{
					// Был справа: эквивалентно dist - tau <= mu
					if (dist - tau <= mu) SearchKNN(far, query, k, heap, ref tau);
				}
			}
		}

		// Max-heap by distance (largest on top)
		// Max-heap by distance (largest on top)
		private sealed class MaxHeap
		{
			private struct Item { public int Index; public double Dist; }

			private readonly List<Item> _a;

			public MaxHeap(int initialCapacity = 0)
			{
				_a = initialCapacity > 0 ? new List<Item>(initialCapacity) : new List<Item>();
			}

			public int Count => _a.Count;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public double PeekDist() => _a[0].Dist;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Push(int index, double dist)
			{
				_a.Add(new Item { Index = index, Dist = dist });
				SiftUp(_a.Count - 1);
			}

			public void Pop(out int index, out double dist)
			{
				var root = _a[0];
				int lastIdx = _a.Count - 1;
				var last = _a[lastIdx];
				_a.RemoveAt(lastIdx);
				if (_a.Count > 0) { _a[0] = last; SiftDown(0); }
				index = root.Index;
				dist = root.Dist;
			}

			public void ReplaceTop(int index, double dist)
			{
				_a[0] = new Item { Index = index, Dist = dist };
				SiftDown(0);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private void SiftUp(int i)
			{
				while (i > 0)
				{
					int p = (i - 1) / 2;
					if (Compare(_a[p], _a[i]) >= 0) break;
					var t = _a[p]; _a[p] = _a[i]; _a[i] = t;
					i = p;
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private void SiftDown(int i)
			{
				int n = _a.Count;
				while (true)
				{
					int l = 2 * i + 1, r = l + 1, best = i;
					if (l < n && Compare(_a[l], _a[best]) > 0) best = l;
					if (r < n && Compare(_a[r], _a[best]) > 0) best = r;
					if (best == i) break;
					var t = _a[i]; _a[i] = _a[best]; _a[best] = t;
					i = best;
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private static int Compare(Item a, Item b)
			{
				int c = a.Dist.CompareTo(b.Dist);
				return c != 0 ? c : a.Index.CompareTo(b.Index);
			}
		}
	}
}
