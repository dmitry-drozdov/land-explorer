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

		public VPTree(IList<T> items, Func<T, T, double> distance, int? seed = null, ITracer tracer = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			var idxs = Enumerable.Range(0, _items.Count).ToList();
			using (var scope = tracer?.BuildSpan("Build").StartActive())
			{
				MaybeBuildNeighborBags();
				EnsureSigIndexBuilt();
				_root = Build(idxs);
				if (scope != null)
				{
					int depth = ComputeDepth(_root);
					scope.Span.SetTag("vptree.build.depth", depth);
				}
			}
		}

		private readonly object _rngLock = new object();

		/// <summary>
		/// If the tree is built over MethodAnchor and NeighborBag is mostly empty, build simple neighbor bags
		/// so the Neigh component can participate in the metric. This is a no-op if bags look already built.
		/// </summary>
		/// <summary>
		/// If the tree is built over MethodAnchor and NeighborBag is mostly empty, build simple neighbor bags
		/// so the Neigh component can participate in the metric. This is a no-op if bags look already built.
		/// </summary>
		private void MaybeBuildNeighborBags()
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

			AnchorsIO.BuildNeighborBags(anchors, window: 4);
		}

		/// <summary>
		/// Builds a lookup from method "signature" (receiver + name + return + args) to an existing anchor.
		/// Used to normalize query anchors that do not carry NeighborBag (otherwise Neigh component adds a constant penalty).
		/// </summary>
		private void EnsureSigIndexBuilt()
		{
			if (_sigIndex != null) return;

			var dict = new Dictionary<string, MethodAnchor>(StringComparer.Ordinal);
			for (int i = 0; i < _items.Count; i++)
			{
				if (!(_items[i] is MethodAnchor ma)) continue;
				string key = SigKey(ma);
				if (dict.TryGetValue(key, out var existing))
				{
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
			}
		}

		private Node Build(List<int> idxs)
		{
			return BuildInternal(idxs, parallelDepth: 0);
		}

		private static int ComputeDepth(Node node)
		{
			if (node == null) return 0;
			int ld = ComputeDepth(node.Left);
			int rd = ComputeDepth(node.Right);
			return 1 + (ld > rd ? ld : rd);
		}

		
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



		private Node BuildInternal(List<int> idxs, int parallelDepth)
		{
			if (idxs.Count == 0)
				return null;

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

			for (int i = 0; i < n; i++)
			{
				double d = dists[i];
				int id = idxs[i];

				if (d < mu) left.Add(id);
				else if (d > mu) right.Add(id);
				else (eq ??= new List<int>()).Add(id);
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

			// Pараллелим ТОЛЬКО первые два уровня (parallelDepth > 0).
			if (parallelDepth > 0 && left.Count > 0 && right.Count > 0)
			{
				Node leftNode = null, rightNode = null;

				Parallel.Invoke(
				    () => leftNode = BuildInternal(left, parallelDepth - 1),
				    () => rightNode = BuildInternal(right, parallelDepth - 1)
				);

				node.Left = leftNode;
				node.Right = rightNode;
			}
			else
			{
				// Дальше – строго последовательно
				node.Left = left.Count > 0 ? BuildInternal(left, 0) : null;
				node.Right = right.Count > 0 ? BuildInternal(right, 0) : null;
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
