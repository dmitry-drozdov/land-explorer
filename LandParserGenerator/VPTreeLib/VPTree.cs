using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
		// -----------------------
		// Build profiling helpers
		// -----------------------
		private sealed class BuildStats
		{
			public long Nodes;
			public long DistanceCalls;
			public long DistanceTicks;
			public long DistanceParallelTrue;
			public long DistanceParallelFalse;
			public long QuickSelectTicks;
			public long PartitionTicks;
			public long VpSelectTicks;
			public long RecurseTicks;
			public int MaxDepth;
			public long MaxN;


			public long DegenerateSplits;
			public long MuIsOneSplits;
			public long MedianEqTotal;
			public int MedianEqMax;
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void ObserveNode(int depth, int n)
			{
				System.Threading.Interlocked.Increment(ref Nodes);
				if (depth > MaxDepth) MaxDepth = depth;
				if (n > MaxN) MaxN = n;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddDistance(int calls, long ticks)
			{
				System.Threading.Interlocked.Add(ref DistanceCalls, calls);
				System.Threading.Interlocked.Add(ref DistanceTicks, ticks);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddDistanceParallel(bool usedParallel)
			{
				if (usedParallel)
					System.Threading.Interlocked.Increment(ref DistanceParallelTrue);
				else
					System.Threading.Interlocked.Increment(ref DistanceParallelFalse);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddQuickSelect(long ticks) => System.Threading.Interlocked.Add(ref QuickSelectTicks, ticks);
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddPartition(long ticks) => System.Threading.Interlocked.Add(ref PartitionTicks, ticks);
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddVpSelect(long ticks) => System.Threading.Interlocked.Add(ref VpSelectTicks, ticks);
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddRecurse(long ticks) => System.Threading.Interlocked.Add(ref RecurseTicks, ticks);

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddDegenerateSplit(bool degenerate)
			{
				if (degenerate) System.Threading.Interlocked.Increment(ref DegenerateSplits);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddMuIsOne(bool isOne)
			{
				if (isOne) System.Threading.Interlocked.Increment(ref MuIsOneSplits);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void AddMedianEq(int eq)
			{
				System.Threading.Interlocked.Add(ref MedianEqTotal, eq);
				if (eq > MedianEqMax) MedianEqMax = eq;
			}
		}


		private const int TraceBuildMaxDepth = 2; // не раздуваем Jaeger тысячами спанов
							  // После оптимизаций distance() стала значительно дешевле, поэтому включать параллель слишком рано
							  // зачастую невыгодно из-за overhead планировщика.
		private const int ParallelDistanceThreshold = 4096;

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

		public VPTree(IList<T> items, Func<T, T, double> distance, int? seed = null, ITracer tracer = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			// На большом n критично минимизировать аллокации в билде.
			// Работаем с одним массивом индексов и передаём диапазоны (start/len),
			// чтобы не создавать List<int> на каждом узле.
			var idxs = Enumerable.Range(0, _items.Count).ToArray();

			var stats = new BuildStats();
			using (var scope = tracer?.BuildSpan("VP.Build").StartActive())
			{
				// Aggregated distance breakdown (cheap, no extra spans)
				Dist.Prof.Reset();

				var span = scope?.Span;
				span?.SetTag("vp.items.count", _items.Count);
				span?.SetTag("vp.seed", seed ?? 42);
				span?.SetTag("vp.parallel.dist.threshold", ParallelDistanceThreshold);
				span?.SetTag("vp.trace.maxDepth", TraceBuildMaxDepth);
				_root = Build(idxs, tracer, stats);

				// Сводка по билду (все в одном спане, чтобы было легко сравнивать запуски)
				span?.SetTag("vp.build.nodes", stats.Nodes);
				span?.SetTag("vp.build.maxDepth", stats.MaxDepth);
				span?.SetTag("vp.build.maxN", stats.MaxN);
				span?.SetTag("vp.build.dist.calls", stats.DistanceCalls);
				span?.SetTag("vp.build.dist.ms", TicksToMs(stats.DistanceTicks));
				span?.SetTag("vp.build.dist.parallel.true.count", stats.DistanceParallelTrue);
				span?.SetTag("vp.build.dist.parallel.false.count", stats.DistanceParallelFalse);
				span?.SetTag("vp.build.quickselect.ms", TicksToMs(stats.QuickSelectTicks));
				span?.SetTag("vp.build.partition.ms", TicksToMs(stats.PartitionTicks));
				span?.SetTag("vp.build.vpselect.ms", TicksToMs(stats.VpSelectTicks));
				span?.SetTag("vp.build.recurse.ms", TicksToMs(stats.RecurseTicks));
				span?.SetTag("vp.build.degenerateSplits", stats.DegenerateSplits);
				span?.SetTag("vp.build.muIsOneSplits", stats.MuIsOneSplits);
				span?.SetTag("vp.build.medianEq.total", stats.MedianEqTotal);
				span?.SetTag("vp.build.medianEq.max", stats.MedianEqMax);

				var d = Dist.Prof.Snapshot();
				span?.SetTag("dist.calls", d.calls);
				span?.SetTag("dist.calls.lev", d.callsLev);
				span?.SetTag("dist.calls.args", d.callsArgs);
				span?.SetTag("dist.calls.jacc", d.callsJacc);
				span?.SetTag("dist.ms.total", Dist.Prof.TickToMs(d.ticksTotal));
				span?.SetTag("dist.ms.name", Dist.Prof.TickToMs(d.ticksName));
				span?.SetTag("dist.ms.args", Dist.Prof.TickToMs(d.ticksArgs));
				span?.SetTag("dist.ms.ret", Dist.Prof.TickToMs(d.ticksRet));
				span?.SetTag("dist.ms.recv", Dist.Prof.TickToMs(d.ticksRecv));
				span?.SetTag("dist.ms.neigh", Dist.Prof.TickToMs(d.ticksNeigh));
				span?.SetTag("dist.lev.chars.total", d.charsLev);
				span?.SetTag("dist.args.pairs.total", d.argsPairs);
				span?.SetTag("dist.neigh.iter.min.total", d.neighMinIter);
				span?.SetTag("dist.args.n.max", d.argsNMax);
				span?.SetTag("dist.args.n.gt10.count", d.argsNGt10);
				span?.SetTag("dist.args.n.hist", Dist.Prof.GetArgsNHistogramString());
			}
		}

		private readonly object _rngLock = new object();

		private static double TicksToMs(long ticks)
		{
			// Stopwatch.Frequency ticks/sec
			return ticks <= 0 ? 0.0 : (ticks * 1000.0) / Stopwatch.Frequency;
		}

		private Node Build(int[] idxs, ITracer tracer, BuildStats stats)
		{
			return BuildInternal(idxs, start: 0, len: idxs.Length, depth: 0, parallelDepth: 0, tracer: tracer, stats: stats);
		}

		/// <summary>
		/// Построение на одном массиве индексов с передачей диапазонов.
		/// Это убирает два самых дорогих источника overhead на больших данных:
		///  1) создание List&lt;int&gt; left/right на каждом узле
		///  2) массовые аллокации double[] на каждом узле (берём из ArrayPool)
		/// </summary>
		private Node BuildInternal(int[] idxs, int start, int len, int depth, int parallelDepth, ITracer tracer, BuildStats stats)
		{
			if (len <= 0)
				return null;

			stats?.ObserveNode(depth, len);

			// Ограничиваем количество спанов: только верхние уровни.
			IScope nodeScope = null;
			if (tracer != null && depth <= TraceBuildMaxDepth)
			{
				nodeScope = tracer.BuildSpan("VP.BuildNode")
					.WithTag("vp.depth", depth)
					.WithTag("vp.n", len)
					.StartActive();
			}

			double[] dists = null;
			int n = 0;
			try
			{
				var node = new Node();

				var sw = Stopwatch.StartNew();
				int vpPos;
				lock (_rngLock)                       // Random не потокобезопасен
					vpPos = _rng.Next(len);
				sw.Stop();
				stats?.AddVpSelect(sw.ElapsedTicks);

				int vpIndex = idxs[start + vpPos];
				node.Index = vpIndex;
				nodeScope?.Span?.SetTag("vp.vpIndex", vpIndex);

				if (len == 1)
				{
					node.Left = null;
					node.Right = null;
					node.Threshold = 0;
					return node;
				}

				// move vp to end of this segment
				int last = start + len - 1;
				int vpAbs = start + vpPos;
				int t = idxs[vpAbs]; idxs[vpAbs] = idxs[last]; idxs[last] = t;

				n = len - 1;
				dists = ArrayPool<double>.Shared.Rent(n);

				bool usedParallel = false;
				var distSw = Stopwatch.StartNew();
				{
					var vp = _items[vpIndex];
					var items = _items;
					var dist = _dist;

					if (n >= ParallelDistanceThreshold)
					{
						usedParallel = true;
						var opts = new ParallelOptions
						{
							// Cap DOP for better stability on small/medium workloads.
							MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 8))
						};

						const int chunkSize = 128;
						Parallel.ForEach(Partitioner.Create(0, n, chunkSize), opts, range =>
						{
							for (int i = range.Item1; i < range.Item2; i++)
								dists[i] = dist(vp, items[idxs[start + i]]);
						});
					}
					else
					{
						for (int i = 0; i < n; i++)
							dists[i] = dist(vp, items[idxs[start + i]]);
					}
				}
				distSw.Stop();
				nodeScope?.Span?.SetTag("vp.dist.parallel", usedParallel);
				nodeScope?.Span?.SetTag("vp.dist.n", n);
				nodeScope?.Span?.SetTag("vp.dist.ms", TicksToMs(distSw.ElapsedTicks));
				stats?.AddDistance(n, distSw.ElapsedTicks);
				stats?.AddDistanceParallel(usedParallel);// median via quickselect (average O(n))
									 // IMPORTANT: keep dists[i] paired with idxs[start+i]. We select median in-place on both arrays.
				double mu;
				var qsSw = Stopwatch.StartNew();
				mu = QuickSelectMedianPairInPlace(dists, idxs, idxBase: start, len: n);
				qsSw.Stop();
				stats?.AddQuickSelect(qsSw.ElapsedTicks);
				nodeScope?.Span?.SetTag("vp.qs.ms", TicksToMs(qsSw.ElapsedTicks));

				node.Threshold = mu;

				// 3-way partition (<mu, ==mu, >mu) + choose a balanced split inside the ==mu band.
				var partSw = Stopwatch.StartNew();
				int targetLeft = n / 2;
				int lt, eq, gt;
				int leftLen = Partition3WayAndChooseLeft(dists, idxs, idxBase: start, n: n, mu: mu, targetLeft: targetLeft, out lt, out eq, out gt);
				int rightLen = n - leftLen;
				partSw.Stop();
				stats?.AddPartition(partSw.ElapsedTicks);
				stats?.AddDegenerateSplit(leftLen == 0 || rightLen == 0);
				stats?.AddMuIsOne(mu == 1.0);
				stats?.AddMedianEq(eq);

				nodeScope?.Span?.SetTag("vp.mu", mu);
				nodeScope?.Span?.SetTag("vp.split.targetLeft", targetLeft);
				nodeScope?.Span?.SetTag("vp.split.lt", lt);
				nodeScope?.Span?.SetTag("vp.split.eq", eq);
				nodeScope?.Span?.SetTag("vp.split.gt", gt);
				nodeScope?.Span?.SetTag("vp.partition.left", leftLen);
				nodeScope?.Span?.SetTag("vp.partition.right", rightLen);
				nodeScope?.Span?.SetTag("vp.partition.ms", TicksToMs(partSw.ElapsedTicks));


				if (parallelDepth > 0 && leftLen > 0 && rightLen > 0)
				{
					Node leftNode = null, rightNode = null;
					Parallel.Invoke(
						() => leftNode = BuildInternal(idxs, start, leftLen, depth + 1, parallelDepth - 1, tracer, stats),
						() => rightNode = BuildInternal(idxs, start + leftLen, rightLen, depth + 1, parallelDepth - 1, tracer, stats)
					);
					node.Left = leftNode;
					node.Right = rightNode;
				}
				else
				{
					node.Left = leftLen > 0 ? BuildInternal(idxs, start, leftLen, depth + 1, 0, tracer, stats) : null;
					node.Right = rightLen > 0 ? BuildInternal(idxs, start + leftLen, rightLen, depth + 1, 0, tracer, stats) : null;
				}
				/*recSw.Stop();
				stats?.AddRecurse(recSw.ElapsedTicks);
				nodeScope?.Span?.SetTag("vp.recurse.ms", TicksToMs(recSw.ElapsedTicks));*/

				return node;
			}
			finally
			{
				if (dists != null)
					ArrayPool<double>.Shared.Return(dists, clearArray: false);
				nodeScope?.Dispose();
			}
		}

		/// <summary>
		/// Median via QuickSelect, in-place (не копирует массив, чтобы не плодить GC на каждом узле).
		/// ВНИМАНИЕ: мутирует arr.
		/// </summary>
		private double QuickSelectMedianInPlace(double[] arr, int len)
		{
			if (len <= 0) return 0.0;
			int k = len / 2;
			return QuickSelect(arr, 0, len - 1, k);
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

		/// <summary>
		/// Median via QuickSelect, in-place, but keeps (dists[i] &lt;-&gt; idxs[idxBase+i]) pairing.
		/// We use the lower median for even len: k = (len-1)/2.
		/// </summary>
		private double QuickSelectMedianPairInPlace(double[] dists, int[] idxs, int idxBase, int len)
		{
			if (len <= 0) return 0.0;
			int k = (len - 1) / 2;
			return QuickSelectPair(dists, idxs, idxBase, 0, len - 1, k);
		}

		private double QuickSelectPair(double[] a, int[] idxs, int idxBase, int left, int right, int k)
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
						Swap(a, i, j);
						Swap(idxs, idxBase + i, idxBase + j);
						i++; j--;
					}
				}

				if (k <= j) right = j;
				else if (k >= i) left = i;
				else return a[k];
			}
		}

		/// <summary>
		/// 3-way partition around mu and choose a balanced split point.
		/// After partition:
		///   [0..lt)      : &lt; mu
		///   [lt..gtStart): == mu
		///   [gtStart..n) : &gt; mu
		/// We then pick leftLen inside the == band to keep sizes balanced, while preserving VP-tree invariants:
		///   Left distances &lt;= mu, Right distances &gt;= mu.
		/// </summary>
		private static int Partition3WayAndChooseLeft(
			double[] dists,
			int[] idxs,
			int idxBase,
			int n,
			double mu,
			int targetLeft,
			out int lt,
			out int eq,
			out int gt)
		{
			int l = 0;
			int i = 0;
			int r = n - 1;

			while (i <= r)
			{
				double v = dists[i];
				if (v < mu)
				{
					if (i != l)
					{
						Swap(dists, i, l);
						Swap(idxs, idxBase + i, idxBase + l);
					}
					i++; l++;
				}
				else if (v > mu)
				{
					if (i != r)
					{
						Swap(dists, i, r);
						Swap(idxs, idxBase + i, idxBase + r);
					}
					r--;
				}
				else
				{
					i++;
				}
			}

			lt = l;
			int gtStart = r + 1;
			eq = gtStart - l;
			gt = n - gtStart;

			// Choose a split point within the ==mu band to keep counts balanced,
			// but never move elements &lt; mu to the right or &gt; mu to the left.
			int leftLen = targetLeft;
			if (leftLen < lt) leftLen = lt;
			else if (leftLen > gtStart) leftLen = gtStart;

			// Avoid completely empty child (when possible)
			if (n > 1)
			{
				if (leftLen == 0) leftLen = 1;
				else if (leftLen == n) leftLen = n - 1;
			}

			return leftLen;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void Swap(double[] a, int i, int j)
		{
			double t = a[i]; a[i] = a[j]; a[j] = t;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void Swap(int[] a, int i, int j)
		{
			int t = a[i]; a[i] = a[j]; a[j] = t;
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
